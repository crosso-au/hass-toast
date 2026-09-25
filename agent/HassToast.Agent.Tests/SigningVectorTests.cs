using System.Text;
using System.Text.Json;
using HassToast.Agent.Security;
using Xunit;

namespace HassToast.Agent.Tests;

/// <summary>
/// Checks the agent against the same vectors the Home Assistant integration checks itself
/// against, in <c>docs/signing-vectors.json</c>.
/// <para>
/// The two sides never run in the same process, so nothing else can catch them disagreeing
/// about the signing format until a real toast silently fails to verify in production. A shared
/// file both suites read is the only thing that turns that into a test failure.
/// </para>
/// </summary>
public sealed class SigningVectorTests
{
    private sealed record Vector(
        string Name, int Version, string DeviceId, string Op, string Nonce,
        long Ts, string Payload, string SigningInput, string Signature);

    private static (byte[] Key, List<Vector> Vectors) Load()
    {
        var path = FindVectorFile();
        using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
        var root = document.RootElement;

        var key = Convert.FromBase64String(root.GetProperty("key_base64").GetString()!);

        var vectors = root.GetProperty("vectors").EnumerateArray().Select(v => new Vector(
            v.GetProperty("name").GetString()!,
            v.GetProperty("version").GetInt32(),
            v.GetProperty("device_id").GetString()!,
            v.GetProperty("op").GetString()!,
            v.GetProperty("nonce").GetString()!,
            v.GetProperty("ts").GetInt64(),
            v.GetProperty("payload").GetString()!,
            v.GetProperty("signing_input").GetString()!,
            v.GetProperty("signature").GetString()!)).ToList();

        return (key, vectors);
    }

    /// <summary>Walks up from the test binary to the repository root.</summary>
    private static string FindVectorFile()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "signing-vectors.json");
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "docs/signing-vectors.json was not found. Both the agent and the Home Assistant " +
            "integration verify against it, so it must be present.");
    }

    [Fact]
    public void Vector_file_is_present_and_populated()
    {
        var (key, vectors) = Load();

        Assert.Equal(32, key.Length);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, v => Assert.False(string.IsNullOrEmpty(v.Signature),
            $"vector '{v.Name}' has no expected signature; run hass/tools/fill_vectors.py"));
    }

    [Fact]
    public void Signing_input_matches_the_python_side_byte_for_byte()
    {
        var (_, vectors) = Load();

        foreach (var vector in vectors)
        {
            var produced = Encoding.UTF8.GetString(PayloadSigner.BuildSigningInput(
                vector.Version, vector.DeviceId, vector.Op, vector.Nonce, vector.Ts, vector.Payload));

            Assert.Equal(vector.SigningInput, produced);
        }
    }

    [Fact]
    public void Signatures_match_the_python_side()
    {
        var (key, vectors) = Load();

        foreach (var vector in vectors)
        {
            var input = PayloadSigner.BuildSigningInput(
                vector.Version, vector.DeviceId, vector.Op, vector.Nonce, vector.Ts, vector.Payload);

            var signature = Convert.ToBase64String(PayloadSigner.ComputeSignature(key, input));

            Assert.Equal(vector.Signature, signature);
        }
    }

    [Fact]
    public void Every_vector_verifies_through_the_real_check()
    {
        // Exercises the constant-time comparison path the agent actually uses, rather than
        // only comparing strings.
        var (key, vectors) = Load();

        foreach (var vector in vectors)
        {
            var input = PayloadSigner.BuildSigningInput(
                vector.Version, vector.DeviceId, vector.Op, vector.Nonce, vector.Ts, vector.Payload);

            Assert.True(PayloadSigner.SignatureMatches(key, input, vector.Signature),
                $"vector '{vector.Name}' did not verify");
        }
    }

    [Fact]
    public void A_vector_signed_with_the_wrong_key_does_not_verify()
    {
        var (_, vectors) = Load();
        var wrongKey = SecretStore.GenerateSigningKey();

        var vector = vectors[0];
        var input = PayloadSigner.BuildSigningInput(
            vector.Version, vector.DeviceId, vector.Op, vector.Nonce, vector.Ts, vector.Payload);

        Assert.False(PayloadSigner.SignatureMatches(wrongKey, input, vector.Signature));
    }
}
