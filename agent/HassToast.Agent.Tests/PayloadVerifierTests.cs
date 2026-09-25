using System.Text;
using System.Text.Json;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class PayloadVerifierTests
{
    private static readonly byte[] Key = Convert.FromBase64String("bnJPQjRhVjhqTmR6UWtMeVh3RWdVc0h0Q21QZjJZaWs=");
    private const string Device = "test-device";

    private static AgentConfig Config(Action<AgentConfig>? tweak = null)
    {
        var config = new AgentConfig
        {
            HomeAssistant = { Url = "https://ha.local" },
            DeviceId = Device,
        };
        tweak?.Invoke(config);
        return config;
    }

    private static PayloadVerifier VerifierFor(AgentConfig? config = null, ReplayCache? cache = null)
        => new(config ?? Config(), () => Key, cache);

    /// <summary>Builds a correctly signed envelope, then lets a test corrupt one field at a time.</summary>
    private static JsonElement Envelope(
        string? device = null,
        int version = 1,
        string op = "send",
        string? nonce = null,
        long? ts = null,
        string? payload = null,
        string? signature = null,
        byte[]? signingKey = null)
    {
        device ??= Device;
        nonce ??= Guid.NewGuid().ToString("N");
        ts ??= DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        payload ??= """{"visual":{"text":["hello"]}}""";

        var input = PayloadSigner.BuildSigningInput(version, device, op, nonce, ts.Value, payload);
        signature ??= Convert.ToBase64String(PayloadSigner.ComputeSignature(signingKey ?? Key, input));

        var json = JsonSerializer.Serialize(new
        {
            v = version,
            device_id = device,
            op,
            nonce,
            ts = ts.Value,
            payload,
            sig = signature,
        });

        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void Accepts_a_correctly_signed_envelope()
    {
        var result = VerifierFor().Verify(Envelope(), DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.Accepted, result.Outcome);
        Assert.NotNull(result.Payload);
        Assert.Equal(["hello"], result.Payload!.Visual.Text);
    }

    [Fact]
    public void Rejects_a_signature_made_with_a_different_key()
    {
        var otherKey = SecretStore.GenerateSigningKey();
        var result = VerifierFor().Verify(Envelope(signingKey: otherKey), DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.BadSignature, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("AAAA")]
    public void Rejects_malformed_signatures(string signature)
    {
        var result = VerifierFor().Verify(Envelope(signature: signature), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.BadSignature, result.Outcome);
    }

    [Fact]
    public void Rejects_a_payload_tampered_with_after_signing()
    {
        // Sign one body, ship another — the classic substitution attack.
        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signed = """{"visual":{"text":["harmless"]}}""";
        var swapped = """{"visual":{"text":["malicious"]}}""";

        var input = PayloadSigner.BuildSigningInput(1, Device, "send", nonce, ts, signed);
        var signature = Convert.ToBase64String(PayloadSigner.ComputeSignature(Key, input));

        var result = VerifierFor().Verify(
            Envelope(nonce: nonce, ts: ts, payload: swapped, signature: signature),
            DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.BadSignature, result.Outcome);
    }

    [Fact]
    public void Ignores_envelopes_addressed_to_another_device()
    {
        var result = VerifierFor().Verify(Envelope(device: "someone-elses-pc"), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.NotForThisDevice, result.Outcome);
    }

    [Fact]
    public void Rejects_an_unknown_schema_version()
    {
        var result = VerifierFor().Verify(Envelope(version: 99), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.UnsupportedVersion, result.Outcome);
    }

    [Fact]
    public void Rejects_an_unknown_operation()
    {
        var result = VerifierFor().Verify(Envelope(op: "exec"), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.UnknownOperation, result.Outcome);
    }

    [Theory]
    [InlineData(-600)]  // far in the past: a captured envelope replayed later
    [InlineData(600)]   // far in the future: a forged timestamp to extend validity
    public void Rejects_envelopes_outside_the_time_window(int offsetSeconds)
    {
        var now = DateTimeOffset.UtcNow;
        var envelope = Envelope(ts: now.AddSeconds(offsetSeconds).ToUnixTimeSeconds());

        var result = VerifierFor().Verify(envelope, now);

        Assert.Equal(VerificationOutcome.OutsideTimeWindow, result.Outcome);
    }

    [Fact]
    public void Accepts_modest_clock_skew_in_both_directions()
    {
        var now = DateTimeOffset.UtcNow;
        var verifier = VerifierFor();

        Assert.Equal(VerificationOutcome.Accepted,
            verifier.Verify(Envelope(ts: now.AddSeconds(-30).ToUnixTimeSeconds()), now).Outcome);
        Assert.Equal(VerificationOutcome.Accepted,
            verifier.Verify(Envelope(ts: now.AddSeconds(30).ToUnixTimeSeconds()), now).Outcome);
    }

    [Fact]
    public void Rejects_a_replayed_nonce()
    {
        var verifier = VerifierFor();
        var envelope = Envelope();
        var now = DateTimeOffset.UtcNow;

        Assert.Equal(VerificationOutcome.Accepted, verifier.Verify(envelope, now).Outcome);
        // Byte-identical resend: valid signature, valid timestamp, already used nonce.
        Assert.Equal(VerificationOutcome.Replayed, verifier.Verify(envelope, now).Outcome);
    }

    [Fact]
    public void Rejects_an_envelope_with_no_nonce()
    {
        var result = VerifierFor().Verify(Envelope(nonce: ""), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void Rejects_a_payload_that_is_not_valid_json()
    {
        var result = VerifierFor().Verify(Envelope(payload: "{not json"), DateTimeOffset.UtcNow);
        Assert.Equal(VerificationOutcome.Malformed, result.Outcome);
    }

    [Theory]
    [InlineData(0x00)] // NUL — the classic string-truncation smuggler
    [InlineData(0x07)] // BEL
    [InlineData(0x1B)] // ESC — terminal escape sequences, were this ever logged raw
    [InlineData(0x0A)] // raw newline inside a JSON string
    public void Rejects_a_payload_containing_raw_control_characters(int codePoint)
    {
        // Raw control bytes inside a JSON string are invalid per the spec and must be refused,
        // not silently repaired on the way to the toast. Given as code points rather than
        // literals: an invisible character in the source would make this pass for a reason no
        // reader could see, and would not survive a reformat.
        var control = (char)codePoint;
        var payload = "{\"visual\":{\"text\":[\"bad" + control + "value\"]}}";

        var result = VerifierFor().Verify(Envelope(payload: payload), DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.Malformed, result.Outcome);
    }

    [Fact]
    public void Accepts_control_characters_that_are_properly_escaped()
    {
        // Correctly escaped they are legal JSON and legal toast text; the builder escapes them
        // again on the way into the notification XML.
        var payload = """{"visual":{"text":["line one\nline two\ttabbed"]}}""";

        var result = VerifierFor().Verify(Envelope(payload: payload), DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.Accepted, result.Outcome);
        Assert.Contains("line two", result.Payload!.Visual.Text[0]);
    }

    [Fact]
    public void Signature_check_precedes_payload_parsing()
    {
        // Unparseable body AND a bad signature: the signature must be what rejects it, so an
        // unauthenticated caller can never reach the JSON parser.
        var result = VerifierFor().Verify(
            Envelope(payload: "{{{ not json", signingKey: SecretStore.GenerateSigningKey()),
            DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.BadSignature, result.Outcome);
    }

    [Fact]
    public void Signature_covers_every_envelope_field()
    {
        // Re-signing the body alone must not validate a changed op, nonce or timestamp.
        var now = DateTimeOffset.UtcNow;
        var payload = """{"visual":{"text":["x"]}}""";
        var nonce = "fixed-nonce";
        var ts = now.ToUnixTimeSeconds();

        var input = PayloadSigner.BuildSigningInput(1, Device, "send", nonce, ts, payload);
        var signature = Convert.ToBase64String(PayloadSigner.ComputeSignature(Key, input));

        // Same signature, different op.
        var tampered = Envelope(op: "clear", nonce: nonce, ts: ts, payload: payload, signature: signature);
        Assert.Equal(VerificationOutcome.BadSignature, VerifierFor().Verify(tampered, now).Outcome);
    }

    [Fact]
    public void Signature_verification_can_be_disabled_but_is_on_by_default()
    {
        Assert.True(new AgentConfig().Security.RequireSignature);

        var config = Config(c => c.Security.RequireSignature = false);
        var result = VerifierFor(config).Verify(Envelope(signature: "garbage"), DateTimeOffset.UtcNow);

        Assert.Equal(VerificationOutcome.Accepted, result.Outcome);
    }
}

public sealed class PayloadSignerTests
{
    [Fact]
    public void Length_prefixing_stops_field_boundaries_being_forged()
    {
        // Without length prefixes these two would flatten to the same signed bytes, letting a
        // crafted device id impersonate a different op.
        var a = PayloadSigner.BuildSigningInput(1, "device", "send", "n", 1, "p");
        var b = PayloadSigner.BuildSigningInput(1, "device:send", "", "n", 1, "p");

        Assert.NotEqual(Convert.ToBase64String(a), Convert.ToBase64String(b));
    }

    [Fact]
    public void Signing_input_is_stable_for_identical_fields()
    {
        var a = PayloadSigner.BuildSigningInput(1, "d", "send", "n", 1700000000, """{"a":1}""");
        var b = PayloadSigner.BuildSigningInput(1, "d", "send", "n", 1700000000, """{"a":1}""");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Signing_input_encodes_non_ascii_as_utf8()
    {
        // The Python side encodes UTF-8; a different encoding here would break every signature
        // containing an accent or emoji.
        var input = PayloadSigner.BuildSigningInput(1, "d", "send", "n", 1, "café 🔔");
        var text = Encoding.UTF8.GetString(input);

        Assert.Contains("café 🔔", text);
        Assert.Contains($"{Encoding.UTF8.GetByteCount("café 🔔")}:", text);
    }

    [Fact]
    public void Known_answer_test_pins_the_wire_format()
    {
        // Frozen expected value. If this changes, the Python integration must change in step —
        // that is the whole point of pinning it.
        var key = Encoding.UTF8.GetBytes("test-key");
        var input = PayloadSigner.BuildSigningInput(1, "desk", "send", "abc", 1700000000, """{"visual":{"text":["hi"]}}""");

        Assert.Equal("1:14:desk4:send3:abc10:170000000026:{\"visual\":{\"text\":[\"hi\"]}}",
            Encoding.UTF8.GetString(input));

        var signature = Convert.ToBase64String(PayloadSigner.ComputeSignature(key, input));
        Assert.Equal(44, signature.Length); // base64 of 32 bytes
    }
}

public sealed class ReplayCacheTests
{
    [Fact]
    public void Accepts_a_nonce_once()
    {
        var cache = new ReplayCache(TimeSpan.FromMinutes(5));
        var now = DateTimeOffset.UtcNow;

        Assert.True(cache.TryRegister("a", now));
        Assert.False(cache.TryRegister("a", now));
    }

    [Fact]
    public void Forgets_nonces_once_they_fall_outside_retention()
    {
        // Beyond the window the timestamp check rejects them anyway, so holding them is waste.
        var cache = new ReplayCache(TimeSpan.FromMinutes(5));
        var start = DateTimeOffset.UtcNow;

        Assert.True(cache.TryRegister("a", start));
        Assert.True(cache.TryRegister("b", start.AddMinutes(6)));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Fails_closed_when_full_rather_than_evicting_a_live_nonce()
    {
        // Dropping the oldest entry to make room would make that nonce replayable, which is
        // exactly what the cache exists to prevent.
        var cache = new ReplayCache(TimeSpan.FromMinutes(5), capacity: 3);
        var now = DateTimeOffset.UtcNow;

        Assert.True(cache.TryRegister("a", now));
        Assert.True(cache.TryRegister("b", now));
        Assert.True(cache.TryRegister("c", now));
        Assert.False(cache.TryRegister("d", now));

        // The earlier nonces are still protected.
        Assert.False(cache.TryRegister("a", now));
    }

    [Fact]
    public void Is_safe_under_concurrent_registration()
    {
        var cache = new ReplayCache(TimeSpan.FromMinutes(5), capacity: 10_000);
        var now = DateTimeOffset.UtcNow;
        var accepted = 0;

        Parallel.For(0, 500, i =>
        {
            // Every thread races on the same 50 nonces; exactly 50 may win.
            if (cache.TryRegister($"nonce-{i % 50}", now)) Interlocked.Increment(ref accepted);
        });

        Assert.Equal(50, accepted);
    }
}
