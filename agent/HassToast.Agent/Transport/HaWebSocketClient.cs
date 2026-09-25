using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Config;
using HassToast.Agent.Security;

namespace HassToast.Agent.Transport;

/// <summary>
/// Maintains an outbound WebSocket connection to the Home Assistant API and forwards
/// subscribed bus events. Outbound-only by design: the desktop opens no listening port,
/// needs no forwarded port, and works from behind NAT.
/// </summary>
public sealed class HaWebSocketClient : IToastSource
{
    private static readonly JsonWriterOptions WriterOptions = new() { Indented = false };

    private readonly AgentConfig _config;
    private readonly SecretStore _secretStore;
    private readonly ILogger<HaWebSocketClient> _log;

    private ConnectionState _state = ConnectionState.Disconnected;
    private string? _stateDetail;

    public HaWebSocketClient(AgentConfig config, SecretStore secretStore, ILogger<HaWebSocketClient> log)
    {
        _config = config;
        _secretStore = secretStore;
        _log = log;
    }

    public ConnectionState State => _state;

    public string? StateDetail => _stateDetail;

    /// <summary>
    /// The live session, or null between connections. Assigned as a whole so a caller either
    /// gets a usable session or nothing, never one being torn down.
    /// </summary>
    private Session? _session;

    public async Task<bool> FireEventAsync(string eventType, object data, CancellationToken ct)
    {
        var session = _session;
        if (session is null || _state != ConnectionState.Connected)
        {
            _log.LogWarning(
                "Cannot send '{EventType}' - not connected to Home Assistant. The response is lost.",
                eventType);
            return false;
        }

        try
        {
            var json = JsonSerializer.SerializeToElement(data);

            await session.SendAsync(w =>
            {
                w.WriteNumber("id", session.NextId());
                w.WriteString("type", "fire_event");
                w.WriteString("event_type", eventType);
                w.WritePropertyName("event_data");
                json.WriteTo(w);
            }, ct);

            _log.LogInformation("Fired '{EventType}' on the Home Assistant bus.", eventType);
            return true;
        }
        catch (Exception ex)
        {
            // The link may have dropped between the check and the send.
            _log.LogWarning("Could not send '{EventType}': {Message}", eventType, ex.Message);
            return false;
        }
    }

    public event Action<ConnectionState, string?>? StateChanged;

    private void SetState(ConnectionState state, string? detail = null)
    {
        // Compare the detail too: staying Disconnected while the reason changes is a
        // transition worth reporting, not a no-op.
        if (_state == state && _stateDetail == detail) return;
        _state = state;
        _stateDetail = detail;
        StateChanged?.Invoke(state, detail);
    }

    public async Task RunAsync(ChannelWriter<InboundEvent> sink, CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ConnectAndPumpAsync(sink, ct);
                attempt = 0; // A clean session resets backoff.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (AuthenticationRejectedException ex)
            {
                // Retrying immediately just spams a rejection. Back off hard and say why.
                SetState(ConnectionState.Unauthorized, ex.Message);
                _log.LogError("Home Assistant rejected the access token. Re-run with --setup to " +
                              "store a new long-lived token. Retrying in 5 minutes.");
                await DelaySafe(TimeSpan.FromMinutes(5), ct);
                continue;
            }
            catch (Exception ex)
            {
                SetState(ConnectionState.Disconnected, ex.Message);
                _log.LogWarning("Connection to Home Assistant failed: {Message}", ex.Message);
            }

            if (ct.IsCancellationRequested) break;

            var delay = BackoffDelay(++attempt);
            _log.LogInformation("Reconnecting in {Seconds:F0}s (attempt {Attempt}).",
                delay.TotalSeconds, attempt);
            await DelaySafe(delay, ct);
        }

