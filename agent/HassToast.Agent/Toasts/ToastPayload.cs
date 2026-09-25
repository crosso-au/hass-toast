using System.Text.Json;
using System.Text.Json.Serialization;

namespace HassToast.Agent.Toasts;

/// <summary>
/// The signed wire envelope. <see cref="Payload"/> stays a string until the signature has been
/// verified — parsing untrusted JSON before authenticating it would be doing attacker-controlled
/// work for free.
/// </summary>
public sealed class ToastEnvelope
{
    [JsonPropertyName("v")]
    public int Version { get; set; }

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = "";

    [JsonPropertyName("op")]
    public string Op { get; set; } = "send";

    [JsonPropertyName("nonce")]
    public string Nonce { get; set; } = "";

    [JsonPropertyName("ts")]
    public long Timestamp { get; set; }

    /// <summary>Compact JSON for a <see cref="ToastPayload"/>, signed verbatim.</summary>
    [JsonPropertyName("payload")]
    public string Payload { get; set; } = "";

    [JsonPropertyName("sig")]
    public string Signature { get; set; } = "";
}

public static class ToastJson
{
    /// <summary>
    /// Shared options for payload parsing. Case-insensitive matching is off: the wire format is
    /// snake_case and accepting other spellings only hides schema mistakes.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };
}

/// <summary>Operations the agent understands. Anything else is rejected.</summary>
public static class ToastOp
{
    public const string Send = "send";
    public const string Update = "update";
    public const string Remove = "remove";
    public const string Clear = "clear";

    public static bool IsKnown(string op) => op is Send or Update or Remove or Clear;
}

public sealed class ToastPayload
{
    /// <summary>Identity for a later update or removal.</summary>
    public string? Tag { get; set; }

    public string? Group { get; set; }

    /// <summary>default | reminder | alarm | incomingCall | urgent</summary>
    public string? Scenario { get; set; }

    /// <summary>short | long</summary>
    public string? Duration { get; set; }

    /// <summary>Seconds until Windows drops it from the Action Center.</summary>
    public int? ExpiresIn { get; set; }

    /// <summary>Deliver to the Action Center without showing a banner.</summary>
    public bool SuppressPopup { get; set; }

    /// <summary>Overrides the displayed time with when the event actually happened.</summary>
    public DateTimeOffset? Timestamp { get; set; }

    public ToastVisual Visual { get; set; } = new();

    public ToastAudio? Audio { get; set; }

    /// <summary>What clicking the body of the toast does.</summary>
    public ToastLaunch? Launch { get; set; }

    /// <summary>Groups related toasts under a heading in the Action Center.</summary>
    public ToastHeader? Header { get; set; }

    /// <summary>Text boxes and dropdowns. Windows allows at most five.</summary>
    public List<ToastInput> Inputs { get; set; } = [];

    /// <summary>Buttons. Shares a cap of five with <see cref="ContextMenu"/>.</summary>
    public List<ToastButton> Buttons { get; set; } = [];

    /// <summary>Right-click menu entries.</summary>
    public List<ToastButton> ContextMenu { get; set; } = [];
}

public sealed class ToastLaunch
{
    /// <summary>foreground | protocol</summary>
    public string? Type { get; set; }

    /// <summary>Target for protocol activation. Gated by security.allowedUriSchemes.</summary>
    public string? Uri { get; set; }

    /// <summary>Returned verbatim to Home Assistant when the toast is clicked.</summary>
    public Dictionary<string, string>? Args { get; set; }
}

public sealed class ToastHeader
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public Dictionary<string, string>? Args { get; set; }
}

public sealed class ToastInput
{
    /// <summary>text | selection</summary>
    public string Type { get; set; } = "text";

    /// <summary>Key the entered value comes back under.</summary>
    public string Id { get; set; } = "";

    public string? Title { get; set; }

    /// <summary>Greyed-out hint shown in an empty text box.</summary>
    public string? Placeholder { get; set; }

    /// <summary>Pre-filled text, or the pre-selected item id for a selection.</summary>
    public string? Default { get; set; }

    /// <summary>Options for a selection input. Windows allows at most five.</summary>
    public List<ToastSelectionItem> Items { get; set; } = [];
}

