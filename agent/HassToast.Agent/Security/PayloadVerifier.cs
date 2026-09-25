using System.Text.Json;
using HassToast.Agent.Config;
using HassToast.Agent.Toasts;

namespace HassToast.Agent.Security;

public enum VerificationOutcome
{
    Accepted,
    Malformed,
    UnsupportedVersion,
    /// <summary>Addressed to a different machine. Expected and unremarkable.</summary>
    NotForThisDevice,
    UnknownOperation,
    BadSignature,
    OutsideTimeWindow,
    Replayed,
}

public sealed record VerificationResult(
    VerificationOutcome Outcome,
    ToastEnvelope? Envelope = null,
    ToastPayload? Payload = null,
    string? Detail = null)
{
    public bool Accepted => Outcome == VerificationOutcome.Accepted;

    public static VerificationResult Fail(VerificationOutcome outcome, string? detail = null)
        => new(outcome, Detail: detail);
}

/// <summary>
/// Authenticates an inbound envelope before any of it is trusted.
/// <para>
/// Order matters here. The signature is checked before the payload is parsed, so malformed or
/// hostile JSON from an unauthenticated source is never handed to the parser. The device check
/// comes first only because it is the cheapest way to discard traffic meant for another machine.
/// </para>
/// </summary>
public sealed class PayloadVerifier
{
    /// <summary>Schema version this agent speaks.</summary>
    public const int SupportedVersion = 1;

    private readonly AgentConfig _config;
    private readonly ReplayCache _replayCache;
    private readonly Func<byte[]> _signingKey;

    public PayloadVerifier(AgentConfig config, Func<byte[]> signingKey, ReplayCache? replayCache = null)
    {
        _config = config;
        _signingKey = signingKey;
        _replayCache = replayCache
            ?? new ReplayCache(TimeSpan.FromSeconds(config.Security.TimestampToleranceSeconds * 2));
    }

    public VerificationResult Verify(JsonElement raw, DateTimeOffset now)
    {
        ToastEnvelope? envelope;
        try
        {
            envelope = raw.Deserialize<ToastEnvelope>(ToastJson.Options);
        }
        catch (JsonException ex)
        {
            return VerificationResult.Fail(VerificationOutcome.Malformed, ex.Message);
        }

        if (envelope is null)
            return VerificationResult.Fail(VerificationOutcome.Malformed, "empty envelope");

        // Cheapest discriminator first: most traffic on a shared bus is for someone else.
        if (!string.Equals(envelope.DeviceId, _config.DeviceId, StringComparison.OrdinalIgnoreCase))
            return VerificationResult.Fail(VerificationOutcome.NotForThisDevice, envelope.DeviceId);

        if (envelope.Version != SupportedVersion)
            return VerificationResult.Fail(VerificationOutcome.UnsupportedVersion,
                $"envelope v{envelope.Version}, agent speaks v{SupportedVersion}");

        if (!ToastOp.IsKnown(envelope.Op))
            return VerificationResult.Fail(VerificationOutcome.UnknownOperation, envelope.Op);

        if (string.IsNullOrWhiteSpace(envelope.Nonce))
            return VerificationResult.Fail(VerificationOutcome.Malformed, "missing nonce");

        if (_config.Security.RequireSignature)
        {
            var signingInput = PayloadSigner.BuildSigningInput(
                envelope.Version, envelope.DeviceId, envelope.Op,
                envelope.Nonce, envelope.Timestamp, envelope.Payload);

            if (!PayloadSigner.SignatureMatches(_signingKey(), signingInput, envelope.Signature))
                return VerificationResult.Fail(VerificationOutcome.BadSignature);
        }

        // Timestamp and replay are checked after the signature: both mutate or consult shared
        // state, and an unauthenticated caller should not be able to reach either.
        var skew = now - DateTimeOffset.FromUnixTimeSeconds(envelope.Timestamp);
        var tolerance = TimeSpan.FromSeconds(_config.Security.TimestampToleranceSeconds);
        if (skew > tolerance || skew < -tolerance)
            return VerificationResult.Fail(VerificationOutcome.OutsideTimeWindow,
                $"{skew.TotalSeconds:F0}s outside a ±{tolerance.TotalSeconds:F0}s window");

        if (!_replayCache.TryRegister(envelope.Nonce, now))
            return VerificationResult.Fail(VerificationOutcome.Replayed, envelope.Nonce);

        // Authenticated: only now is it safe to parse the body.
        ToastPayload? payload = null;
        if (envelope.Op is ToastOp.Send or ToastOp.Update or ToastOp.Remove)
        {
            try
            {
                payload = JsonSerializer.Deserialize<ToastPayload>(envelope.Payload, ToastJson.Options);
            }
            catch (JsonException ex)
            {
                return VerificationResult.Fail(VerificationOutcome.Malformed, ex.Message);
            }

            if (payload is null)
                return VerificationResult.Fail(VerificationOutcome.Malformed, "empty payload");
        }

        return new VerificationResult(VerificationOutcome.Accepted, envelope, payload);
    }
}
