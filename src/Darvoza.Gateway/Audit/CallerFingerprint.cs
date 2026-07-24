using System.Security.Cryptography;
using System.Text;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// Produces a short, non-reversible fingerprint of a caller key for the audit trail, so calls from the
/// same caller correlate WITHOUT the raw secret ever being written to disk. HMAC-SHA256 under a
/// per-deployment salt, truncated to a <see cref="HexLength"/>-character prefix (A01-T6e, G-17 #1).
/// </summary>
/// <remarks>
/// The salt (the HMAC key) makes the fingerprint deployment-local: a published audit sample cannot be
/// dictionary-matched against guessed caller keys, and the same key maps to different fingerprints on
/// different deployments. The salt comes from <c>DARVOZA_FINGERPRINT_SALT</c> when configured (stable
/// fingerprints across restarts) or is generated fresh at startup (fingerprints correlate within a run
/// only) — see <c>GatewayOptions.ResolveFingerprintSalt</c>. It is never logged and never written to the
/// trail. At 16 hex chars (64 bits) the collision (birthday) bound is ~4 billion distinct keys.
/// Truncation weakens only collision resistance, not pre-image resistance.
/// </remarks>
public sealed class CallerFingerprint
{
    /// <summary>Length (hex chars) of the truncated digest.</summary>
    public const int HexLength = 16;

    private readonly byte[] _salt;

    /// <param name="salt">The per-deployment HMAC key. Must be non-empty — an empty salt would silently
    /// degrade to an unsalted keyed hash, defeating the gate.</param>
    public CallerFingerprint(byte[] salt)
    {
        if (salt is not { Length: > 0 })
            throw new ArgumentException("Fingerprint salt must be non-empty.", nameof(salt));
        _salt = salt;
    }

    /// <summary>The fingerprint of <paramref name="callerKey"/>, or <c>null</c> when no key was presented.</summary>
    public string? Of(string? callerKey)
    {
        if (string.IsNullOrEmpty(callerKey))
            return null;

        var digest = HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(callerKey));
        return Convert.ToHexStringLower(digest)[..HexLength];
    }
}
