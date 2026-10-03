using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HassToast.Agent.Updates;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// Self-update: reading GitHub's release, deciding whether to offer it, and refusing any installer
/// whose signature does not hold. The signature tests matter most - they are the line between
/// "the GitHub account was compromised" and "every installed agent ran the attacker's file".
/// </summary>
public sealed class UpdateTests
{
    private const string Download = "https://github.com/crosso-au/hass-toast/releases/download/v2.1.3/";

    private static string ReleaseJson(
        string tag = "v2.1.3", bool draft = false, bool prerelease = false, string? downloadBase = null,
        bool withSignature = true)
    {
        var baseUrl = downloadBase ?? Download;
        var assets = new List<object>
        {
            new { name = "HassToast.Setup-2.1.3-win-x64.exe", browser_download_url = baseUrl + "HassToast.Setup-2.1.3-win-x64.exe", size = 1000 },
            new { name = "hass_toast-2.1.3.zip", browser_download_url = baseUrl + "hass_toast-2.1.3.zip", size = 10 },
        };
        if (withSignature)
            assets.Add(new { name = "HassToast.Setup-2.1.3-win-x64.exe.sig", browser_download_url = baseUrl + "HassToast.Setup-2.1.3-win-x64.exe.sig", size = 88 });

        return JsonSerializer.Serialize(new
        {
            tag_name = tag,
            draft,
            prerelease,
            html_url = "https://github.com/crosso-au/hass-toast/releases/tag/" + tag,
            assets,
        });
    }

    // ------------------------------------------------------------------ parsing

    [Fact]
    public void A_complete_release_is_parsed()
    {
        var release = ReleaseFeed.Parse(ReleaseJson(), Architecture.X64);

        Assert.NotNull(release);
        Assert.Equal(new Version(2, 1, 3), release.Version);
        Assert.Equal("2.1.3", release.VersionText);
        Assert.Equal("HassToast.Setup-2.1.3-win-x64.exe", release.InstallerName);
        Assert.EndsWith(".exe.sig", release.SignatureUrl.AbsolutePath);
        Assert.Equal("https://github.com/crosso-au/hass-toast/releases/tag/v2.1.3", release.PageUrl);
    }

    [Fact]
    public void A_release_without_its_signature_is_not_an_update()
        => Assert.Null(ReleaseFeed.Parse(ReleaseJson(withSignature: false), Architecture.X64));

    [Fact]
    public void A_release_without_this_architectures_installer_is_not_an_update()
        => Assert.Null(ReleaseFeed.Parse(ReleaseJson(), Architecture.Arm64));

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Drafts_and_prereleases_are_ignored(bool draft, bool prerelease)
        => Assert.Null(ReleaseFeed.Parse(ReleaseJson(draft: draft, prerelease: prerelease), Architecture.X64));

    [Theory]
    [InlineData("https://evil.example/crosso-au/hass-toast/releases/download/v2.1.3/")]
    [InlineData("http://github.com/crosso-au/hass-toast/releases/download/v2.1.3/")]
    [InlineData("https://github.com/someone-else/hass-toast/releases/download/v2.1.3/")]
    public void Downloads_are_only_taken_from_this_repository_over_https(string downloadBase)
        => Assert.Null(ReleaseFeed.Parse(ReleaseJson(downloadBase: downloadBase), Architecture.X64));

    [Theory]
    [InlineData("v2.1.3", "2.1.3")]
    [InlineData("2.1.3", "2.1.3")]
    [InlineData("v3.0", "3.0.0")]
    [InlineData("v2.1.3-beta", null)]
    [InlineData("latest", null)]
    [InlineData("", null)]
    public void Tags_parse_to_three_part_versions(string tag, string? expected)
        => Assert.Equal(expected, ReleaseFeed.ParseVersion(tag)?.ToString(3));

    // ------------------------------------------------------------- deciding

    private static readonly DateTimeOffset Now = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Version Installed = new(2, 1, 2);

    private static AvailableRelease Release(string version) => new(
        Version.Parse(version), version, "https://github.com/crosso-au/hass-toast/releases/latest",
        $"HassToast.Setup-{version}-win-x64.exe", new Uri(Download + "x.exe"), 1, new Uri(Download + "x.exe.sig"));

    [Fact]
    public void A_newer_release_is_offered()
        => Assert.True(new UpdateState().ShouldOffer(Release("2.1.3"), Installed, Now, manual: false));

    [Theory]
    [InlineData("2.1.2")]
    [InlineData("2.0.0")]
    public void The_same_or_an_older_release_is_never_offered(string version)
    {
        Assert.False(new UpdateState().ShouldOffer(Release(version), Installed, Now, manual: false));
        Assert.False(new UpdateState().ShouldOffer(Release(version), Installed, Now, manual: true));
    }

    [Fact]
    public void A_skipped_version_is_offered_only_when_asked_for()
    {
        var state = new UpdateState { SkippedVersion = "2.1.3" };

        Assert.False(state.ShouldOffer(Release("2.1.3"), Installed, Now, manual: false));
        Assert.True(state.ShouldOffer(Release("2.1.3"), Installed, Now, manual: true));
        Assert.True(state.ShouldOffer(Release("2.1.4"), Installed, Now, manual: false));
    }

    [Fact]
    public void Remind_me_tomorrow_holds_until_it_comes_due()
    {
        var state = new UpdateState { RemindAfterUtc = Now.AddHours(5) };

        Assert.False(state.ShouldOffer(Release("2.1.3"), Installed, Now, manual: false));
        Assert.True(state.ShouldOffer(Release("2.1.3"), Installed, Now, manual: true));
        Assert.True(state.ShouldOffer(Release("2.1.3"), Installed, Now.AddHours(5), manual: false));
    }

