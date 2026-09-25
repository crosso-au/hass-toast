using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class ContentPolicyTests
{
    private static ContentPolicy Policy(Action<SecurityConfig>? tweak = null)
    {
        var config = new SecurityConfig();
        tweak?.Invoke(config);
        return new ContentPolicy(config);
    }

    [Theory]
    [InlineData("https://example.com/build/42")]
    [InlineData("http://192.168.1.10:8123/lovelace")]
    [InlineData("mailto:someone@example.com")]
    public void Allows_the_default_activation_schemes(string uri)
    {
        Assert.True(Policy().CheckActivationUri(uri).Allowed);
    }

    [Theory]
    // Each of these would hand the shell something a payload author should not control.
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("ms-settings:windowsupdate")]
    [InlineData("shell:startup")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("steam://run/12345")]
    [InlineData("\\\\evil-host\\share\\payload.exe")]
    public void Blocks_dangerous_activation_schemes_by_default(string uri)
    {
        var result = Policy().CheckActivationUri(uri);
        Assert.False(result.Allowed);
        Assert.NotNull(result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a uri at all")]
    [InlineData("/relative/path")]
    public void Rejects_activation_targets_that_are_not_absolute_uris(string uri)
    {
        Assert.False(Policy().CheckActivationUri(uri).Allowed);
    }

    [Fact]
    public void Activation_allowlist_can_be_widened_deliberately()
    {
        var policy = Policy(c => c.AllowedUriSchemes.Add("steam"));
        Assert.True(policy.CheckActivationUri("steam://run/12345").Allowed);
    }

    [Theory]
    [InlineData("https://example.com/a.png")]
    [InlineData("http://example.com/a.jpg")]
    public void Allows_the_default_image_schemes(string uri)
    {
        Assert.True(Policy().CheckImageSource(uri, out _).Allowed);
    }

    [Fact]
    public void Blocks_local_file_images_until_opted_in()
    {
        Assert.False(Policy().CheckImageSource("file:///C:/secret.png", out _).Allowed);

        var permissive = Policy(c => c.AllowedImageSchemes.Add("file"));
        Assert.True(permissive.CheckImageSource("file:///C:/secret.png", out _).Allowed);
    }

    [Fact]
    public void Blocks_unc_image_paths_even_when_file_is_allowed()
    {
        // A UNC fetch would push the user's credentials at an attacker-chosen host.
        var policy = Policy(c => c.AllowedImageSchemes.Add("file"));
        var result = policy.CheckImageSource("file://evil-host/share/a.png", out _);

        Assert.False(result.Allowed);
        Assert.Contains("UNC", result.Reason!);
    }

    [Fact]
    public void Logo_images_get_a_tighter_ceiling_than_inline_images()
    {
        // Mirrors what Windows itself enforces: ~200 KB for logo and hero, 3 MB inline.
        var policy = Policy();

        Assert.Equal(200 * 1024, policy.MaxBytesFor(ImageRole.AppLogo));
        Assert.Equal(200 * 1024, policy.MaxBytesFor(ImageRole.Hero));
        Assert.Equal(3 * 1024 * 1024, policy.MaxBytesFor(ImageRole.Inline));
    }

    [Fact]
    public void Rate_limiting_allows_a_burst_then_refuses()
    {
        var policy = Policy(c => c.MaxToastsPerMinute = 5);

        for (var i = 0; i < 5; i++)
            Assert.True(policy.TryConsumeToastBudget(), $"toast {i + 1} should have been allowed");

        Assert.False(policy.TryConsumeToastBudget());
    }

    [Fact]
    public void Rate_limit_refills_over_time()
    {
        var time = new FakeTimeProvider();
        var policy = new ContentPolicy(new SecurityConfig { MaxToastsPerMinute = 60 }, time);

        for (var i = 0; i < 60; i++) Assert.True(policy.TryConsumeToastBudget());
        Assert.False(policy.TryConsumeToastBudget());

        // 60/minute is one per second.
        time.Advance(TimeSpan.FromSeconds(3));

        Assert.True(policy.TryConsumeToastBudget());
        Assert.True(policy.TryConsumeToastBudget());
        Assert.True(policy.TryConsumeToastBudget());
        Assert.False(policy.TryConsumeToastBudget());
    }
}

public sealed class ImageSniffingTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, ".png")]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".jpg")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0, 0, 0, 0, 0 }, ".gif")]
    [InlineData(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ".bmp")]
    public void Identifies_images_by_magic_bytes(byte[] bytes, string expected)
    {
        Assert.Equal(expected, ImageResolver.SniffImageType(bytes));
    }

    [Fact]
    public void Identifies_webp_by_its_riff_container()
    {
        byte[] webp = [0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50];
        Assert.Equal(".webp", ImageResolver.SniffImageType(webp));
    }

    [Theory]
    // An executable served as image/png must not survive on the strength of its Content-Type.
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 })]
    // A shell script, and plain HTML.
    [InlineData(new byte[] { 0x23, 0x21, 0x2F, 0x62, 0x69, 0x6E, 0x2F, 0x73, 0x68, 0x0A, 0, 0 })]
    [InlineData(new byte[] { 0x3C, 0x21, 0x44, 0x4F, 0x43, 0x54, 0x59, 0x50, 0x45, 0x20, 0, 0 })]
    public void Rejects_content_that_is_not_an_image(byte[] bytes)
    {
        Assert.Null(ImageResolver.SniffImageType(bytes));
    }

    [Fact]
    public void Rejects_input_too_short_to_identify()
    {
        Assert.Null(ImageResolver.SniffImageType([0x89, 0x50]));
        Assert.Null(ImageResolver.SniffImageType([]));
    }
}

/// <summary>Manually advanced clock, so rate-limit refill can be tested without real waiting.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private long _ticks = 1_000_000;

    public override long GetTimestamp() => _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan by) => _ticks += by.Ticks;
}
