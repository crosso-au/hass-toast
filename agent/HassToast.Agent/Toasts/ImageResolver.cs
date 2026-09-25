using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Config;
using HassToast.Agent.Security;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Turns a payload image source into a local file Windows can render.
/// <para>
/// Windows will happily load an <c>http(s)</c> image itself, but letting it do so hands an
/// untrusted payload the ability to make the machine issue arbitrary outbound requests, with no
/// size or type checking and no way to log what happened. Fetching here instead keeps the
/// allowlist, the size ceiling and the content sniffing under the agent's control.
/// </para>
/// </summary>
public sealed class ImageResolver
{
    private static readonly HttpClient Http = CreateClient();

    private readonly ContentPolicy _policy;
    private readonly ILogger<ImageResolver> _log;
    private readonly string _cacheDirectory;

    public ImageResolver(ContentPolicy policy, ILogger<ImageResolver> log, string? cacheDirectory = null)
    {
        _policy = policy;
        _log = log;
        _cacheDirectory = cacheDirectory ?? AgentConfig.ImageCacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(5),
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("hass-toast/2.0");
        return client;
    }

    /// <summary>
    /// Returns a local path for the image, or null if it was rejected or could not be fetched.
    /// A missing image degrades the toast; it must never fail the whole notification.
    /// </summary>
    public async Task<string?> ResolveAsync(string? source, ImageRole role, CancellationToken ct)
    {
        var verdict = _policy.CheckImageSource(source, out var uri);
        if (!verdict.Allowed || uri is null)
        {
            _log.LogWarning("Rejected image source: {Reason}", verdict.Reason);
            return null;
        }

        var maxBytes = _policy.MaxBytesFor(role);

        try
        {
            if (uri.IsFile)
            {
                var path = uri.LocalPath;
                if (!File.Exists(path))
                {
                    _log.LogWarning("Image file does not exist: {Path}", path);
                    return null;
                }

                var length = new FileInfo(path).Length;
                if (length > maxBytes)
                {
                    _log.LogWarning("Image {Path} is {Size} bytes, over the {Max} byte ceiling for {Role}.",
                        path, length, maxBytes, role);
                    return null;
                }

                return path;
            }

            return await DownloadAsync(uri, role, maxBytes, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Could not resolve image {Uri}: {Message}", uri, ex.Message);
            return null;
        }
    }

    private async Task<string?> DownloadAsync(Uri uri, ImageRole role, int maxBytes, CancellationToken ct)
    {
        // The extension is only known once the bytes have been sniffed, so the cached file is
        // the hash plus whatever that turned out to be. Probing the bare hash would never match
        // anything this method writes, and every image would be fetched again on every toast.
        var cached = CachePathFor(uri);
        if (FindCached(cached) is { } hit) return hit;

        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
        {
            _log.LogWarning("Image fetch for {Uri} returned {Status}.", uri, (int)response.StatusCode);
            return null;
        }

        // Trust the declared length only as an early reject; it is attacker-controlled, so the
        // real enforcement is the byte counting below.
        if (response.Content.Headers.ContentLength is { } declared && declared > maxBytes)
        {
            _log.LogWarning("Image {Uri} declares {Size} bytes, over the {Max} byte ceiling for {Role}.",
                uri, declared, maxBytes, role);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];

        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read == 0) break;

            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                _log.LogWarning("Image {Uri} exceeded the {Max} byte ceiling for {Role} mid-transfer.",
                    uri, maxBytes, role);
                return null;
            }
        }

        var bytes = buffer.ToArray();
        var extension = SniffImageType(bytes);
        if (extension is null)
        {
            // Content-Type is whatever the server claimed; the magic bytes are what Windows
            // will actually try to decode.
            _log.LogWarning("Image {Uri} is not a recognised image format; refusing to use it.", uri);
            return null;
        }

        var path = cached + extension;
        await File.WriteAllBytesAsync(path, bytes, ct);
        _log.LogDebug("Cached image {Uri} to {Path} ({Size} bytes).", uri, path, bytes.Length);
        return path;
    }

    private static ReadOnlySpan<byte> PngMagic => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Identifies the format from its magic bytes rather than the declared Content-Type.</summary>
    internal static string? SniffImageType(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return null;

        if (bytes[..8].SequenceEqual(PngMagic))
            return ".png";

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";

        if (bytes[..6].SequenceEqual("GIF87a"u8) || bytes[..6].SequenceEqual("GIF89a"u8))
            return ".gif";

        if (bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8))
            return ".webp";

        if (bytes[..2].SequenceEqual("BM"u8))
            return ".bmp";

        return null;
    }

    private string CachePathFor(Uri uri)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
        return Path.Combine(_cacheDirectory, hash[..32]);
    }

    /// <summary>The cached file for this hash whatever extension it was stored under, or null.</summary>
    private static string? FindCached(string basePath)
    {
        var directory = Path.GetDirectoryName(basePath);
        if (directory is null || !Directory.Exists(directory)) return null;

        return Directory.EnumerateFiles(directory, Path.GetFileName(basePath) + ".*").FirstOrDefault();
    }
}
