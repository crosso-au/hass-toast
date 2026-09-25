using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Config;

namespace HassToast.Agent.Transport;

/// <summary>What a probe of a Home Assistant instance found.</summary>
public sealed record HomeAssistantProbe(
    bool Reachable,
    bool TokenAccepted,
    bool IsAdmin,
    string? Version,
    string? UserName,
    string? Error)
{
    /// <summary>Whether this instance can be set up without further help from the user.</summary>
    public bool Usable => Reachable && TokenAccepted && IsAdmin;

    public static HomeAssistantProbe Unreachable(string error)
        => new(false, false, false, null, null, error);
}

/// <summary>Whether HACS is installed, and at what version.</summary>
public sealed record HacsStatus(bool Installed, string? Version);

/// <summary>One config entry as Home Assistant reports it.</summary>
public sealed record ConfigEntrySummary(string EntryId, string Domain, string Title);

/// <summary>What came back from submitting the integration's config flow.</summary>
public sealed record ConfigEntryResult(string? EntryId, bool AlreadyConfigured, string? Error)
{
    public bool Created => EntryId is not null;
}

/// <summary>
/// The administrative half of the Home Assistant conversation: everything the setup wizard and
/// the uninstaller need that the running agent does not.
/// <para>
/// This is what removes the two worst steps of the old install. Home Assistant exposes no API for
/// writing into <c>custom_components</c>, so the integration itself has to arrive through HACS —
/// but it <em>does</em> expose the config-entry flow the frontend uses, which means the device id
/// and the signing key can be delivered over TLS instead of being read off a terminal and pasted
/// into a browser. A key that never touches a clipboard cannot be left on one.
/// </para>
/// <para>
/// Everything here needs an admin token. That is not this code's choice: Home Assistant requires
/// admin for starting a config flow and for every HACS command, so a non-admin token is worth
/// detecting up front rather than three steps in.
/// </para>
/// </summary>
public sealed class HomeAssistantAdmin : IDisposable
{
    private const string IntegrationDomain = "hass_toast";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>How long an ordinary request may take before it counts as unanswered.</summary>
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a config-flow request may take. Finishing the flow sets the new entry up before
    /// Home Assistant answers, and on an instance that has only just restarted that can take far
    /// longer than any ordinary request, since it waits its turn behind everything else starting.
    /// </summary>
    private static readonly TimeSpan FlowRequestTimeout = TimeSpan.FromMinutes(2);

    private static readonly HttpRequestOptionsKey<TimeSpan> TimeoutOption = new("hass-toast.timeout");

    private readonly HomeAssistantConfig _config;
    private readonly string _token;
    private readonly ILogger? _log;
    private readonly HttpClient _http;

