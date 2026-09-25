using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace HassToast.Agent.Tests;

/// <summary>
/// A stand-in for the administrative surface of Home Assistant: the REST endpoints behind the
/// config-entries flow, and the websocket commands behind HACS.
/// <para>
/// Separate from <see cref="FakeHomeAssistant"/> on purpose. That one exists to exercise a
/// long-lived subscriber — reconnection, ping/pong, event delivery — and bending it to also serve
/// request/response REST would make both halves harder to read than either is alone.
/// </para>
/// <para>
/// A real listener and a real socket rather than mocks, because what is actually under test is
/// whether the client speaks the protocol: the two-POST flow handshake, matching websocket
/// results by id, and the add-then-poll-then-download order HACS requires.
/// </para>
/// </summary>
public sealed class FakeHomeAssistantAdmin : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly string _expectedToken;
    private readonly Dictionary<string, (string Domain, string Title)> _entries = new();

    private Task? _acceptLoop;
    private int _nextFlow;
    private int _nextEntry;

    public FakeHomeAssistantAdmin(string expectedToken = "good-token")
    {
        _expectedToken = expectedToken;
        Port = GetFreePort();
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public int Port { get; }

    public string BaseUrl => $"http://localhost:{Port}";

    // --- what this instance pretends to be -----------------------------------

    public bool IsAdmin { get; set; } = true;
    public bool HacsInstalled { get; set; } = true;
    public bool IntegrationInstalled { get; set; }
    public string Version { get; set; } = "2025.8.0";

    /// <summary>
    /// How many more times <c>api/config</c> reports the instance as still starting before it
    /// reports running, as it does for a while after a restart.
    /// </summary>
    public int StartingPolls { get; set; }

    /// <summary>Set to make the config flow abort the way a duplicate device id does.</summary>
    public bool AbortAsAlreadyConfigured { get; set; }

    // --- what the client did -------------------------------------------------

    public List<string> HacsCommands { get; } = [];
    public string? AddedRepository { get; private set; }
    public string? DownloadedRepositoryId { get; private set; }
    public string? SubmittedDeviceId { get; private set; }
    public string? SubmittedSigningKey { get; private set; }
    public List<string> DeletedEntryIds { get; } = [];
    public int RestartCount { get; private set; }

    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(AcceptAsync);
    }

    /// <summary>Pre-seeds an entry, as though one had been configured earlier.</summary>
    public string AddEntry(string domain, string title)
    {
        var id = $"entry{++_nextEntry}";
        _entries[id] = (domain, title);
        return id;
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // Each connection is served on its own task: the client holds a websocket open while
            // also making REST calls, and serving in sequence would deadlock on exactly that.
            _ = Task.Run(() => ServeAsync(context));
        }
    }

    private async Task ServeAsync(HttpListenerContext context)
    {
        try
        {
            if (context.Request.IsWebSocketRequest)
            {
                var ws = await context.AcceptWebSocketAsync(subProtocol: null);
                await ServeSocketAsync(ws.WebSocket, _cts.Token);
                return;
            }

            await ServeRestAsync(context);
        }
        catch (Exception)
        {
            // A fake that throws its own errors into the test output helps nobody; failures show
            // up as the client's own error handling, which is what is being tested.
            try { context.Response.Abort(); } catch (Exception) { /* already gone */ }
        }
    }

    // ------------------------------------------------------------------- REST

    private async Task ServeRestAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var path = request.Url!.AbsolutePath.TrimEnd('/');

        if (request.Headers["Authorization"] != $"Bearer {_expectedToken}")
        {
            await RespondAsync(context, 401, """{"message":"Unauthorized"}""");
            return;
        }

        switch (request.HttpMethod, path)
        {
            case ("GET", "/api"):
                await RespondAsync(context, 200, """{"message":"API running."}""");
                return;

            case ("GET", "/api/config"):
                var state = StartingPolls-- > 0 ? "STARTING" : "RUNNING";
                await RespondAsync(context, 200,
                    $$"""{"version":"{{Version}}","location_name":"Home","state":"{{state}}"}""");
                return;

            case ("GET", "/api/config/config_entries/flow_handlers"):
                await RespondAsync(context, 200, IntegrationInstalled ? """["hass_toast"]""" : "[]");
                return;

            case ("POST", "/api/services/homeassistant/restart"):
                RestartCount++;
                await RespondAsync(context, 200, "[]");
                return;

            case ("POST", "/api/config/config_entries/flow"):
                await RespondAsync(context, 200, $$"""
                    {"type":"form","flow_id":"flow{{++_nextFlow}}","handler":"hass_toast","step_id":"user","errors":null}
                    """);
                return;

            case ("GET", "/api/config/config_entries/entry"):
                var listed = _entries.Select(e => $$"""
                    {"entry_id":"{{e.Key}}","domain":"{{e.Value.Domain}}","title":"{{e.Value.Title}}"}
                    """);
                await RespondAsync(context, 200, $"[{string.Join(',', listed)}]");
                return;
        }

        if (request.HttpMethod == "POST" && path.StartsWith("/api/config/config_entries/flow/"))
        {
            await ServeFlowSubmissionAsync(context);
            return;
        }

        if (request.HttpMethod == "DELETE" && path.StartsWith("/api/config/config_entries/entry/"))
        {
            var id = path[(path.LastIndexOf('/') + 1)..];
            DeletedEntryIds.Add(id);
            _entries.Remove(id);
            await RespondAsync(context, 200, """{"require_restart":false}""");
            return;
        }

        await RespondAsync(context, 404, """{"message":"Not found"}""");
    }

    private async Task ServeFlowSubmissionAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        using var body = JsonDocument.Parse(await reader.ReadToEndAsync());

        SubmittedDeviceId = body.RootElement.GetProperty("device_id").GetString();
        SubmittedSigningKey = body.RootElement.GetProperty("signing_key").GetString();

        if (AbortAsAlreadyConfigured)
        {
            await RespondAsync(context, 200, """{"type":"abort","reason":"already_configured"}""");
            return;
        }

        var id = $"entry{++_nextEntry}";
        _entries[id] = ("hass_toast", SubmittedDeviceId ?? "");

        // Three '$' throughout: these payloads end on two literal closing braces, and a raw
        // interpolated string can only carry runs of fewer than that many as content.
        await RespondAsync(context, 200, $$$"""
            {"type":"create_entry","title":"{{{SubmittedDeviceId}}}","result":{"entry_id":"{{{id}}}","domain":"hass_toast"}}
            """);
    }

    private static async Task RespondAsync(HttpListenerContext context, int status, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json.Trim());

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    // -------------------------------------------------------------- WebSocket

    private async Task ServeSocketAsync(WebSocket socket, CancellationToken ct)
    {
        await SendAsync(socket, """{"type":"auth_required","ha_version":"2025.8.0"}""", ct);

        using (var auth = await ReceiveAsync(socket, ct))
        {
            var token = auth.RootElement.GetProperty("access_token").GetString();

            if (token != _expectedToken)
            {
                await SendAsync(socket, """{"type":"auth_invalid","message":"Invalid access token"}""", ct);
                return;
            }
        }

        await SendAsync(socket, """{"type":"auth_ok","ha_version":"2025.8.0"}""", ct);

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            JsonDocument message;
            try
            {
                message = await ReceiveAsync(socket, ct);
            }
            catch (Exception)
            {
                return;
            }

            using (message)
            {
                var root = message.RootElement;
                var id = root.GetProperty("id").GetInt32();
                var type = root.GetProperty("type").GetString();

                await SendAsync(socket, HandleCommand(id, type, root), ct);
            }
        }
    }

    private string HandleCommand(int id, string? type, JsonElement message)
    {
        if (type is not null && type.StartsWith("hacs/")) HacsCommands.Add(type);

        switch (type)
        {
            case "auth/current_user":
                return $$$"""
                    {"id":{{{id}}},"type":"result","success":true,
                     "result":{"id":"u1","name":"Test User","is_admin":{{{(IsAdmin ? "true" : "false")}}}}}
                    """;

            case "hacs/info" when HacsInstalled:
                return $$$"""{"id":{{{id}}},"type":"result","success":true,"result":{"version":"2.0.5"}}""";

            case "hacs/repositories/add":
                AddedRepository = message.GetProperty("repository").GetString();
                return $$$"""{"id":{{{id}}},"type":"result","success":true,"result":{}}""";

            case "hacs/repositories/list":
                // Only listed once it has been added, which is what forces the client to poll
                // rather than assume the add was synchronous.
                var repositories = AddedRepository is null
                    ? ""
                    : $$$"""{"id":"1296666","full_name":"{{{AddedRepository}}}","category":"integration"}""";

                return $$$"""{"id":{{{id}}},"type":"result","success":true,"result":[{{{repositories}}}]}""";

            case "hacs/repository/download":
                DownloadedRepositoryId = message.GetProperty("repository").GetString();
                IntegrationInstalled = true;
                return $$$"""{"id":{{{id}}},"type":"result","success":true,"result":{}}""";

            default:
                return $$$"""
                    {"id":{{{id}}},"type":"result","success":false,
                     "error":{"code":"unknown_command","message":"Unknown command."}}
                    """;
        }
    }

    private static async Task SendAsync(WebSocket socket, string json, CancellationToken ct)
        => await socket.SendAsync(
            Encoding.UTF8.GetBytes(json.Trim()), WebSocketMessageType.Text, true, ct);

    private static async Task<JsonDocument> ReceiveAsync(WebSocket socket, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];

        while (true)
        {
            var result = await socket.ReceiveAsync(chunk, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new WebSocketException("closed");

            buffer.Write(chunk, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        buffer.Position = 0;
        return JsonDocument.Parse(buffer);
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();

        try { _listener.Stop(); } catch (Exception) { /* already stopped */ }
        try { _listener.Close(); } catch (Exception) { /* already closed */ }

        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch (Exception) { /* expected on shutdown */ }
        }

        _cts.Dispose();
    }
}
