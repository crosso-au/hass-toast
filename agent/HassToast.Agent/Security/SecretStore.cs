using System.Security.Cryptography;
using System.Text.Json;

namespace HassToast.Agent.Security;

/// <summary>The two secrets the agent holds. Neither is ever written in plaintext or logged.</summary>
/// <param name="AccessToken">Home Assistant long-lived access token.</param>
/// <param name="SigningKey">Shared HMAC key, matching the one held by the HA integration.</param>
public sealed record AgentSecrets(string AccessToken, byte[] SigningKey);

/// <summary>
/// Stores agent secrets encrypted with DPAPI under the current user's scope, so the
/// ciphertext is useless to another account on the same machine and to anyone who
/// copies the file off it.
/// </summary>
public sealed class SecretStore
{
    // Bound to this application so another DPAPI caller cannot trivially decrypt the blob.
    private static readonly byte[] Entropy = "HassToast.v1"u8.ToArray();

    private readonly string _path;

    public SecretStore(string? path = null)
        => _path = path ?? Path.Combine(Config.AgentConfig.DefaultDirectory, "secrets.dat");

    public string FilePath => _path;

    public bool Exists => File.Exists(_path);

    public void Save(AgentSecrets secrets)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new Persisted
        {
            AccessToken = secrets.AccessToken,
            SigningKey = Convert.ToBase64String(secrets.SigningKey),
        });

        try
        {
            var cipher = ProtectedData.Protect(payload, Entropy, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, cipher);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public AgentSecrets Load()
    {
        if (!Exists)
            throw new InvalidOperationException(
                $"No secrets stored at {_path}. Run the agent with --setup to configure it.");

        var cipher = File.ReadAllBytes(_path);
        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException(
                $"Stored secrets at {_path} could not be decrypted. They are bound to the Windows " +
                "user that saved them; re-run with --setup to store them again.", ex);
        }

        try
        {
            var persisted = JsonSerializer.Deserialize<Persisted>(plain)
                            ?? throw new InvalidOperationException("Secret store is corrupt.");
            return new AgentSecrets(
                persisted.AccessToken,
                Convert.FromBase64String(persisted.SigningKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public void Delete()
    {
        if (Exists) File.Delete(_path);
    }

    /// <summary>Generates a fresh 256-bit signing key to share with the Home Assistant integration.</summary>
    public static byte[] GenerateSigningKey() => RandomNumberGenerator.GetBytes(32);

    private sealed class Persisted
    {
        public string AccessToken { get; set; } = "";
        public string SigningKey { get; set; } = "";
    }
}
