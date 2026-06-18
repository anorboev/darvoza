using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// One audit record — exactly one is written per <c>CallToolAsync</c> through the gateway (A01-T4), for
/// every outcome: allowed→upstream-ok, allowed→upstream-error, and policy-denied. Serialized as a single
/// JSON line (JSONL). Carries the caller identity/role, the tool, a redacted argument summary, the policy
/// decision (+ reason on deny), the upstream status (when the call reached upstream), latency, and a UTC
/// timestamp. <b>It deliberately holds no raw secret</b> — never the caller key, never the PAT, never the
/// unredacted argument values (see <see cref="AuditArgs"/>).
/// </summary>
public sealed record AuditRecord
{
    /// <summary>UTC timestamp, ISO-8601 (round-trip "O").</summary>
    [JsonPropertyName("ts")]
    public required string Ts { get; init; }

    /// <summary>The invoked tool name.</summary>
    [JsonPropertyName("tool")]
    public required string Tool { get; init; }

    /// <summary>The caller's identity — role + non-reversible key fingerprint, never the raw key.</summary>
    [JsonPropertyName("caller")]
    public required AuditCaller Caller { get; init; }

    /// <summary>The policy decision: <c>"allow"</c> or <c>"deny"</c>.</summary>
    [JsonPropertyName("decision")]
    public required string Decision { get; init; }

    /// <summary>The denial reason; <c>null</c> on allow.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>A redacted summary of the tool arguments — key names + count + digest, never the values.</summary>
    [JsonPropertyName("args")]
    public required AuditArgs Args { get; init; }

    /// <summary>The upstream outcome when the call was forwarded; <c>null</c> for a policy denial.</summary>
    [JsonPropertyName("upstream")]
    public AuditUpstream? Upstream { get; init; }

    /// <summary>Wall-clock latency of the wrapped call, in milliseconds.</summary>
    [JsonPropertyName("latencyMs")]
    public required long LatencyMs { get; init; }

    // One cached, no-indent options instance → each record serializes to exactly one line. Property names
    // come from the explicit [JsonPropertyName] attributes above (no reflective name policy needed).
    // DefaultIgnoreCondition.Never is deliberate: every record carries the SAME keys (a fixed schema), so
    // log tooling can rely on field presence and a denial reads as `"upstream":null` rather than a missing
    // key. The trade-off (slightly larger allow lines + null-sentinel rather than absence semantics) is
    // accepted in favor of a stable, self-documenting line shape.
    private static readonly JsonSerializerOptions LineOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Serializes this record to a single JSON line (no embedded newlines).</summary>
    public string ToJsonLine() => JsonSerializer.Serialize(this, LineOptions);
}

/// <summary>The caller identity in an <see cref="AuditRecord"/>: role + non-reversible key fingerprint.</summary>
public sealed record AuditCaller
{
    /// <summary>The resolved role, or <c>null</c> when the caller key is unknown/missing.</summary>
    [JsonPropertyName("role")]
    public string? Role { get; init; }

    /// <summary>A short SHA-256 fingerprint of the caller key — never the raw key. <c>null</c> if no key.</summary>
    [JsonPropertyName("keyFingerprint")]
    public string? KeyFingerprint { get; init; }
}

/// <summary>
/// A redacted argument summary: which parameters were present (key names + count) and a digest of their
/// canonical form — <b>never the values</b>. The digest lets identical argument sets correlate without the
/// values being recoverable from the trail.
/// </summary>
public sealed record AuditArgs
{
    /// <summary>The argument key names, ordinally sorted. Empty when the call had no arguments.</summary>
    [JsonPropertyName("keys")]
    public required IReadOnlyList<string> Keys { get; init; }

    /// <summary>The number of arguments.</summary>
    [JsonPropertyName("count")]
    public required int Count { get; init; }

    /// <summary>SHA-256 (hex) of the canonical argument form; <c>null</c> when there were no arguments.</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; init; }

    /// <summary>
    /// Builds a redacted summary from the raw MCP arguments dictionary. The values are hashed (their raw
    /// JSON, under ordinally-sorted keys) but never copied into the record.
    /// </summary>
    public static AuditArgs From(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return new AuditArgs { Keys = [], Count = 0, Sha256 = null };

        var keys = arguments.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        // Canonical form: ordinally-sorted "key<US>rawValue<RS>" pairs. The ASCII unit (0x1F) and record
        // (0x1E) separators cannot appear unescaped in a JSON key or token, so distinct argument sets
        // cannot collide on the canonical string. Values are hashed here, never stored.
        const char unitSeparator = (char)0x1F;
        const char recordSeparator = (char)0x1E;
        var canonical = new StringBuilder();
        foreach (var key in keys)
        {
            canonical.Append(key).Append(unitSeparator)
                .Append(arguments[key].GetRawText()).Append(recordSeparator);
        }

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return new AuditArgs { Keys = keys, Count = keys.Length, Sha256 = Convert.ToHexStringLower(digest) };
    }
}

/// <summary>The upstream outcome for a forwarded call.</summary>
public sealed record AuditUpstream
{
    /// <summary><c>"ok"</c> when the upstream returned a non-error result; <c>"error"</c> otherwise.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }
}
