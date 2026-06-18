using System.Security.Cryptography;
using System.Text;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// Produces a short, non-reversible fingerprint of a caller key for the audit trail, so calls from the
/// same caller correlate WITHOUT the raw secret ever being written to disk. SHA-256, truncated to a
/// 8-hex-character (32-bit) prefix — enough to group a handful of demo callers, not the full digest.
/// </summary>
public static class CallerFingerprint
{
    /// <summary>The fingerprint of <paramref name="callerKey"/>, or <c>null</c> when no key was presented.</summary>
    public static string? Of(string? callerKey)
    {
        if (string.IsNullOrEmpty(callerKey))
            return null;

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(callerKey));
        return Convert.ToHexStringLower(digest)[..8];
    }
}