    [Fact]
    public void Checks_are_daily()
    {
        Assert.True(new UpdateState().IsCheckDue(Now));
        Assert.False(new UpdateState { LastCheckUtc = Now.AddHours(-23) }.IsCheckDue(Now));
        Assert.True(new UpdateState { LastCheckUtc = Now.AddHours(-24) }.IsCheckDue(Now));
    }

    [Fact]
    public void A_reminder_coming_due_brings_the_check_forward()
        => Assert.True(new UpdateState { LastCheckUtc = Now.AddHours(-2), RemindAfterUtc = Now.AddMinutes(-1) }.IsCheckDue(Now));

    [Fact]
    public void A_last_check_in_the_future_means_the_clock_moved()
        => Assert.True(new UpdateState { LastCheckUtc = Now.AddDays(3) }.IsCheckDue(Now));

    [Fact]
    public void Turning_automatic_checks_off_stops_them()
        => Assert.False(new UpdateState { CheckAutomatically = false, RemindAfterUtc = Now.AddDays(-1) }.IsCheckDue(Now));

    [Fact]
    public void State_round_trips_and_a_corrupt_file_falls_back_to_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"hass-toast-update-{Guid.NewGuid():N}.json");
        try
        {
            new UpdateState { CheckAutomatically = false, SkippedVersion = "2.1.3", LastCheckUtc = Now }.Save(path);
            var loaded = UpdateState.Load(path);
            Assert.False(loaded.CheckAutomatically);
            Assert.Equal("2.1.3", loaded.SkippedVersion);
            Assert.Equal(Now, loaded.LastCheckUtc);

            File.WriteAllText(path, "{ not json");
            Assert.True(UpdateState.Load(path).CheckAutomatically);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ------------------------------------------------------------ signatures

    private static (ECDsa Key, string PublicKey) NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (key, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    private static string Sign(ECDsa key, string version, string name, byte[] content)
        => UpdateSignature.Encode(key.SignData(
            UpdateSignature.BuildSigningInput(version, name, SHA256.HashData(content)), HashAlgorithmName.SHA256));

    private static readonly byte[] Installer = "pretend installer"u8.ToArray();
    private const string Name = "HassToast.Setup-2.1.3-win-x64.exe";

    [Fact]
    public void A_correct_signature_verifies()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var sig = Sign(key, "2.1.3", Name, Installer);
            Assert.True(UpdateSignature.Verify(publicKey, "2.1.3", Name, SHA256.HashData(Installer), sig));
        }
    }

    [Fact]
    public void Changed_bytes_do_not_verify()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var sig = Sign(key, "2.1.3", Name, Installer);
            Assert.False(UpdateSignature.Verify(publicKey, "2.1.3", Name, SHA256.HashData("something else"u8.ToArray()), sig));
        }
    }

    [Fact]
    public void An_old_signed_installer_cannot_be_passed_off_as_a_newer_version()
    {
        var (key, publicKey) = NewKey();
        using (key)
        {
            var sig = Sign(key, "2.1.0", "HassToast.Setup-2.1.0-win-x64.exe", Installer);
            Assert.False(UpdateSignature.Verify(publicKey, "2.1.3", Name, SHA256.HashData(Installer), sig));
            Assert.False(UpdateSignature.Verify(publicKey, "2.1.3", "HassToast.Setup-2.1.0-win-x64.exe", SHA256.HashData(Installer), sig));
        }
    }

    [Fact]
    public void Another_key_does_not_verify()
    {
        var (theirs, _) = NewKey();
        var (ours, ourPublicKey) = NewKey();
        using (theirs)
        using (ours)
        {
            var sig = Sign(theirs, "2.1.3", Name, Installer);
            Assert.False(UpdateSignature.Verify(ourPublicKey, "2.1.3", Name, SHA256.HashData(Installer), sig));

            // And nothing signed by an arbitrary key verifies against the key agents carry.
            Assert.False(UpdateSignature.Verify("2.1.3", Name, SHA256.HashData(Installer), sig));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64!")]
    [InlineData("AAAA")]
    public void Malformed_signatures_fail_rather_than_throw(string sig)
        => Assert.False(UpdateSignature.Verify("2.1.3", Name, SHA256.HashData(Installer), sig));

    [Fact]
    public void The_embedded_public_key_is_a_valid_p256_key()
    {
        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateSignature.PublicKey), out _);
        Assert.Equal(256, key.KeySize);
    }

    // -------------------------------------------------------------- download

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond(request));
    }

    private static ReleaseFeed Feed(byte[] installer, string signature) => new(new HttpClient(new StubHandler(request =>
        request.RequestUri!.AbsolutePath.EndsWith(".sig")
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(signature) }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(installer) })));

    [Fact]
    public async Task A_download_that_fails_its_signature_is_deleted_not_kept()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"hass-toast-dl-{Guid.NewGuid():N}");
        try
        {
            // Signed with a key that is not the agent's: exactly what a compromised release looks like.
            var (key, _) = NewKey();
            using (key)
            {
                var feed = Feed(Installer, Sign(key, "2.1.3", Name, Installer));
                var release = ReleaseFeed.Parse(ReleaseJson(), Architecture.X64)!;

                await Assert.ThrowsAsync<UpdateRejectedException>(
                    () => feed.DownloadVerifiedAsync(release, directory, CancellationToken.None));

                Assert.False(File.Exists(Path.Combine(directory, Name)));
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task No_published_release_is_not_an_error()
    {
        var feed = new ReleaseFeed(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        Assert.Null(await feed.GetLatestAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_check_throws_rather_than_reporting_up_to_date()
    {
        var feed = new ReleaseFeed(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden))));
        await Assert.ThrowsAsync<HttpRequestException>(() => feed.GetLatestAsync(CancellationToken.None));
    }
}