        SetState(ConnectionState.Disconnected, "stopped");
    }

    /// <summary>Exponential backoff capped at one minute, with jitter to avoid lockstep retries.</summary>
    internal static TimeSpan BackoffDelay(int attempt)
    {
        var seconds = Math.Min(60, Math.Pow(2, Math.Min(attempt, 6)));
        var jitter = Random.Shared.NextDouble() * 0.3 * seconds;
        return TimeSpan.FromSeconds(seconds + jitter);
    }

    private static async Task DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    private async Task ConnectAndPumpAsync(ChannelWriter<InboundEvent> sink, CancellationToken ct)
    {
        var uri = _config.HomeAssistant.WebSocketUri();
        SetState(ConnectionState.Connecting, uri.ToString());
        _log.LogInformation("Connecting to {Uri}", uri);

        using var ws = new ClientWebSocket();
        ConfigureTls(ws);

        using var session = new Session(ws, _log);
        int subscriptionId;

        // Bound the whole connect-and-handshake phase. A peer that accepts TCP but never
        // upgrades, or never answers auth, must fall through to the reconnect path rather
        // than parking here forever.
        var timeout = TimeSpan.FromSeconds(_config.HomeAssistant.ConnectTimeoutSeconds);
        using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            handshakeCts.CancelAfter(timeout);
            var handshakeToken = handshakeCts.Token;

            try
            {
                await ws.ConnectAsync(uri, handshakeToken);

                // 1. Server greets with auth_required.
                using (var greeting = await session.ReceiveAsync(handshakeToken))
                {
                    var greetingType = greeting.RootElement.GetProperty("type").GetString();
                    if (greetingType != "auth_required")
                        throw new InvalidOperationException($"Expected 'auth_required', got '{greetingType}'.");
                }

                // 2. Authenticate with the long-lived access token.
                SetState(ConnectionState.Authenticating);
                var secrets = _secretStore.Load();
                await session.SendAsync(w =>
                {
                    w.WriteString("type", "auth");
                    w.WriteString("access_token", secrets.AccessToken);
                }, handshakeToken);

                using (var authResult = await session.ReceiveAsync(handshakeToken))
                {
                    var type = authResult.RootElement.GetProperty("type").GetString();
                    if (type == "auth_invalid")
                    {
                        var message = authResult.RootElement.TryGetProperty("message", out var m)
                            ? m.GetString() : "invalid access token";
                        throw new AuthenticationRejectedException(message ?? "invalid access token");
                    }
                    if (type != "auth_ok")
                        throw new InvalidOperationException($"Expected 'auth_ok', got '{type}'.");
                }

                // 3. Subscribe to the agent's bus event.
                subscriptionId = session.NextId();
                await session.SendAsync(w =>
                {
                    w.WriteNumber("id", subscriptionId);
                    w.WriteString("type", "subscribe_events");
                    w.WriteString("event_type", _config.EventType);
                }, handshakeToken);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The linked token fired on its own: that is the handshake deadline, not shutdown.
                throw new TimeoutException(
                    $"Home Assistant did not complete the connection handshake within {timeout.TotalSeconds:F0}s.");
            }
        }

        SetState(ConnectionState.Connected, uri.Host);
        _log.LogInformation("Connected. Subscribed to '{EventType}' as device '{DeviceId}'.",
            _config.EventType, _config.DeviceId);

        // Publish the session only once it is fully usable, and retract it before teardown so
        // an in-flight response never writes to a closing socket.
        _session = session;
        try
        {
            await PumpAsync(session, subscriptionId, sink, ct);
        }
        finally
        {
            _session = null;
        }
    }

    /// <summary>Receives until the link drops, dispatching events and driving keepalive.</summary>
    private async Task PumpAsync(Session session, int subscriptionId,
        ChannelWriter<InboundEvent> sink, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pingInterval = TimeSpan.FromSeconds(_config.HomeAssistant.PingIntervalSeconds);
        var pingTimeout = TimeSpan.FromSeconds(_config.HomeAssistant.PingTimeoutSeconds);
        var lastPong = DateTimeOffset.UtcNow;

        var keepalive = Task.Run(async () =>
        {
            while (!linked.Token.IsCancellationRequested)
            {
                await Task.Delay(pingInterval, linked.Token);

                // Liveness is the time since the last pong, whichever ping it answered. Tracking
                // outstanding ping ids would say no more: a stale link stops answering all of
                // them, and a live one answers in order.
                if (DateTimeOffset.UtcNow - lastPong > pingInterval + pingTimeout)
                {
                    _log.LogWarning("No pong within {Timeout}s; treating the link as dead.",
                        (pingInterval + pingTimeout).TotalSeconds);
                    await linked.CancelAsync();
                    return;
                }

                await session.SendAsync(w =>
                {
                    w.WriteNumber("id", session.NextId());
                    w.WriteString("type", "ping");
                }, linked.Token);
            }
        }, linked.Token);

        try
        {
            while (!linked.Token.IsCancellationRequested)
            {
                using var message = await session.ReceiveAsync(linked.Token);
                var root = message.RootElement;
                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                switch (type)
                {
                    case "pong":
                        lastPong = DateTimeOffset.UtcNow;
                        break;

                    case "event":
                        // Clone: the JsonDocument backing this element is disposed on loop exit.
                        if (root.TryGetProperty("event", out var evt) &&
                            evt.TryGetProperty("data", out var data))
                        {
                            await sink.WriteAsync(
                                new InboundEvent(data.Clone(), DateTimeOffset.UtcNow), linked.Token);
                        }
                        else
                        {
                            _log.LogDebug("Ignoring event with no data payload.");
                        }
                        break;

                    case "result":
                        var id = root.TryGetProperty("id", out var rid) ? rid.GetInt32() : -1;
                        var ok = root.TryGetProperty("success", out var s) && s.GetBoolean();
                        if (!ok)
                        {
                            var error = root.TryGetProperty("error", out var e) ? e.ToString() : "unknown";
                            if (id == subscriptionId)
                                throw new InvalidOperationException($"subscribe_events failed: {error}");
                            _log.LogWarning("Command {Id} failed: {Error}", id, error);
                        }
                        break;

                    default:
                        _log.LogTrace("Ignoring message of type '{Type}'.", type);
                        break;
                }
            }
        }
        finally
        {
            await linked.CancelAsync();
            try { await keepalive; } catch { /* expected on shutdown */ }
        }
    }

    private void ConfigureTls(ClientWebSocket ws)
    {
        // Shared with the setup wizard's admin client — see HomeAssistantTls for why the two
        // must not be allowed to disagree about which certificates are acceptable.
        var validator = HomeAssistantTls.Validator(_config.HomeAssistant, _log);
        if (validator is not null) ws.Options.RemoteCertificateValidationCallback = validator;
    }

    /// <summary>Base64 SHA-256 over the certificate's SubjectPublicKeyInfo.</summary>
    internal static string SpkiFingerprint(X509Certificate certificate)
        => HomeAssistantTls.SpkiFingerprint(certificate);

    /// <summary>Framing and JSON helpers over a single connection. Sends are serialised.</summary>
    private sealed class Session(ClientWebSocket ws, ILogger log) : IDisposable
    {
        private const int MaxMessageBytes = 4 * 1024 * 1024;

        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private int _nextId;

        public int NextId() => Interlocked.Increment(ref _nextId);

        public async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }

            await _sendLock.WaitAsync(ct);
            try
            {
                await ws.SendAsync(buffer.ToArray(), WebSocketMessageType.Text, true, ct);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task<JsonDocument> ReceiveAsync(CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];

            while (true)
            {
                var result = await ws.ReceiveAsync(chunk, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    // Complete the closing handshake before bailing out. Without this reply the
                    // peer's CloseAsync blocks until its own timeout, holding the connection open
                    // on the Home Assistant side well after we have moved on.
                    if (ws.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            using var ack = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ack.Token);
                        }
                        catch (Exception ex)
                        {
                            log.LogTrace("Could not acknowledge close frame: {Message}", ex.Message);
                        }
                    }

                    throw new WebSocketException(
                        $"Home Assistant closed the connection: {result.CloseStatus} {result.CloseStatusDescription}");
                }

                buffer.Write(chunk, 0, result.Count);

                if (buffer.Length > MaxMessageBytes)
                    throw new InvalidOperationException(
                        $"Message exceeded {MaxMessageBytes} bytes; refusing to buffer further.");

                if (result.EndOfMessage) break;
            }

            buffer.Position = 0;
            try
            {
                return JsonDocument.Parse(buffer);
            }
            catch (JsonException ex)
            {
                log.LogWarning("Discarding malformed JSON from Home Assistant: {Message}", ex.Message);
                throw;
            }
        }

        public void Dispose() => _sendLock.Dispose();
    }
}

/// <summary>Home Assistant rejected the access token. Retrying without new credentials is futile.</summary>
public sealed class AuthenticationRejectedException(string message) : Exception(message);
