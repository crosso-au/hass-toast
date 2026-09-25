using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HassToast.Agent.Tests;

/// <summary>
/// A minimal stand-in for the Home Assistant WebSocket API, faithful enough to exercise the
/// real handshake: auth_required, auth / auth_ok or auth_invalid, subscribe_events, ping/pong
/// and event delivery. Using a real socket rather than a mock means the client's framing,
/// message ordering and JSON shape are all genuinely under test.
/// </summary>
public sealed class FakeHomeAssistant : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _expectedToken;
    private readonly TaskCompletionSource _subscribed =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WebSocket? _socket;
    private Task? _acceptLoop;
    private int _connectionCount;

    public FakeHomeAssistant(string expectedToken = "good-token")
    {
        _expectedToken = expectedToken;
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}";

    /// <summary>Completes once the client has issued subscribe_events.</summary>
    public Task Subscribed => _subscribed.Task;

    /// <summary>Event types the client subscribed to.</summary>
    public List<string> Subscriptions { get; } = [];

    /// <summary>Number of pings the client has sent.</summary>
    public int PingCount { get; private set; }

    /// <summary>How many times a client has completed the WebSocket upgrade.</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    /// <summary>
    /// Serves connections one after another, so a client that reconnects after a drop is
    /// actually served rather than left hanging on an unaccepted TCP connection.
    /// </summary>
    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
                _socket = wsContext.WebSocket;
                Interlocked.Increment(ref _connectionCount);
                await ServeAsync(_socket, _cts.Token);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return; // Shutting down.
            }
            catch (HttpListenerException)
            {
                return; // Listener stopped.
            }
            catch (WebSocketException)
            {
                // This session died; go back and wait for the client to reconnect.
            }
        }
    }

    private async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        await SendAsync(socket, new { type = "auth_required", ha_version = "2026.8.0" }, ct);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveAsync(socket, ct);
            if (message is null) return;

            using var doc = message;
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "auth":
                    var token = root.GetProperty("access_token").GetString();
                    if (token == _expectedToken)
                        await SendAsync(socket, new { type = "auth_ok", ha_version = "2026.8.0" }, ct);
                    else
                        await SendAsync(socket, new { type = "auth_invalid", message = "Invalid access token or password" }, ct);
                    break;

                case "subscribe_events":
                    var id = root.GetProperty("id").GetInt32();
                    Subscriptions.Add(root.TryGetProperty("event_type", out var et)
                        ? et.GetString() ?? "" : "");
                    await SendAsync(socket, new { id, type = "result", success = true, result = (object?)null }, ct);
                    _subscribed.TrySetResult();
                    break;

                case "ping":
                    PingCount++;
                    await SendAsync(socket, new { id = root.GetProperty("id").GetInt32(), type = "pong" }, ct);
                    break;
            }
        }
    }

    /// <summary>Pushes a bus event to the connected client.</summary>
    public async Task FireEventAsync(string eventType, object data)
    {
        if (_socket is not { State: WebSocketState.Open })
            throw new InvalidOperationException("No client is connected.");

        await SendAsync(_socket, new
        {
            id = 1,
            type = "event",
            @event = new
            {
                event_type = eventType,
                data,
                origin = "LOCAL",
                time_fired = DateTimeOffset.UtcNow.ToString("o"),
            },
        }, _cts.Token);
    }

    /// <summary>
    /// Kills the connection abruptly, modelling a dead link — a Home Assistant restart or a
    /// network blip — rather than a negotiated shutdown. Abort rather than CloseAsync: the
    /// latter waits for the peer's closing handshake, which is not something a dropped
    /// connection ever supplies.
    /// </summary>
    public void DropConnection() => _socket?.Abort();

    /// <summary>Closes the connection politely, modelling a graceful server shutdown.</summary>
    public async Task CloseGracefullyAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _socket.CloseOutputAsync(
                WebSocketCloseStatus.EndpointUnavailable, "server going away", timeout.Token);
        }
    }

    private static async Task SendAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload);
        await socket.SendAsync(json, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<JsonDocument?> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        buffer.Position = 0;
        return JsonDocument.Parse(buffer);
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); } catch { /* already stopped */ }
        _listener.Close();
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* best effort */ }
        }
        _socket?.Dispose();
        _cts.Dispose();
    }
}
