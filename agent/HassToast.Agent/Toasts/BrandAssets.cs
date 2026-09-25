using System.Drawing;
using System.Reflection;

namespace HassToast.Agent.Toasts;

/// <summary>
/// The product mark, embedded in the executable by the csproj.
/// <para>
/// Both forms are carried rather than one derived from the other. The .ico holds a
/// Lanczos-downscaled frame at every size the shell asks for, which is the whole reason the
/// format exists — rescaling a 256 px PNG at runtime with GDI+ would throw that away at exactly
/// the small sizes where it shows. The PNG is what gets written to disk for the shell to read
/// back, a job an .ico cannot do: Windows wants a PNG for a toast's app icon.
/// </para>
/// </summary>
internal static class BrandAssets
{
    private const string IconResource = "HassToast.Brand.icon.ico";
    private const string PngResource = "HassToast.Brand.icon-256.png";

    private static byte[]? _png;

    /// <summary>
    /// The 256 px mark as PNG bytes, for writing where the shell can read it.
    /// Empty if the resource is missing, which callers treat as "no icon".
    /// </summary>
    public static byte[] IconPng() => _png ??= Read(PngResource);

    /// <summary>
    /// The mark at <paramref name="size"/>, taken from the nearest frame in the .ico rather than
    /// rescaled. Null when the resource is missing or unreadable — the caller draws a fallback.
    /// </summary>
    public static Icon? LoadIcon(int size)
    {
        try
        {
            using var stream = Open(IconResource);
            if (stream is null) return null;

            return new Icon(stream, size, size);
        }
        catch (Exception)
        {
            // A missing or malformed icon must not be the reason the agent fails to start.
            return null;
        }
    }

    private static byte[] Read(string name)
    {
        try
        {
            using var stream = Open(name);
            if (stream is null) return [];

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static Stream? Open(string name) =>
        Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
}
