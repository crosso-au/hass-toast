using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using HassToast.Agent.Config;

namespace HassToast.Agent.Transport;

/// <summary>
/// Certificate policy for talking to Home Assistant, written once because two clients need it:
/// the agent's long-lived websocket and the setup wizard's admin calls.
/// <para>
/// Keeping them together is not tidiness. A wizard that trusted a certificate the agent will
/// later refuse would report a successful install and leave behind a machine that never connects
/// — and the person would have no reason to suspect the certificate, having just watched the
/// wizard talk to the same server.
/// </para>
/// </summary>
public static class HomeAssistantTls
{
    /// <summary>
    /// The validation callback this configuration calls for, or null to leave standard chain
    /// validation in place — which is the default and the one to prefer.
    /// </summary>
    public static RemoteCertificateValidationCallback? Validator(
        HomeAssistantConfig config, ILogger? log = null)
    {
        var pin = config.PinnedSpkiSha256;

        if (!string.IsNullOrWhiteSpace(pin))
        {
            // Pinning supersedes chain validation: an internal self-signed certificate is
            // accepted, but only that exact key.
            return (_, cert, _, _) =>
            {
                if (cert is null) return false;

                var actual = SpkiFingerprint(cert);
                var match = CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actual), Encoding.ASCII.GetBytes(pin));

                if (!match)
                {
                    log?.LogError(
                        "Certificate pin mismatch. Expected {Expected}, server presented {Actual}.",
                        pin, actual);
                }

                return match;
            };
        }

        if (config.VerifyTls) return null;

        log?.LogWarning("TLS verification is DISABLED for the Home Assistant connection. " +
                        "Set homeAssistant.pinnedSpkiSha256 instead - it accepts a self-signed " +
                        "certificate without accepting every certificate.");

        return (_, _, _, _) => true;
    }

    /// <summary>Base64 SHA-256 over the certificate's SubjectPublicKeyInfo.</summary>
    public static string SpkiFingerprint(X509Certificate certificate)
    {
        using var cert = new X509Certificate2(certificate);
        return Convert.ToBase64String(SHA256.HashData(cert.PublicKey.ExportSubjectPublicKeyInfo()));
    }

    /// <summary>An <see cref="HttpMessageHandler"/> applying this configuration's certificate policy.</summary>
    public static HttpMessageHandler CreateHttpHandler(HomeAssistantConfig config, ILogger? log = null)
    {
        var handler = new HttpClientHandler();
        var validator = Validator(config, log);

        if (validator is not null)
        {
            handler.ServerCertificateCustomValidationCallback =
                (_, cert, chain, errors) => validator(handler, cert, chain, errors);
        }

        return handler;
    }
}
