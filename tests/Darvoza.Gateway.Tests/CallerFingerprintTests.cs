using System.Security.Cryptography;
using System.Text;
using Darvoza.Gateway.Audit;

namespace Darvoza.Gateway.Tests;

// A01-T6e (G-17 #1) — the audit caller fingerprint is an HMAC-SHA256 of the caller key under a
// per-deployment salt, truncated to 16 hex chars (64 bits). Salting means a published audit sample
// cannot be dictionary-matched against guessed keys, and the same key no longer maps to the same
// fingerprint across deployments; 16 hex keeps the collision (birthday) bound far beyond any
// realistic caller count.
public class CallerFingerprintTests
{
    private static readonly byte[] SaltA = Encoding.UTF8.GetBytes("per-deployment-salt-A");
    private static readonly byte[] SaltB = Encoding.UTF8.GetBytes("per-deployment-salt-B");

    [Fact]
    public void Fingerprint_is_16_lowercase_hex_chars()
    {
        var fingerprint = new CallerFingerprint(SaltA).Of("some-caller-key");

        Assert.NotNull(fingerprint);
        Assert.Equal(CallerFingerprint.HexLength, fingerprint!.Length);
        Assert.Equal(16, CallerFingerprint.HexLength);
        Assert.All(fingerprint, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
    }

    [Fact]
    public void Same_key_under_the_same_salt_is_deterministic()
    {
        var fingerprints = new CallerFingerprint(SaltA);

        Assert.Equal(fingerprints.Of("some-caller-key"), fingerprints.Of("some-caller-key"));
    }

    [Fact]
    public void Same_key_under_a_different_salt_diverges()
    {
        // The dictionary-match / cross-deployment-correlation resistance the salt exists for.
        Assert.NotEqual(
            new CallerFingerprint(SaltA).Of("some-caller-key"),
            new CallerFingerprint(SaltB).Of("some-caller-key"));
    }

    [Fact]
    public void Fingerprint_is_the_truncated_hmac_of_the_key_not_a_plain_hash()
    {
        var keyBytes = Encoding.UTF8.GetBytes("some-caller-key");
        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(SaltA, keyBytes))
            [..CallerFingerprint.HexLength];
        var unsalted = Convert.ToHexStringLower(SHA256.HashData(keyBytes))
            [..CallerFingerprint.HexLength];

        var fingerprint = new CallerFingerprint(SaltA).Of("some-caller-key");

        Assert.Equal(expected, fingerprint);
        Assert.NotEqual(unsalted, fingerprint); // an unsalted digest would defeat the whole gate
    }

    [Fact]
    public void Null_or_empty_key_yields_null()
    {
        var fingerprints = new CallerFingerprint(SaltA);

        Assert.Null(fingerprints.Of(null));
        Assert.Null(fingerprints.Of(""));
    }

    [Fact]
    public void Empty_salt_is_refused_at_construction()
    {
        // An empty salt would silently degrade to an unsalted keyed hash — fail fast instead.
        Assert.Throws<ArgumentException>(() => new CallerFingerprint([]));
    }
}
