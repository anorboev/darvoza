using System.Security.Cryptography;
using System.Text;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// Produces a short, non-reversible fingerprint of a caller key for the audit trail, so calls from the
/// same caller correlate WITHOUT the raw secret ever being written to disk. SHA-256, truncated to a
/// <see cref="HexLength"/>-character prefix.
/// </summary>
/// <remarks>
/// The truncation only weakens collision resistance, not pre-image resistance (the key is the high-entropy
/// secret; this is a one-way function of it). At 8 hex chars (32 bits) the birthday bound is ~65k distinct
/// keys — ample for the single-tenant demo's handful of roles. <b>Scale/cross-tenant follow-up:</b> if the
/// gateway ever serves many callers, or trails from different deployments are aggregated, bump
/// <see cref="HexLength"/> and key the digest with a per-deployment salt/HMAC before truncating, so the
/// fingerprint is not a stable cross-deployment identity.
/// </remarks>
public static class CallerFingerprint
{
    /// <summary>Length (hex chars) of the truncated digest. See the remarks before raising this for scale.</summary>
    public const int HexLength = 8;

    /// <summary>The fingerprint of <paramref name="callerKey"/>, or <c>null</c> when no key was presented.</summary>
    public static string? Of(string? callerKey)
    {
        if (string.IsNullOrEmpty(callerKey))
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(callerKey));
        return Convert.ToHexStringLower(digest)[..HexLength];
    }
}
