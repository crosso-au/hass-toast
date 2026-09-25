using System.Text;
using System.Text.Json;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using HassToast.Agent.Toasts;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// Verifies envelopes the Home Assistant integration actually produced, in
/// <c>docs/sample-envelopes.json</c>.
/// <para>
/// The signing vectors prove both sides agree on the bytes they hash. They say nothing about
/// whether the agent accepts what the integration emits — field names, payload nesting and value
/// types all sit outside the signature, so the two could sign identically and still fail to
/// interoperate. These fixtures run the integration's real output through the real verifier.
/// </para>
/// </summary>
public sealed class IntegrationEnvelopeTests
{
    private sealed record Fixture(string Name, JsonElement Envelope);

    private static (byte[] Key, string DeviceId, DateTimeOffset Now, List<Fixture> Fixtures) Load()
    {
        var path = FindFixtureFile();
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;

        var fixtures = root.GetProperty("envelopes").EnumerateArray()
            .Select(e => new Fixture(
                e.GetProperty("name").GetString()!,
                e.GetProperty("envelope").Clone()))
            .ToList();

        return (
            Convert.FromBase64String(root.GetProperty("key_base64").GetString()!),
            root.GetProperty("device_id").GetString()!,
            DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("timestamp").GetInt64()),
            fixtures);
    }

    private static string FindFixtureFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "sample-envelopes.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "docs/sample-envelopes.json was not found. Regenerate it with hass/tools/make_envelope.py.");
    }

    private static PayloadVerifier VerifierFor(byte[] key, string deviceId)
        => new(
            new AgentConfig { HomeAssistant = { Url = "https://ha.local" }, DeviceId = deviceId },
            () => key);

    [Fact]
    public void Every_integration_envelope_is_accepted()
    {
        var (key, deviceId, now, fixtures) = Load();

        Assert.NotEmpty(fixtures);

        foreach (var fixture in fixtures)
        {
            // A fresh verifier per fixture: they deliberately share a timestamp, and one replay
            // cache across all of them would reject the second onwards for reused nonces.
            var result = VerifierFor(key, deviceId).Verify(fixture.Envelope, now);

            Assert.True(result.Accepted,
                $"envelope '{fixture.Name}' was rejected: {result.Outcome} {result.Detail}");
        }
    }

    [Fact]
    public void Send_envelopes_parse_into_a_renderable_payload()
    {
        var (key, deviceId, now, fixtures) = Load();

        foreach (var fixture in fixtures.Where(f =>
                     f.Envelope.GetProperty("op").GetString() == "send"))
        {
            var result = VerifierFor(key, deviceId).Verify(fixture.Envelope, now);

            Assert.NotNull(result.Payload);
            Assert.NotEmpty(result.Payload!.Visual.Text);
        }
    }

    [Fact]
    public void The_rich_payload_survives_the_data_block_intact()
    {
        // Proves the integration's `data` passthrough lines up with the agent's schema — the
        // most likely place for the two to drift apart.
        var (key, deviceId, now, fixtures) = Load();

        var fixture = fixtures.Single(f => f.Name == "rich payload through the data block");
        var result = VerifierFor(key, deviceId).Verify(fixture.Envelope, now);

        Assert.True(result.Accepted);

        var payload = result.Payload!;
        Assert.Equal("door-front", payload.Tag);
        Assert.Equal("security", payload.Group);
        Assert.Equal("reminder", payload.Scenario);
        Assert.Equal("Frigate", payload.Visual.Attribution);
        Assert.Equal(["Front door", "Motion detected"], payload.Visual.Text);

        var button = Assert.Single(payload.Buttons);
        Assert.Equal("Unlock", button.Content);
        Assert.Equal("Success", button.Style);
        Assert.Equal("unlock", button.Args!["action"]);
    }

    [Fact]
    public void A_progress_update_carries_values_the_composer_can_use()
    {
        var (key, deviceId, now, fixtures) = Load();

        var fixture = fixtures.Single(f => f.Name == "progress update");
        var result = VerifierFor(key, deviceId).Verify(fixture.Envelope, now);

        Assert.True(result.Accepted);
        Assert.Equal("update", result.Envelope!.Op);

        var progress = result.Payload!.Visual.Progress;
        Assert.NotNull(progress);
        Assert.Equal(0.8, progress!.Value);
        Assert.Equal("Uploading...", progress.Status);
    }

    [Fact]
    public void Non_ascii_content_round_trips()
    {
        // The signature covers UTF-8 bytes, so a mismatched encoding anywhere in the chain
        // shows up as a verification failure rather than as mangled text.
        var (key, deviceId, now, fixtures) = Load();

        var fixture = fixtures.Single(f => f.Name == "non-ascii content");
        var result = VerifierFor(key, deviceId).Verify(fixture.Envelope, now);

        Assert.True(result.Accepted);
        Assert.Contains("Küche", result.Payload!.Visual.Text);
        Assert.Contains("Kaffee ist fertig ☕", result.Payload.Visual.Text);
    }

    [Fact]
    public void A_tampered_integration_envelope_is_rejected()
    {
        var (key, deviceId, now, fixtures) = Load();

        var original = fixtures[0].Envelope;
        var tampered = JsonSerializer.SerializeToElement(new
        {
            v = original.GetProperty("v").GetInt32(),
            device_id = original.GetProperty("device_id").GetString(),
            op = original.GetProperty("op").GetString(),
            nonce = original.GetProperty("nonce").GetString(),
            ts = original.GetProperty("ts").GetInt64(),
            payload = """{"visual":{"text":["swapped in"]}}""",
            sig = original.GetProperty("sig").GetString(),
        });

        var result = VerifierFor(key, deviceId).Verify(tampered, now);

        Assert.Equal(VerificationOutcome.BadSignature, result.Outcome);
    }

    [Fact]
    public void Envelopes_for_another_device_are_ignored()
    {
        var (key, _, now, fixtures) = Load();

        var result = VerifierFor(key, "a-different-machine").Verify(fixtures[0].Envelope, now);

        Assert.Equal(VerificationOutcome.NotForThisDevice, result.Outcome);
    }
}