    public HomeAssistantAdmin(HomeAssistantConfig config, string accessToken, ILogger? log = null)
    {
        _config = config;
        _token = accessToken;
        _log = log;

        // The timeout is applied per request by TimeoutHandler rather than here, because the
        // config flow needs a much longer one than everything else.
        _http = new HttpClient(new TimeoutHandler(HomeAssistantTls.CreateHttpHandler(config, log)))
        {
            BaseAddress = config.RestBaseUri(),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("hass-toast/2.0");
    }

    // ---------------------------------------------------------------- probing

    /// <summary>
    /// Establishes whether this URL and token can actually do the job, in one round trip the
    /// wizard can run behind a "Test connection" button.
    /// </summary>
    public async Task<HomeAssistantProbe> ProbeAsync(CancellationToken ct)
    {
        string? version;
        try
        {
            using var response = await _http.GetAsync("api/config", ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return new HomeAssistantProbe(true, false, false, null, null, "The access token was rejected.");

            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch (Exception ex)
        {
            return HomeAssistantProbe.Unreachable(Describe(ex));
        }

        // Admin status is only exposed over the websocket API, and asking for it doubles as proof
        // that the websocket endpoint — the one the agent will actually live on — is reachable
        // too. A REST-only check would happily pass on an instance the agent could never reach.
        try
        {
            await using var socket = await ConnectAsync(ct);
            var user = await socket.CommandAsync("auth/current_user", null, ct);

            var result = user.GetProperty("result");
            var isAdmin = result.TryGetProperty("is_admin", out var a) && a.GetBoolean();
            var name = result.TryGetProperty("name", out var n) ? n.GetString() : null;

            return new HomeAssistantProbe(
                Reachable: true,
                TokenAccepted: true,
                IsAdmin: isAdmin,
                Version: version,
                UserName: name,
                Error: isAdmin ? null : "That token belongs to a non-administrator, which cannot add integrations.");
        }
        catch (Exception ex)
        {
            return new HomeAssistantProbe(true, true, false, version, null,
                $"The REST API answered but the WebSocket API did not: {Describe(ex)}");
        }
    }

    /// <summary>Whether the integration is present and offering a config flow.</summary>
    public async Task<bool> IsIntegrationInstalledAsync(CancellationToken ct)
    {
        try
        {
            var handlers = await _http.GetFromJsonAsync<string[]>(
                "api/config/config_entries/flow_handlers", Json, ct);

            return handlers?.Contains(IntegrationDomain) == true;
        }
        catch (Exception ex)
        {
            _log?.LogDebug("Could not read flow handlers: {Message}", Describe(ex));
            return false;
        }
    }

    // ------------------------------------------------------------------ HACS

    public async Task<HacsStatus> ProbeHacsAsync(CancellationToken ct)
    {
        try
        {
            await using var socket = await ConnectAsync(ct);
            var info = await socket.CommandAsync("hacs/info", null, ct);

            if (!info.GetProperty("success").GetBoolean()) return new HacsStatus(false, null);

            var result = info.GetProperty("result");
            var version = result.TryGetProperty("version", out var v) ? v.GetString() : null;
            return new HacsStatus(true, version);
        }
        catch (Exception ex)
        {
            _log?.LogDebug("HACS probe failed: {Message}", Describe(ex));
            return new HacsStatus(false, null);
        }
    }

    /// <summary>
    /// Adds this project as a HACS custom repository and downloads it.
    /// <para>
    /// The add and the download are separate commands because HACS treats them as separate
    /// things, and adding is asynchronous: HACS goes to the GitHub API to resolve the repository
    /// before it appears in its own list. Hence the poll between the two — asking for the
    /// download immediately fails with a repository id that does not exist yet.
    /// </para>
    /// </summary>
    /// <param name="repositorySlug">The <c>owner/name</c> form, e.g. <c>crosso-au/hass-toast</c>.</param>
    public async Task InstallViaHacsAsync(
        string repositorySlug, IProgress<string>? progress, CancellationToken ct)
    {
        await using var socket = await ConnectAsync(ct);

        progress?.Report($"Adding {repositorySlug} to HACS…");

        var added = await socket.CommandAsync("hacs/repositories/add", w =>
        {
            w.WriteString("repository", repositorySlug);
            w.WriteString("category", "integration");
        }, ct);

        // An already-known repository is reported as an error, and it is not one: re-running the
        // wizard over an existing install should reach the same end state, not stop at the
        // first step that has already happened.
        if (!added.GetProperty("success").GetBoolean())
        {
            var message = ErrorMessage(added);
            _log?.LogInformation("HACS declined to add the repository ({Message}); " +
                                 "continuing in case it is already known.", message);
        }

        var id = await WaitForRepositoryIdAsync(socket, repositorySlug, ct);
        if (id is null)
        {
            throw new InvalidOperationException(
                $"HACS did not list {repositorySlug} after adding it. Check that the repository " +
                "is public and that Home Assistant can reach github.com.");
        }

        progress?.Report("Downloading the integration…");

        var downloaded = await socket.CommandAsync("hacs/repository/download", w =>
        {
            w.WriteString("repository", id);
        }, ct);

        if (!downloaded.GetProperty("success").GetBoolean())
            throw new InvalidOperationException($"HACS could not download the integration: {ErrorMessage(downloaded)}");

        progress?.Report("Downloaded. Home Assistant must restart to load it.");
    }

    private async Task<string?> WaitForRepositoryIdAsync(
        AdminSocket socket, string slug, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);

        while (DateTime.UtcNow < deadline)
        {
            var list = await socket.CommandAsync("hacs/repositories/list", w =>
            {
                w.WriteStartArray("categories");
                w.WriteStringValue("integration");
                w.WriteEndArray();
            }, ct);

            if (list.GetProperty("success").GetBoolean())
            {
                foreach (var repo in list.GetProperty("result").EnumerateArray())
                {
                    if (!repo.TryGetProperty("full_name", out var fullName)) continue;
                    if (!string.Equals(fullName.GetString(), slug, StringComparison.OrdinalIgnoreCase)) continue;

                    // The id comes back as a string in current HACS, but it has been a number
                    // before now, and being wrong here fails three calls later with something
                    // unhelpful about an unknown repository.
                    return repo.TryGetProperty("id", out var idValue)
                        ? idValue.ValueKind == JsonValueKind.Number
                            ? idValue.GetInt64().ToString()
                            : idValue.GetString()
                        : null;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return null;
    }

    // --------------------------------------------------------------- restart

    /// <summary>
    /// Restarts Home Assistant and waits until it is answering again.
    /// <para>
    /// A newly downloaded custom component is not loaded until restart, so this is not optional.
    /// The wait deliberately watches for the instance to go <em>down</em> first: polling for
    /// "up" straight away sees the instance that has not stopped yet and declares success before
    /// anything has happened.
    /// </para>
    /// </summary>
    /// <param name="shutdownGrace">
    /// How long to spend watching for the instance to go down before giving up on seeing it and
    /// waiting for it to come back. Only worth shortening in tests, where the stand-in never
    /// actually stops.
    /// </param>
    public async Task<bool> RestartAndWaitAsync(
        TimeSpan timeout, IProgress<string>? progress, CancellationToken ct,
        TimeSpan? shutdownGrace = null)
    {
        progress?.Report("Restarting Home Assistant…");

        try
        {
            using var content = new StringContent("{}", Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync("api/services/homeassistant/restart", content, ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException("The access token is not allowed to restart Home Assistant.");
        }
        catch (HttpRequestException)
        {
            // Expected often enough to be unremarkable: Home Assistant can drop the connection
            // as it goes down, before it has finished answering the request that told it to.
        }
        catch (TimeoutException)
        {
            // Same thing, surfacing as a client timeout instead.
        }

        var deadline = DateTime.UtcNow + timeout;

        // Phase one: watch it go.
        var sawItStop = false;
        var stopBy = DateTime.UtcNow + (shutdownGrace ?? TimeSpan.FromSeconds(30));
        while (DateTime.UtcNow < stopBy && DateTime.UtcNow < deadline)
        {
            if (!await IsAnsweringAsync(ct)) { sawItStop = true; break; }
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        if (!sawItStop)
            progress?.Report("Home Assistant did not appear to stop; waiting for it to come back anyway…");

        // Phase two: wait for it to come back.
        progress?.Report("Waiting for Home Assistant to come back…");

        while (DateTime.UtcNow < deadline)
        {
            if (await IsAnsweringAsync(ct))
            {
                // Answering is not the same as ready: the API comes up well before startup has
                // finished, and a config flow started in that window can stall past any timeout.
                if (!await WaitUntilRunningAsync(deadline - DateTime.UtcNow, progress, ct)) return false;

                progress?.Report("Home Assistant is back.");
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }

        return false;
    }

    /// <summary>
    /// Waits until Home Assistant reports that it has finished starting. Returns false if it
    /// never did within <paramref name="timeout"/>, including when it could not be reached.
    /// </summary>
    public async Task<bool> WaitUntilRunningAsync(
        TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var reported = false;

        while (true)
        {
            var state = await GetStateAsync(ct);
            if (state == "RUNNING") return true;

            if (DateTime.UtcNow >= deadline) return false;

            if (state is not null && !reported)
            {
                progress?.Report("Waiting for Home Assistant to finish starting…");
                reported = true;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    /// <summary>
    /// The core state from <c>api/config</c>, or null if Home Assistant could not be asked. An
    /// instance too old to report a state is taken as running, since it answered at all.
    /// </summary>
    private async Task<string?> GetStateAsync(CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync("api/config", ct);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return doc.RootElement.TryGetProperty("state", out var s) ? s.GetString() : "RUNNING";
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Polls until the integration shows up, which lags the restart by a few seconds.</summary>
    public async Task<bool> WaitForIntegrationAsync(
        TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Waiting for the integration to load…");

        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await IsIntegrationInstalledAsync(ct)) return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }

    private async Task<bool> IsAnsweringAsync(CancellationToken ct)
    {
        try
        {
            using var probe = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, probe.Token);
            using var response = await _http.GetAsync("api/", linked.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // --------------------------------------------------------- config entries

    public async Task<IReadOnlyList<ConfigEntrySummary>> GetEntriesAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("api/config/config_entries/entry", ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        var entries = new List<ConfigEntrySummary>();
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var domain = entry.TryGetProperty("domain", out var d) ? d.GetString() : null;
            var id = entry.TryGetProperty("entry_id", out var i) ? i.GetString() : null;
            if (domain is null || id is null) continue;

            entries.Add(new ConfigEntrySummary(
                id, domain, entry.TryGetProperty("title", out var t) ? t.GetString() ?? "" : ""));
        }

        return entries;
    }

    /// <summary>This integration's entry for a given device id, or null if there is none.</summary>
    public async Task<ConfigEntrySummary?> FindEntryAsync(string deviceId, CancellationToken ct)
    {
        var entries = await GetEntriesAsync(ct);

        // The entry is titled with the device id by the config flow, which is the only handle
        // the REST listing gives us — it does not return entry data.
        return entries.FirstOrDefault(e =>
            e.Domain == IntegrationDomain &&
            string.Equals(e.Title, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drives the integration's config flow to completion, creating the entry for this machine.
    /// </summary>
    public async Task<ConfigEntryResult> CreateEntryAsync(
        string deviceId, string signingKeyBase64, CancellationToken ct)
    {
        string flowId;

        using (var started = await PostJsonAsync("api/config/config_entries/flow", w =>
        {
            w.WriteString("handler", IntegrationDomain);
            w.WriteBoolean("show_advanced_options", false);
        }, ct, FlowRequestTimeout))
        {
            var root = started.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

            if (type != "form")
            {
                return new ConfigEntryResult(null, false,
                    $"Home Assistant did not offer the expected form (got '{type ?? "nothing"}').");
            }

            flowId = root.GetProperty("flow_id").GetString()!;
        }

        using var submitted = await PostJsonAsync($"api/config/config_entries/flow/{flowId}", w =>
        {
            w.WriteString("device_id", deviceId);
            w.WriteString("signing_key", signingKeyBase64);
        }, ct, FlowRequestTimeout);

        var result = submitted.RootElement;
        var resultType = result.TryGetProperty("type", out var rt) ? rt.GetString() : null;

        switch (resultType)
        {
            case "create_entry":
                return new ConfigEntryResult(
                    result.GetProperty("result").GetProperty("entry_id").GetString(), false, null);

            case "abort":
                var reason = result.TryGetProperty("reason", out var r) ? r.GetString() : null;
                return new ConfigEntryResult(null, reason == "already_configured",
                    reason == "already_configured"
                        ? $"'{deviceId}' is already configured in Home Assistant."
                        : $"Home Assistant aborted the flow: {reason ?? "no reason given"}.");

            case "form":
                // The form came back, which means it rejected what we sent. The only field that
                // can be rejected is the key, and we generated it — so this is worth reporting
                // verbatim rather than paraphrasing.
                var errors = result.TryGetProperty("errors", out var e) ? e.ToString() : "no detail";
                return new ConfigEntryResult(null, false, $"Home Assistant rejected the details: {errors}");

            default:
                return new ConfigEntryResult(null, false, $"Unexpected flow result '{resultType}'.");
        }
    }

    /// <summary>
    /// Performs a Home Assistant action, such as <c>notify.hass_toast</c>, exactly as an
    /// automation would. The installer's self-test uses this so it exercises the integration's
    /// real actions end to end rather than a shortcut around them.
    /// </summary>
    /// <param name="jsonData">The action's data, as a JSON object.</param>
    public async Task CallServiceAsync(string domain, string service, string jsonData, CancellationToken ct)
    {
        using var content = new StringContent(jsonData, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"api/services/{domain}/{service}", content, ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new InvalidOperationException("Home Assistant refused the access token.");

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"{domain}.{service} failed with {(int)response.StatusCode}: {Truncate(body)}");
        }
    }

    /// <summary>Removes one machine's entry. Leaves the integration and HACS alone.</summary>
    public async Task<bool> DeleteEntryAsync(string entryId, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"api/config/config_entries/entry/{entryId}", ct);
        return response.IsSuccessStatusCode;
    }

    // ---------------------------------------------------------------- plumbing

    private async Task<JsonDocument> PostJsonAsync(
        string path, Action<Utf8JsonWriter> write, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        using var content = new ByteArrayContent(buffer.ToArray());
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };
        if (timeout is { } t) request.Options.Set(TimeoutOption, t);

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new InvalidOperationException(
                "Home Assistant refused the request. The access token must belong to an administrator.");
        }

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{(int)response.StatusCode} from {path}: {Truncate(body)}");

        return JsonDocument.Parse(body);
    }

    private async Task<AdminSocket> ConnectAsync(CancellationToken ct)
        => await AdminSocket.ConnectAsync(
            _config.WebSocketUri(), _token, HomeAssistantTls.Validator(_config, _log), ct);

    private static string ErrorMessage(JsonElement result)
        => result.TryGetProperty("error", out var error)
            ? error.TryGetProperty("message", out var m) ? m.GetString() ?? error.ToString() : error.ToString()
            : "no detail";

    private static string Describe(Exception ex)
        => ex.InnerException is null ? ex.Message : $"{ex.Message} ({ex.InnerException.Message})";

    private static string Truncate(string text)
        => text.Length <= 400 ? text : text[..400] + "…";

    public void Dispose() => _http.Dispose();

    /// <summary>
    /// Applies a timeout to each request, <see cref="DefaultRequestTimeout"/> unless the request
    /// carries its own, and reports it as a <see cref="TimeoutException"/> naming the endpoint.
    /// </summary>
    private sealed class TimeoutHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var timeout = request.Options.TryGetValue(TimeoutOption, out var t) ? t : DefaultRequestTimeout;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                return await base.SendAsync(request, cts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"Home Assistant did not answer {request.RequestUri?.AbsolutePath} " +
                    $"within {timeout.TotalSeconds:F0} seconds.");
            }
        }
    }

    /// <summary>
    /// A short-lived authenticated websocket for request/response commands.
    /// <para>
    /// Deliberately not <see cref="HaWebSocketClient"/>: that one is a long-lived subscriber with
    /// reconnection, backoff and a tray state machine attached, none of which suits a wizard step
    /// that asks two questions and hangs up. Sharing it would mean bending it into both shapes.
    /// </para>
    /// </summary>
    private sealed class AdminSocket : IAsyncDisposable
    {
        private const int MaxMessageBytes = 8 * 1024 * 1024;

        private readonly ClientWebSocket _ws;
        private int _nextId;

        private AdminSocket(ClientWebSocket ws) => _ws = ws;

        public static async Task<AdminSocket> ConnectAsync(
            Uri uri, string token,
            System.Net.Security.RemoteCertificateValidationCallback? validator,
            CancellationToken ct)
        {
            var ws = new ClientWebSocket();
            if (validator is not null) ws.Options.RemoteCertificateValidationCallback = validator;

            var socket = new AdminSocket(ws);

            try
            {
                using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, connectTimeout.Token);

                await ws.ConnectAsync(uri, linked.Token);

                using (var greeting = await socket.ReceiveAsync(linked.Token))
                {
                    var type = greeting.RootElement.GetProperty("type").GetString();
                    if (type != "auth_required")
                        throw new InvalidOperationException($"Expected 'auth_required', got '{type}'.");
                }

                await socket.SendAsync(w =>
                {
                    w.WriteString("type", "auth");
                    w.WriteString("access_token", token);
                }, linked.Token);

                using (var authResult = await socket.ReceiveAsync(linked.Token))
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

                return socket;
            }
            catch
            {
                await socket.DisposeAsync();
                throw;
            }
        }

        /// <summary>Sends one command and returns the matching result message.</summary>
        public async Task<JsonElement> CommandAsync(
            string type, Action<Utf8JsonWriter>? extra, CancellationToken ct)
        {
            var id = Interlocked.Increment(ref _nextId);

            await SendAsync(w =>
            {
                w.WriteNumber("id", id);
                w.WriteString("type", type);
                extra?.Invoke(w);
            }, ct);

            // Home Assistant interleaves other traffic on this socket, so results are matched by
            // id rather than assumed to be the next thing that arrives.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

            while (DateTime.UtcNow < deadline)
            {
                using var message = await ReceiveAsync(ct);
                var root = message.RootElement;

                if (!root.TryGetProperty("id", out var messageId) || messageId.GetInt32() != id) continue;
                if (root.TryGetProperty("type", out var messageType) && messageType.GetString() != "result") continue;

                return root.Clone();
            }

            throw new TimeoutException($"Home Assistant did not answer '{type}' in time.");
        }

        private async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }

            await _ws.SendAsync(buffer.ToArray(), WebSocketMessageType.Text, true, ct);
        }

        private async Task<JsonDocument> ReceiveAsync(CancellationToken ct)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];

            while (true)
            {
                var result = await _ws.ReceiveAsync(chunk, ct);

                if (result.MessageType == WebSocketMessageType.Close)
                    throw new WebSocketException($"Home Assistant closed the connection: {result.CloseStatus}.");

                buffer.Write(chunk, 0, result.Count);

                if (buffer.Length > MaxMessageBytes)
                    throw new InvalidOperationException($"Message exceeded {MaxMessageBytes} bytes.");

                if (result.EndOfMessage) break;
            }

            buffer.Position = 0;
            return JsonDocument.Parse(buffer);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                {
                    using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, closing.Token);
                }
            }
            catch (Exception)
            {
                // Closing politely is a courtesy; the socket is going away regardless.
            }
            finally
            {
                _ws.Dispose();
            }
        }
    }
}
