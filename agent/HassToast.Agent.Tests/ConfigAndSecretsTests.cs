using System.Security.Cryptography;
using HassToast.Agent.Config;
using HassToast.Agent.Security;
using Xunit;

namespace HassToast.Agent.Tests;

public sealed class HomeAssistantConfigTests
{
    [Theory]
    // A bare base URL gains the API path and the scheme is upgraded to WebSocket.
    [InlineData("https://ha.local", "wss://ha.local/api/websocket")]
    [InlineData("http://ha.local", "ws://ha.local/api/websocket")]
    [InlineData("https://ha.local:8123", "wss://ha.local:8123/api/websocket")]
    [InlineData("http://192.168.1.10:8123/", "ws://192.168.1.10:8123/api/websocket")]
    // An already-complete WebSocket URL is left alone.
    [InlineData("wss://ha.local/api/websocket", "wss://ha.local/api/websocket")]
    [InlineData("ws://ha.local:8123/api/websocket", "ws://ha.local:8123/api/websocket")]
    public void Normalises_urls_to_the_websocket_endpoint(string input, string expected)
    {
        var config = new HomeAssistantConfig { Url = input };
        Assert.Equal(expected, config.WebSocketUri().ToString());
    }

    [Fact]
    public void Default_https_port_is_not_written_into_the_endpoint()
    {
        var config = new HomeAssistantConfig { Url = "https://ha.example.com:443" };
        Assert.Equal("wss://ha.example.com/api/websocket", config.WebSocketUri().ToString());
    }
}

public sealed class AgentConfigTests
{
    [Fact]
    public void Rejects_missing_url()
    {
        var config = new AgentConfig();
        var ex = Assert.Throws<InvalidOperationException>(config.Validate);
        Assert.Contains("url", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ftp://ha.local")]
    [InlineData("file:///etc/passwd")]
    public void Rejects_unsupported_schemes(string url)
    {
        var config = new AgentConfig { HomeAssistant = { Url = url } };
        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Rejects_empty_device_id()
    {
        var config = new AgentConfig { HomeAssistant = { Url = "https://ha.local" }, DeviceId = "  " };
        Assert.Throws<InvalidOperationException>(config.Validate);
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        var dir = Directory.CreateTempSubdirectory("hasstoast-cfg");
        try
        {
            var path = Path.Combine(dir.FullName, "config.json");
            var original = new AgentConfig
            {
                HomeAssistant = { Url = "https://ha.local:8123", VerifyTls = false, PinnedSpkiSha256 = "abc=" },
                DeviceId = "desk",
                EventType = "custom_event",
            };
            original.Security.AllowedUriSchemes = ["https"];
            original.Save(path);

            var loaded = AgentConfig.Load(path);

            Assert.Equal("https://ha.local:8123", loaded.HomeAssistant.Url);
            Assert.False(loaded.HomeAssistant.VerifyTls);
            Assert.Equal("abc=", loaded.HomeAssistant.PinnedSpkiSha256);
            Assert.Equal("desk", loaded.DeviceId);
            Assert.Equal("custom_event", loaded.EventType);
            Assert.Equal(["https"], loaded.Security.AllowedUriSchemes);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Missing_file_yields_usable_defaults()
    {
        var config = AgentConfig.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json"));

        Assert.NotEmpty(config.DeviceId);
        Assert.Equal("hass_toast_notify", config.EventType);
        Assert.True(config.Security.RequireSignature);
        // The dangerous schemes must never be on by default.
        Assert.DoesNotContain("file", config.Security.AllowedUriSchemes);
        Assert.DoesNotContain("ms-settings", config.Security.AllowedUriSchemes);
        Assert.DoesNotContain("file", config.Security.AllowedImageSchemes);
    }
}

public sealed class StartupRegistrationTests
{
    // Explorer's StartupApproved blob: the low bit of the first byte is the switch, the rest is
    // the timestamp of the last change. Reading it wrongly means the tray tick claims the agent
    // will start when Windows has been told not to.
    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81 }, false)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x07, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, false)]
    public void Reads_the_task_manager_approval_byte(byte[] value, bool approved)
        => Assert.Equal(approved, StartupRegistration.IsApproved(value));

    [Theory]
    [InlineData(null)]
    [InlineData(new byte[0])]
    public void No_approval_record_means_approved(byte[]? value)
        => Assert.True(StartupRegistration.IsApproved(value));

    [Fact]
    public void The_registered_command_is_quoted()
    {
        // An unquoted path with a space in it is read as a command plus arguments, which is how
        // a startup entry ends up launching nothing at all.
        var command = StartupRegistration.ExpectedCommand;

        Assert.StartsWith("\"", command, StringComparison.Ordinal);
        Assert.EndsWith("\"", command, StringComparison.Ordinal);
        Assert.Equal(Environment.ProcessPath, command.Trim('"'));
    }
}

public sealed class SecretStoreTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("hasstoast-secrets");

