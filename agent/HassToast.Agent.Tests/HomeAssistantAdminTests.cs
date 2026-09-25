using HassToast.Agent.Config;
using HassToast.Agent.Transport;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// The setup wizard's conversation with Home Assistant, against a real listener.
/// <para>
/// These exist because the wizard's whole value is that a person does not have to carry a signing
/// key between two machines by hand — and every step of that automation is a protocol detail that
/// fails silently if it is wrong: a flow submitted in one POST instead of two, a websocket result
/// matched by arrival order instead of by id, a HACS download asked for before the repository is
/// listed.
/// </para>
/// </summary>
public sealed class HomeAssistantAdminTests
{
    private static HomeAssistantConfig ConfigFor(FakeHomeAssistantAdmin server)
        => new() { Url = server.BaseUrl };

    [Fact]
    public async Task Probe_reports_version_and_administrator_status()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");
        var probe = await admin.ProbeAsync(CancellationToken.None);

        Assert.True(probe.Reachable);
        Assert.True(probe.TokenAccepted);
        Assert.True(probe.IsAdmin);
        Assert.True(probe.Usable);
        Assert.Equal("2025.8.0", probe.Version);
        Assert.Equal("Test User", probe.UserName);
    }

    [Fact]
    public async Task Probe_rejects_a_bad_token_without_claiming_the_server_is_unreachable()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "wrong-token");
        var probe = await admin.ProbeAsync(CancellationToken.None);

        // The distinction matters to whoever is reading the wizard: a wrong address and a wrong
        // token are fixed in different boxes.
        Assert.True(probe.Reachable);
        Assert.False(probe.TokenAccepted);
        Assert.False(probe.Usable);
    }

    [Fact]
    public async Task A_non_administrator_token_is_not_usable()
    {
        await using var server = new FakeHomeAssistantAdmin { IsAdmin = false };
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");
        var probe = await admin.ProbeAsync(CancellationToken.None);

        // Home Assistant requires admin for both config flows and HACS, so catching this at the
        // first step is the difference between one clear message and three confusing ones.
        Assert.True(probe.TokenAccepted);
        Assert.False(probe.IsAdmin);
        Assert.False(probe.Usable);
        Assert.NotNull(probe.Error);
    }

    [Fact]
    public async Task Integration_presence_follows_the_flow_handler_list()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        Assert.False(await admin.IsIntegrationInstalledAsync(CancellationToken.None));

        server.IntegrationInstalled = true;
        Assert.True(await admin.IsIntegrationInstalledAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Hacs_is_reported_absent_when_the_command_is_unknown()
    {
        await using var server = new FakeHomeAssistantAdmin { HacsInstalled = false };
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");
        var status = await admin.ProbeHacsAsync(CancellationToken.None);

        Assert.False(status.Installed);
    }

    [Fact]
    public async Task Creating_an_entry_delivers_the_device_id_and_key_over_the_flow_api()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        var result = await admin.CreateEntryAsync(
            "ryan-desktop", "c2VjcmV0", CancellationToken.None);

        Assert.True(result.Created);
        Assert.NotNull(result.EntryId);
        Assert.Equal("ryan-desktop", server.SubmittedDeviceId);

        // The point of the whole exercise: the key reached Home Assistant without a human
        // copying it anywhere.
        Assert.Equal("c2VjcmV0", server.SubmittedSigningKey);
    }

    [Fact]
    public async Task A_duplicate_device_id_is_reported_as_already_configured_rather_than_as_a_failure()
    {
        await using var server = new FakeHomeAssistantAdmin { AbortAsAlreadyConfigured = true };
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        var result = await admin.CreateEntryAsync(
            "ryan-desktop", "c2VjcmV0", CancellationToken.None);

        // Re-running the wizard over an existing install must be a no-op, not an error page.
        Assert.False(result.Created);
        Assert.True(result.AlreadyConfigured);
    }

    [Fact]
    public async Task An_entry_can_be_found_by_device_id_and_deleted()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        var otherMachine = server.AddEntry("hass_toast", "htpc");
        var thisMachine = server.AddEntry("hass_toast", "ryan-desktop");
        server.AddEntry("hue", "Hue Bridge");

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        var found = await admin.FindEntryAsync("ryan-desktop", CancellationToken.None);
        Assert.NotNull(found);
        Assert.Equal(thisMachine, found.EntryId);

        Assert.True(await admin.DeleteEntryAsync(found.EntryId, CancellationToken.None));

        // Uninstalling one machine must not touch another's entry, which is the whole reason the
        // option is scoped to a single device rather than to the integration.
        Assert.Equal([thisMachine], server.DeletedEntryIds);
        Assert.NotNull(await admin.FindEntryAsync("htpc", CancellationToken.None));
        Assert.Equal(otherMachine, (await admin.FindEntryAsync("htpc", CancellationToken.None))!.EntryId);
    }

    [Fact]
    public async Task Installing_through_hacs_adds_then_finds_then_downloads()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        await admin.InstallViaHacsAsync("crosso-au/hass-toast", null, CancellationToken.None);

        Assert.Equal("crosso-au/hass-toast", server.AddedRepository);

        // The id has to come from the listing rather than be assumed: HACS resolves the
        // repository against GitHub after the add returns, so it does not exist yet at that point.
        Assert.Equal("1296666", server.DownloadedRepositoryId);

        Assert.Equal(
            ["hacs/repositories/add", "hacs/repositories/list", "hacs/repository/download"],
            server.HacsCommands);
    }

    [Fact]
    public async Task Restart_waits_for_the_instance_to_answer_again()
    {
        await using var server = new FakeHomeAssistantAdmin();
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        // The fake never stops answering, which exercises the branch that matters most in
        // practice: an instance that restarts faster than the poll interval must still be
        // reported as back, not waited on until the timeout.
        var back = await admin.RestartAndWaitAsync(
            TimeSpan.FromSeconds(45), null, CancellationToken.None,
            shutdownGrace: TimeSpan.FromSeconds(2));

        Assert.True(back);
        Assert.Equal(1, server.RestartCount);
    }

    [Fact]
    public async Task Waits_for_startup_to_finish_before_reporting_running()
    {
        await using var server = new FakeHomeAssistantAdmin { StartingPolls = 2 };
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        var lines = new List<string>();
        var running = await admin.WaitUntilRunningAsync(
            TimeSpan.FromSeconds(30), new SynchronousProgress(lines.Add), CancellationToken.None);

        Assert.True(running);
        Assert.Contains(lines, l => l.Contains("finish starting"));
    }

    [Fact]
    public async Task Gives_up_on_an_instance_that_never_finishes_starting()
    {
        await using var server = new FakeHomeAssistantAdmin { StartingPolls = int.MaxValue };
        server.Start();

        using var admin = new HomeAssistantAdmin(ConfigFor(server), "good-token");

        var running = await admin.WaitUntilRunningAsync(
            TimeSpan.FromSeconds(3), null, CancellationToken.None);

        Assert.False(running);
    }

    /// <summary><see cref="Progress{T}"/> posts to the thread pool; tests want the lines now.</summary>
    private sealed class SynchronousProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