public sealed class ToastSelectionItem
{
    public string Id { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class ToastButton
{
    /// <summary>
    /// null for a normal button, or <c>snooze</c> / <c>dismiss</c> for the system-handled,
    /// automatically localised variants.
    /// </summary>
    public string? Type { get; set; }

    public string? Content { get; set; }

    public Dictionary<string, string>? Args { get; set; }

    /// <summary>foreground | background | protocol</summary>
    public string? Activation { get; set; }

    /// <summary>Target for protocol activation. Gated by security.allowedUriSchemes.</summary>
    public string? Uri { get; set; }

    /// <summary>Success (green) or Critical (red). Windows 11 only.</summary>
    public string? Style { get; set; }

    /// <summary>Pins this button beside the given input, giving a quick-reply layout.</summary>
    public string? InputId { get; set; }

    /// <summary>16x16 white transparent icon. All-or-nothing across a toast's buttons.</summary>
    public string? Icon { get; set; }

    /// <summary>Shown on hover; the only label an icon-only button has.</summary>
    public string? Tooltip { get; set; }

    public bool IsSystem => Type is "snooze" or "dismiss";
}

public sealed class ToastVisual
{
    /// <summary>Up to three top-level lines; Windows drops any beyond that.</summary>
    public List<string> Text { get; set; } = [];

    public string? Attribution { get; set; }

    /// <summary>Replaces the app icon. Circle-cropped this is the conventional avatar slot.</summary>
    public ToastImage? AppLogo { get; set; }

    /// <summary>Large banner image above the text.</summary>
    public ToastImage? Hero { get; set; }

    /// <summary>Full-width image below the text.</summary>
    public ToastImage? Inline { get; set; }

    public ToastProgress? Progress { get; set; }

    /// <summary>
    /// Multi-column adaptive layout. Also the only place Windows honours text styling —
    /// <c>hint-style</c> is ignored on top-level text elements.
    /// </summary>
    public List<ToastGroup> Groups { get; set; } = [];
}

/// <summary>Desktop-only progress bar.</summary>
public sealed class ToastProgress
{
    public string? Title { get; set; }

    /// <summary>0.0–1.0, or null for an indeterminate bar.</summary>
    [JsonConverter(typeof(ProgressValueConverter))]
    public double? Value { get; set; }

    /// <summary>Replaces the default percentage caption, e.g. "4/10 files".</summary>
    public string? ValueOverride { get; set; }

    /// <summary>Required by Windows; shown under the bar, e.g. "Downloading...".</summary>
    public string Status { get; set; } = "";
}

/// <summary>Accepts either a number or the literal string "indeterminate".</summary>
public sealed class ProgressValueConverter : JsonConverter<double?>
{
    public override double? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number:
                return reader.GetDouble();
            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.Equals(text, "indeterminate", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (double.TryParse(text, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
                throw new JsonException($"progress value '{text}' is neither a number nor 'indeterminate'");
            default:
                throw new JsonException($"unexpected token {reader.TokenType} for a progress value");
        }
    }

    public override void Write(Utf8JsonWriter writer, double? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteStringValue("indeterminate");
        else writer.WriteNumberValue(value.Value);
    }
}

/// <summary>A row of columns. Windows shows the group whole or not at all.</summary>
public sealed class ToastGroup
{
    public List<ToastColumn> Columns { get; set; } = [];
}

public sealed class ToastColumn
{
    /// <summary>Relative width against the other columns in the same group.</summary>
    public int? Weight { get; set; }

    /// <summary>top | center | bottom</summary>
    public string? TextStacking { get; set; }

    public List<ToastColumnChild> Children { get; set; } = [];
}

/// <summary>
/// Either a text element or an image, discriminated by which of <see cref="Text"/> and
/// <see cref="Src"/> is set. A single type keeps the JSON shape flat and forgiving.
/// </summary>
public sealed class ToastColumnChild
{
    public string? Text { get; set; }

    /// <summary>caption | body | base | subtitle | title | subheader | header, plus Subtle/Numeral variants.</summary>
    public string? Style { get; set; }

    /// <summary>auto | left | center | right</summary>
    public string? Align { get; set; }

    public bool? Wrap { get; set; }
    public int? MaxLines { get; set; }
    public int? MinLines { get; set; }

    public string? Src { get; set; }

    /// <summary>none | circle</summary>
    public string? Crop { get; set; }

    public bool? RemoveMargin { get; set; }
    public string? Alt { get; set; }

    public bool IsImage => !string.IsNullOrWhiteSpace(Src);
}

public sealed class ToastImage
{
    public string Src { get; set; } = "";

    /// <summary>none | circle</summary>
    public string? Crop { get; set; }

    public string? Alt { get; set; }
}

public sealed class ToastAudio
{
    /// <summary>
    /// An <c>ms-winsoundevent:*</c> system sound, which is all Windows accepts for an unpackaged
    /// app: the <c>audio</c> element otherwise takes only ms-appx / ms-resource sources, and an
    /// unpackaged app has neither. Anything else is dropped rather than emitted.
    /// </summary>
    public string? Src { get; set; }

    public bool Loop { get; set; }

    public bool Silent { get; set; }
}
