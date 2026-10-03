using System.Security.Cryptography;
using System.Text;

namespace HassToast.Agent.Updates;

/// <summary>
/// Proves a downloaded installer is one CrossboxLabs released.
/// <para>
/// HTTPS and "it came from the GitHub repository" only hold until someone gets into that account.
/// After that, every agent that updates itself would run whatever they uploaded. So each release's
/// installer is signed at build time with a private key that never leaves the release machine, and
/// the agent refuses anything that does not verify against the public half below.
/// </para>
/// <para>
/// The signature covers the version and file name as well as the bytes. Signing only the bytes
/// would let an older, genuinely signed installer be re-attached to a newer release and offered as
/// an update - a downgrade to whatever that older version got wrong.
/// </para>
/// <para>
/// This file is also compiled into the private release-signing tool, so the format is written
/// down once. It must depend on nothing outside the base class library.
/// </para>
/// </summary>
public static class UpdateSignature
{
    /// <summary>
    /// ECDSA P-256 public key (SubjectPublicKeyInfo, base64). Replacing it means every agent
    /// already installed rejects every future release, so treat it as permanent.
    /// </summary>
    public const string PublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEvcueorvXD1u6MFMkVaUI2Ljw3MM6D24fvp3HxDRr/uHljaVrohBO0mnJQ/Hnix+SgMVQt4uiRPkixpQ8D7jkLw==";

    /// <summary>The release asset carrying the signature sits beside the installer with this suffix.</summary>
    public const string SignatureSuffix = ".sig";

    private const string Domain = "hass-toast-update";
    private const string FormatVersion = "v1";

    /// <summary>The exact bytes that are signed.</summary>
    public static byte[] BuildSigningInput(string version, string fileName, byte[] sha256)
        => Encoding.UTF8.GetBytes(string.Join('\n',
            Domain,
            FormatVersion,
            version,
            fileName,
            Convert.ToHexStringLower(sha256)));

    /// <summary>Encodes a signature as the text that goes in the .sig file.</summary>
    public static string Encode(byte[] signature) => Convert.ToBase64String(signature);

    /// <summary>
    /// Checks a signature against the embedded key. Never throws: anything malformed is simply
    /// a signature that does not verify.
    /// </summary>
    public static bool Verify(string version, string fileName, byte[] sha256, string signatureText)
        => Verify(PublicKey, version, fileName, sha256, signatureText);

    /// <summary>As <see cref="Verify(string,string,byte[],string)"/>, against a given key. For tests and the signing tool.</summary>
    public static bool Verify(string publicKey, string version, string fileName, byte[] sha256, string signatureText)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureText.Trim());

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

            return key.VerifyData(
                BuildSigningInput(version, fileName, sha256), signature, HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }
}
