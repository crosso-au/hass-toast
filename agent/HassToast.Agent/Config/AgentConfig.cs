using System.Text.Json;
using System.Text.Json.Serialization;

namespace HassToast.Agent.Config;

/// <summary>
/// Non-secret agent configuration, persisted as JSON next to the encrypted secret store.
/// Secrets (HA token, HMAC key) never appear here — see <see cref="Security.SecretStore"/>.
/// </summary>
public sealed class AgentConfig
{
    public HomeAssistantConfig HomeAssistant { get; set; } = new();

    /// <summary>
    /// Identifies this machine. Envelopes addressed to a different device are ignored.
    /// </summary>
    public string DeviceId { get; set; } = Environment.MachineName.ToLowerInvariant();

    /// <summary>
    /// Bus event this agent subscribes to on the Home Assistant WebSocket API.
    /// <para>
    /// Neither setup nor the Home Assistant config flow asks for this. Both sides default to
    /// <see cref="DefaultEventType"/>, so a mismatch could only ever come from someone typing
    /// the same name into two places and getting one of them wrong. It remains editable here for
    /// the rare case that the default collides with another integration's event.
    /// </para>
    /// </summary>
    public string EventType { get; set; } = DefaultEventType;

    /// <summary>The bus event name both halves agree on without being told.</summary>
    public const string DefaultEventType = "hass_toast_notify";

    /// <summary>
    /// Name shown in the toast header and in Windows notification settings.
    /// <para>
    /// Windows takes this from the Start Menu shortcut's file name, so changing it renames that
    /// shortcut. The AUMID deliberately does not change with it — that would orphan every
    /// notification already in the Action Center and every toast's recorded activator.
    /// </para>
    /// </summary>
    public string DisplayName { get; set; } = Toasts.AppIdentity.DefaultDisplayName;

    public SecurityConfig Security { get; set; } = new();

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HassToast");

    public static string DefaultPath => Path.Combine(DefaultDirectory, "config.json");

    /// <summary>Rolling agent logs.</summary>
    public static string LogDirectory => Path.Combine(DefaultDirectory, "logs");

    /// <summary>
    /// Where fetched toast images are cached. Under TEMP rather than the config directory
    /// because the contents are disposable — but it is still a trace, so it is defined here
    /// alongside the rest rather than inline where it happens to be created.
    /// </summary>
    public static string ImageCacheDirectory => Path.Combine(
        Path.GetTempPath(), "HassToast", "images");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static AgentConfig Load(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new AgentConfig();
        return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path), JsonOptions)
               ?? new AgentConfig();
    }

    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> if the config cannot be used.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(HomeAssistant.Url))
            throw new InvalidOperationException("homeAssistant.url is not configured.");
        if (!Uri.TryCreate(HomeAssistant.Url, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"homeAssistant.url is not a valid URI: {HomeAssistant.Url}");
        if (uri.Scheme is not ("ws" or "wss" or "http" or "https"))
            throw new InvalidOperationException($"homeAssistant.url must be ws/wss/http/https, got '{uri.Scheme}'.");
        if (string.IsNullOrWhiteSpace(DeviceId))
            throw new InvalidOperationException("deviceId must not be empty.");
        if (string.IsNullOrWhiteSpace(EventType))
            throw new InvalidOperationException("eventType must not be empty.");
    }
}

public sealed class HomeAssistantConfig
{
    /// <summary>
    /// Base URL or full WebSocket URL of the Home Assistant instance. Accepts
    /// <c>https://ha.local</c> or <c>wss://ha.local/api/websocket</c>; both normalise
    /// to the same endpoint via <see cref="WebSocketUri"/>.
    /// </summary>
    public string Url { get; set; } = "";

    /// <summary>
    /// Standard TLS chain validation. Only disable on a trusted network with a
    /// self-signed certificate, and prefer <see cref="PinnedSpkiSha256"/> instead.
    /// </summary>
    public bool VerifyTls { get; set; } = true;

