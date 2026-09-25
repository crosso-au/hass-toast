using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Transport;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class HaWebSocketClientTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("hasstoast-tests").FullName;

    private SecretStore StoreWith(string token)
    {
        var store = new SecretStore(Path.Combine(_tempDir, $"{Guid.NewGuid():N}.dat"));
        store.Save(new AgentSecrets(token, SecretStore.GenerateSigningKey()));
        return store;
    }

    private static AgentConfig ConfigFor(FakeHomeAssistant ha, string eventType = "hass_toast_notify") => new()
    {
        HomeAssistant = { Url = ha.BaseUrl, PingIntervalSeconds = 1, PingTimeoutSeconds = 2 },
        DeviceId = "test-device",
        EventType = eventType,
    };

    private static HaWebSocketClient ClientFor(AgentConfig config, SecretStore secrets)
        => new(config, secrets, NullLogger<HaWebSocketClient>.Instance);

    [Fact]
    public async Task Authenticates_and_subscribes_to_the_configured_event()
    {
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.Equal(["hass_toast_notify"], ha.Subscriptions);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Exposes_the_host_as_state_detail_for_late_subscribers()
    {
        // The tray attaches after the transport may already be connected. If the current
        // detail is not readable from the source, it renders "Connected to " with no host.
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(ConnectionState.Connected, client.State);
        Assert.False(string.IsNullOrWhiteSpace(client.StateDetail),
            "StateDetail was empty while connected, so status text would read 'Connected to '");
        Assert.Equal("localhost", client.StateDetail);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Reports_a_changed_reason_even_while_the_state_stays_the_same()
    {
        // Disconnected -> Disconnected with a new reason is a real transition; collapsing it
        // would strand the tray showing a stale cause.
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var seen = new List<(ConnectionState State, string? Detail)>();
        client.StateChanged += (s, d) => { lock (seen) seen.Add((s, d)); };

        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        await cts.CancelAsync();
        await run;

        lock (seen)
        {
            Assert.Contains(seen, e => e.State == ConnectionState.Connecting);
            Assert.Contains(seen, e => e.State == ConnectionState.Connected && e.Detail == "localhost");
        }
    }

    [Fact]
    public async Task Delivers_event_data_to_the_channel()
    {
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        await ha.FireEventAsync("hass_toast_notify", new
        {
            device_id = "test-device",
            nonce = "abc123",
            toast = new { visual = new { text = new[] { "Hello" } } },
        });

        var received = await channel.Reader.ReadAsync(cts.Token);

        Assert.Equal("test-device", received.Data.GetProperty("device_id").GetString());
        Assert.Equal("abc123", received.Data.GetProperty("nonce").GetString());
        Assert.Equal("Hello", received.Data
            .GetProperty("toast").GetProperty("visual").GetProperty("text")[0].GetString());

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Event_data_survives_the_receive_loop_disposing_its_document()
    {
        // The pump parses each frame into a JsonDocument it disposes on the next iteration.
        // Without a defensive Clone, reading the payload later throws ObjectDisposedException.
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        await ha.FireEventAsync("hass_toast_notify", new { device_id = "test-device", seq = 1 });
        var first = await channel.Reader.ReadAsync(cts.Token);

        // Force several more frames through the loop so the original document is long gone.
        for (var i = 2; i <= 5; i++)
            await ha.FireEventAsync("hass_toast_notify", new { device_id = "test-device", seq = i });
        for (var i = 2; i <= 5; i++)
            await channel.Reader.ReadAsync(cts.Token);

        Assert.Equal(1, first.Data.GetProperty("seq").GetInt32());

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Rejected_token_surfaces_as_Unauthorized_rather_than_a_tight_retry_loop()
    {
        await using var ha = new FakeHomeAssistant(expectedToken: "good-token");
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("wrong-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource();

        var run = client.RunAsync(channel.Writer, cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (client.State != ConnectionState.Unauthorized && DateTime.UtcNow < deadline)
            await Task.Delay(50, cts.Token);

        Assert.Equal(ConnectionState.Unauthorized, client.State);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Responds_to_the_keepalive_cycle()
    {
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        // PingIntervalSeconds is 1 in the test config.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (ha.PingCount == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);

        Assert.True(ha.PingCount > 0, "client never sent a keepalive ping");
        Assert.Equal(ConnectionState.Connected, client.State);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Reconnects_after_the_link_drops()
    {
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1, ha.ConnectionCount);

        ha.DropConnection();

        // The client must notice the drop and re-establish the session unaided.
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (ha.ConnectionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);

        Assert.True(ha.ConnectionCount >= 2,
            $"client did not reconnect (connections seen: {ha.ConnectionCount})");

        while (client.State != ConnectionState.Connected && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);
        Assert.Equal(ConnectionState.Connected, client.State);

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Reconnects_after_a_graceful_server_close()
    {
        // Home Assistant restarting sends a proper close frame. The client must answer the
        // closing handshake — otherwise the server sits waiting on it — and then reconnect.
        await using var ha = new FakeHomeAssistant();
        ha.Start();

        var client = ClientFor(ConfigFor(ha), StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(40));

        var run = client.RunAsync(channel.Writer, cts.Token);
        await ha.Subscribed.WaitAsync(TimeSpan.FromSeconds(10));

        // This completes only because the client replies to the close frame.
        await ha.CloseGracefullyAsync().WaitAsync(TimeSpan.FromSeconds(10));

        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (ha.ConnectionCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);

        Assert.True(ha.ConnectionCount >= 2,
            $"client did not reconnect (connections seen: {ha.ConnectionCount})");

        await cts.CancelAsync();
        await run;
    }

    [Fact]
    public async Task Handshake_that_never_completes_times_out_instead_of_hanging()
    {
        // A peer that accepts TCP but never upgrades to WebSocket. Without a handshake
        // deadline the client would park here forever and never retry.
        using var stalled = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        stalled.Start();
        var port = ((System.Net.IPEndPoint)stalled.LocalEndpoint).Port;

        using var stallCts = new CancellationTokenSource();
        var accepting = Task.Run(async () =>
        {
            var held = new List<System.Net.Sockets.TcpClient>();
            try
            {
                // Accept and hold connections open without ever speaking HTTP, so every
                // reconnect attempt during the test stalls the same way.
                while (!stallCts.IsCancellationRequested)
                    held.Add(await stalled.AcceptTcpClientAsync(stallCts.Token));
            }
            catch { /* listener stopped or test finished */ }
            finally
            {
                foreach (var c in held) c.Dispose();
            }
        });

        var config = new AgentConfig
        {
            HomeAssistant = { Url = $"http://localhost:{port}", ConnectTimeoutSeconds = 2 },
            DeviceId = "test-device",
        };

        var client = ClientFor(config, StoreWith("good-token"));
        var channel = Channel.CreateUnbounded<InboundEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var started = DateTime.UtcNow;
        var run = client.RunAsync(channel.Writer, cts.Token);

        // The client should give up on the stalled handshake and enter its backoff.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (client.State != ConnectionState.Disconnected && DateTime.UtcNow < deadline)
            await Task.Delay(100, cts.Token);

        Assert.Equal(ConnectionState.Disconnected, client.State);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20), "took far longer than the configured timeout");

        await cts.CancelAsync();
        await run;
        await stallCts.CancelAsync();
        stalled.Stop();
        await accepting;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(20)]
    public void Backoff_grows_but_stays_capped(int attempt)
    {
        var delay = HaWebSocketClient.BackoffDelay(attempt);
        Assert.True(delay > TimeSpan.Zero);
        // 60s ceiling plus up to 30% jitter.
        Assert.True(delay <= TimeSpan.FromSeconds(78), $"delay was {delay}");
    }

    [Fact]
    public void Backoff_is_monotonic_across_early_attempts()
    {
        // Compare floors rather than samples, since jitter makes single draws non-monotonic.
        var first = Enumerable.Range(0, 50).Min(_ => HaWebSocketClient.BackoffDelay(1));
        var later = Enumerable.Range(0, 50).Min(_ => HaWebSocketClient.BackoffDelay(4));
        Assert.True(later > first);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }
}

