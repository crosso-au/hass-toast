using System.Text;

namespace HassToast.Agent.Toasts;

/// <summary>
/// Encodes the key/value pairs carried on a toast action, and decodes them when Windows hands
/// them back on activation.
/// <para>
/// Percent-encoded query syntax is used rather than the <c>k=v;k=v</c> convention some samples
/// show, because payload authors control these values and a literal <c>;</c> or <c>=</c> in one
/// would otherwise split a pair or invent a new one. Encoding removes that ambiguity.
/// </para>
/// <para>
/// Everything decoded here has round-tripped through the OS and is <b>untrusted on the way back
/// in</b> — it is reported to Home Assistant as data, never acted on directly.
/// </para>
/// </summary>
public static class ActivationArguments
{
    /// <summary>Reserved keys the agent adds so an activation can be traced to its toast.</summary>
    public const string TagKey = "_tag";
    public const string GroupKey = "_group";

    public static string Encode(IReadOnlyDictionary<string, string>? pairs)
    {
        if (pairs is null || pairs.Count == 0) return "";

        var builder = new StringBuilder();
        foreach (var (key, value) in pairs)
        {
            if (string.IsNullOrEmpty(key)) continue;
            if (builder.Length > 0) builder.Append('&');
            builder.Append(Uri.EscapeDataString(key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(value ?? ""));
        }

        return builder.ToString();
    }

    public static Dictionary<string, string> Decode(string? encoded)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(encoded)) return result;

        foreach (var pair in encoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');

            // A bare token with no '=' is kept as a key with an empty value rather than dropped,
            // so a malformed argument string still surfaces something diagnosable.
            if (separator < 0)
            {
                result[Unescape(pair)] = "";
                continue;
            }

            var key = Unescape(pair[..separator]);
            var value = Unescape(pair[(separator + 1)..]);
            if (!string.IsNullOrEmpty(key)) result[key] = value;
        }

        return result;
    }

    private static string Unescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            // Malformed escape sequence — keep the raw text rather than losing the argument.
            return value;
        }
    }

    /// <summary>Adds the toast's identity so a response can be tied back to what raised it.</summary>
    public static Dictionary<string, string> WithIdentity(
        IReadOnlyDictionary<string, string>? args, string? tag, string? group)
    {
        var combined = args is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(args, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(tag)) combined[TagKey] = tag;
        if (!string.IsNullOrWhiteSpace(group)) combined[GroupKey] = group;

        return combined;
    }
}