    private string NewPath() => Path.Combine(_dir.FullName, $"{Guid.NewGuid():N}.dat");

    [Fact]
    public void Round_trips_secrets()
    {
        var path = NewPath();
        var key = SecretStore.GenerateSigningKey();

        new SecretStore(path).Save(new AgentSecrets("token-value", key));
        var loaded = new SecretStore(path).Load();

        Assert.Equal("token-value", loaded.AccessToken);
        Assert.Equal(key, loaded.SigningKey);
    }

    [Fact]
    public void Stored_bytes_do_not_contain_the_plaintext_token()
    {
        var path = NewPath();
        new SecretStore(path).Save(new AgentSecrets("super-secret-token", SecretStore.GenerateSigningKey()));

        var onDisk = File.ReadAllBytes(path);
        var plaintext = "super-secret-token"u8.ToArray();

        Assert.False(ContainsSequence(onDisk, plaintext),
            "the access token appears verbatim in the stored file");
    }

    [Fact]
    public void Load_without_a_stored_file_explains_how_to_fix_it()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new SecretStore(NewPath()).Load());
        Assert.Contains("--setup", ex.Message);
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var path = NewPath();
        new SecretStore(path).Save(new AgentSecrets("token", SecretStore.GenerateSigningKey()));

        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Throws<InvalidOperationException>(() => new SecretStore(path).Load());
    }

    [Fact]
    public void Generated_signing_keys_are_256_bit_and_distinct()
    {
        var a = SecretStore.GenerateSigningKey();
        var b = SecretStore.GenerateSigningKey();

        Assert.Equal(32, a.Length);
        Assert.Equal(32, b.Length);
        Assert.False(CryptographicOperations.FixedTimeEquals(a, b));
    }

    [Fact]
    public void Rotating_the_signing_key_preserves_the_access_token()
    {
        // Rotation exists for when a key is exposed. Forcing the token to be re-entered as well
        // would make people avoid rotating, which is the opposite of what we want.
        var path = NewPath();
        var store = new SecretStore(path);
        var original = SecretStore.GenerateSigningKey();
        store.Save(new AgentSecrets("token-stays-put", original));

        var current = store.Load();
        var rotated = SecretStore.GenerateSigningKey();
        store.Save(current with { SigningKey = rotated });

        var loaded = new SecretStore(path).Load();

        Assert.Equal("token-stays-put", loaded.AccessToken);
        Assert.Equal(rotated, loaded.SigningKey);
        Assert.False(CryptographicOperations.FixedTimeEquals(original, loaded.SigningKey),
            "the old signing key survived rotation");
    }

    [Fact]
    public void A_rotated_out_key_no_longer_verifies_anything_it_signed()
    {
        var config = new Config.AgentConfig { HomeAssistant = { Url = "https://ha.local" }, DeviceId = "d" };
        var oldKey = SecretStore.GenerateSigningKey();
        var newKey = SecretStore.GenerateSigningKey();

        var payload = """{"visual":{"text":["signed with the compromised key"]}}""";
        var nonce = Guid.NewGuid().ToString("N");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var input = PayloadSigner.BuildSigningInput(1, "d", "send", nonce, ts, payload);
        var oldSignature = Convert.ToBase64String(PayloadSigner.ComputeSignature(oldKey, input));

        var envelope = System.Text.Json.JsonSerializer.SerializeToElement(new
        {
            v = 1, device_id = "d", op = "send", nonce, ts, payload, sig = oldSignature,
        });

        // The agent now holds only the new key.
        var verifier = new PayloadVerifier(config, () => newKey);

        Assert.Equal(VerificationOutcome.BadSignature,
            verifier.Verify(envelope, DateTimeOffset.UtcNow).Outcome);
    }

    [Fact]
    public void Delete_removes_the_store()
    {
        var path = NewPath();
        var store = new SecretStore(path);
        store.Save(new AgentSecrets("token", SecretStore.GenerateSigningKey()));
        Assert.True(store.Exists);

        store.Delete();

        Assert.False(store.Exists);
    }

    private static bool ContainsSequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch { /* best effort */ }
    }
}