    /// <summary>
    /// Base64 SHA-256 of the server certificate's SubjectPublicKeyInfo. When set, the
    /// certificate must match this pin regardless of chain validity — the safe way to
    /// accept a self-signed internal HA certificate.
    /// </summary>
    public string? PinnedSpkiSha256 { get; set; }

    /// <summary>
    /// Ceiling on the connect and authentication handshake. Without this, a server that
    /// accepts the TCP connection but never completes the WebSocket upgrade would leave
    /// the agent hung in Connecting, never reaching the reconnect path.
    /// </summary>
    public int ConnectTimeoutSeconds { get; set; } = 15;

    /// <summary>
    /// The config entry the setup wizard created in Home Assistant for this machine.
    /// <para>
    /// Recorded so uninstall can remove exactly that entry and leave every other machine's alone.
    /// Without it the only handle is the entry's title, which the config flow happens to set to
    /// the device id — true today, but a weaker thing to delete on than an id Home Assistant
    /// gave us directly. Null when the machine was configured by hand.
    /// </para>
    /// </summary>
    public string? ConfigEntryId { get; set; }

    /// <summary>Seconds between client pings. HA disconnects idle clients.</summary>
    public int PingIntervalSeconds { get; set; } = 30;

    /// <summary>How long to wait for a pong before treating the connection as dead.</summary>
    public int PingTimeoutSeconds { get; set; } = 10;

    /// <summary>Resolves <see cref="Url"/> to the REST API base, for firing bus events.</summary>
    public Uri RestBaseUri()
    {
        var uri = new Uri(Url, UriKind.Absolute);
        var scheme = uri.Scheme switch
        {
            "ws" => "http",
            "wss" => "https",
            var s => s,
        };

        return new UriBuilder(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port).Uri;
    }

    /// <summary>Resolves <see cref="Url"/> to the WebSocket API endpoint.</summary>
    public Uri WebSocketUri()
    {
        var uri = new Uri(Url, UriKind.Absolute);
        var scheme = uri.Scheme switch
        {
            "http" => "ws",
            "https" => "wss",
            var s => s,
        };

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.EndsWith("/api/websocket", StringComparison.OrdinalIgnoreCase))
            path += "/api/websocket";

        return new UriBuilder(scheme, uri.Host, uri.IsDefaultPort ? -1 : uri.Port, path).Uri;
    }
}

/// <summary>
/// Limits applied to every inbound payload before it is turned into a toast.
/// Defaults are deliberately restrictive; widen them only deliberately.
/// </summary>
public sealed class SecurityConfig
{
    /// <summary>
    /// URI schemes a protocol-activation button may launch. This is the sharpest edge in
    /// the system: a button here launches whatever the payload asked for, so schemes such
    /// as <c>file</c>, <c>ms-settings</c> and custom handlers stay out by default.
    /// </summary>
    public List<string> AllowedUriSchemes { get; set; } = ["https", "http", "mailto"];

    /// <summary>Schemes an image source may use. <c>file</c> is opt-in.</summary>
    public List<string> AllowedImageSchemes { get; set; } = ["https", "http"];

    /// <summary>Cap on a fetched inline image. Windows itself rejects inline images over 3 MB.</summary>
    public int MaxInlineImageBytes { get; set; } = 3 * 1024 * 1024;

    /// <summary>Cap on a fetched hero / app-logo image. Windows rejects these over 200 KB.</summary>
    public int MaxLogoImageBytes { get; set; } = 200 * 1024;

    /// <summary>Accepted clock skew between Home Assistant and this machine, in seconds.</summary>
    public int TimestampToleranceSeconds { get; set; } = 120;

    /// <summary>Sustained toast rate ceiling.</summary>
    public int MaxToastsPerMinute { get; set; } = 30;

    /// <summary>
    /// Reject payloads whose signature does not verify. Disabling this removes the only
    /// defence against another Home Assistant component forging toasts on the event bus.
    /// </summary>
    public bool RequireSignature { get; set; } = true;
}
