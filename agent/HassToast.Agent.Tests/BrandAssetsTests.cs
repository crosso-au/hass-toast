using System.Drawing;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// The embedded product mark.
/// <para>
/// Worth testing because every way this breaks is silent. The resources are matched by the
/// <c>LogicalName</c> strings in the csproj, so a rename, a moved assets folder or a dropped
/// ItemGroup all compile perfectly and simply return null at runtime — where the failure is a
/// toast with no icon, or a blank square in the notification area, on someone else's machine.
/// </para>
/// </summary>
public sealed class BrandAssetsTests
{
    [Fact]
    public void The_mark_is_embedded_as_a_256px_png()
    {
        var bytes = BrandAssets.IconPng();

        Assert.NotEmpty(bytes);

        // Windows rejects an app logo over 200 KB, and this one is written out as exactly that.
        Assert.InRange(bytes.Length, 1, 200 * 1024);

        using var stream = new MemoryStream(bytes);
        using var image = Image.FromStream(stream);

        Assert.Equal(256, image.Width);
        Assert.Equal(256, image.Height);
    }

    /// <summary>
    /// The sizes the notification area asks for across the scaling factors: 16 at 100%, 24 at
    /// 150%, 32 at 200%. A frame missing at any of them is the difference between a crisp mark
    /// and a rescaled smear, and it fails invisibly — <see cref="Icon"/> substitutes the nearest
    /// frame rather than complaining.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(40)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    public void The_icon_carries_a_frame_at_every_size_the_tray_asks_for(int size)
    {
        using var icon = BrandAssets.LoadIcon(size);

        Assert.NotNull(icon);

        // Exact, precisely because substitution is silent: anything else means the frame is not
        // in the file and something is being stretched.
        Assert.Equal(size, icon.Width);
        Assert.Equal(size, icon.Height);
    }

    /// <summary>
    /// Pins the one size that does not round-trip, so nobody reaches for it later and quietly
    /// ships a stretched 128.
    /// <para>
    /// The 256 frame is PNG-compressed, as the format has required since Vista at that size, and
    /// GDI+ cannot read a PNG frame — it silently falls back to the largest one it can. This is
    /// only a limitation of <see cref="Icon"/>. The shell reads the .ico file directly for
    /// <c>ApplicationIcon</c>, where the 256 frame is exactly what Explorer's large views want.
    /// </para>
    /// </summary>
    [Fact]
    public void The_256px_frame_is_for_the_shell_not_for_GDI()
    {
        using var icon = BrandAssets.LoadIcon(256);

        Assert.NotNull(icon);
        Assert.Equal(128, icon.Width);
    }
}
