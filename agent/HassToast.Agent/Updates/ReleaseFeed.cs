using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace HassToast.Agent.Updates;

/// <summary>A published release this agent could update to.</summary>
public sealed record AvailableRelease(
    Version Version,
    string VersionText,
    string PageUrl,
    string InstallerName,
    Uri InstallerUrl,
    long InstallerSize,
    Uri SignatureUrl);

/// <summary>Thrown when a downloaded installer fails its signature check.</summary>
public sealed class UpdateRejectedException(string message) : Exception(message);

/// <summary>
/// Reads the latest release from GitHub and fetches its installer.
/// <para>
/// GitHub's <c>releases/latest</c> already leaves out drafts and pre-releases, so a release can be
/// staged as a draft with its files attached and no agent sees it until it is published. A release
/// published without its installer and signature is treated as no update at all, rather than as an
/// update that then fails.
/// </para>
/// </summary>
public sealed class ReleaseFeed
{
    public const string Repository = "crosso-au/hass-toast";

    public static readonly Uri LatestReleaseUri = new($"https://api.github.com/repos/{Repository}/releases/latest");

    /// <summary>Downloads are only taken from this repository's release files.</summary>
    private const string DownloadPathPrefix = $"/{Repository}/releases/download/";

    private const string PagePrefix = $"https://github.com/{Repository}/releases/";

    /// <summary>The installer is around 130 MB. Anything far larger is not ours.</summary>
    internal const long MaxInstallerBytes = 300L * 1024 * 1024;

    private readonly HttpClient _http;

    public ReleaseFeed(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        // GitHub's API refuses requests without a User-Agent.
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("HassToast-Agent", CurrentVersion.ToString(3)));
    }

    /// <summary>The version of this agent.</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        var text = typeof(ReleaseFeed).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0];

        return ParseVersion(text) ?? new Version(0, 0, 0);
    }

    /// <summary>
    /// The latest published release, or null when there is none this agent can use. Network and
    /// HTTP failures throw: "could not check" and "nothing new" are different answers.
    /// </summary>
    public async Task<AvailableRelease?> GetLatestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, ct);

        // No published release yet.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;

        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(ct), RuntimeInformation.ProcessArchitecture);
    }

    /// <summary>Picks this machine's installer and its signature out of a release.</summary>
    internal static AvailableRelease? Parse(string json, Architecture architecture)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (Bool(root, "draft") || Bool(root, "prerelease")) return null;

        var tag = Text(root, "tag_name");
        var version = ParseVersion(tag);
        if (version is null) return null;

        var versionText = version.ToString(3);
        var rid = architecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        var installerName = $"HassToast.Setup-{versionText}-{rid}.exe";
        var signatureName = installerName + UpdateSignature.SignatureSuffix;

        (Uri Url, long Size)? installer = null;
        Uri? signature = null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                var url = DownloadUrl(Text(asset, "browser_download_url"));
                if (url is null) continue;

                if (name == installerName)
                    installer = (url, asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : 0);
                else if (name == signatureName)
                    signature = url;
            }
        }

        if (installer is null || signature is null) return null;

        var page = Text(root, "html_url");
        if (page is null || !page.StartsWith(PagePrefix, StringComparison.Ordinal))
            page = PagePrefix + "latest";

        return new AvailableRelease(
            version, versionText, page, installerName, installer.Value.Url, installer.Value.Size, signature);
    }

    /// <summary>
    /// Downloads the installer into <paramref name="directory"/> and checks its signature. Returns
    /// the path of a file that has passed; anything that does not is deleted, never left to run.
    /// </summary>
    public async Task<string> DownloadVerifiedAsync(AvailableRelease release, string directory, CancellationToken ct)
    {
        if (release.InstallerSize > MaxInstallerBytes)
            throw new UpdateRejectedException($"The installer is larger than expected ({release.InstallerSize} bytes).");

        var signatureText = await _http.GetStringAsync(release.SignatureUrl, ct);

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, release.InstallerName);
        var verified = false;

        try
        {
            byte[] hash;
            using (var response = await _http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
                using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxInstallerBytes)
                        throw new UpdateRejectedException("The installer is larger than expected.");

                    sha256.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }

                hash = sha256.GetHashAndReset();
            }

            if (!UpdateSignature.Verify(release.VersionText, release.InstallerName, hash, signatureText))
                throw new UpdateRejectedException($"The signature on {release.InstallerName} does not verify.");

            verified = true;
            return path;
        }
        finally
        {
            if (!verified)
            {
                try { File.Delete(path); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>"v2.1.2" or "2.1.2" to a three-part version. Anything else, including pre-release tags, is null.</summary>
    internal static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(trimmed, out var parsed)) return null;

        return new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
    }

    /// <summary>Accepts only HTTPS links to this repository's release files.</summary>
    private static Uri? DownloadUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return null;
        if (!uri.AbsolutePath.StartsWith(DownloadPathPrefix, StringComparison.Ordinal)) return null;
        return uri;
    }

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
