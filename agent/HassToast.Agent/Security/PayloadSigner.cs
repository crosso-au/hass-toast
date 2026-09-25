using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HassToast.Agent.Security;

/// <summary>
/// Builds the byte string that both sides sign, and computes the HMAC over it.
/// <para>
/// The toast body is signed as an <b>opaque JSON string</b> rather than a re-serialised object.
/// Canonical JSON across Python and C# is a trap: float formatting, key ordering, integer-vs-float
/// typing and non-ASCII escaping all differ, and any one of them silently breaks every signature.
/// Signing the exact bytes Home Assistant produced removes the whole class of problem — the agent
/// verifies the string first and only then parses it.
/// </para>
/// <para>
/// Fields are length-prefixed rather than delimiter-joined, so no field's content can be crafted
/// to look like a field boundary.
/// </para>
/// </summary>
public static class PayloadSigner
{
    /// <summary>Length-prefixed encoding: each field becomes <c>&lt;utf8 byte count&gt;:&lt;bytes&gt;</c>.</summary>
    public static byte[] BuildSigningInput(
        int version, string deviceId, string op, string nonce, long timestamp, string payload)
    {
        using var buffer = new MemoryStream();

        AppendField(buffer, version.ToString(CultureInfo.InvariantCulture));
        AppendField(buffer, deviceId);
        AppendField(buffer, op);
        AppendField(buffer, nonce);
        AppendField(buffer, timestamp.ToString(CultureInfo.InvariantCulture));
        AppendField(buffer, payload);

        return buffer.ToArray();
    }

    private static void AppendField(Stream target, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var prefix = Encoding.UTF8.GetBytes($"{bytes.Length}:");
        target.Write(prefix);
        target.Write(bytes);
    }

    public static byte[] ComputeSignature(byte[] key, byte[] signingInput)
        => HMACSHA256.HashData(key, signingInput);

    /// <summary>Constant-time comparison — a timing-variable check would leak the expected MAC.</summary>
    public static bool SignatureMatches(byte[] key, byte[] signingInput, string base64Signature)
    {
        byte[] provided;
        try
        {
            provided = Convert.FromBase64String(base64Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = ComputeSignature(key, signingInput);
        return CryptographicOperations.FixedTimeEquals(expected, provided);
    }
}
