using System.Security.Cryptography;
using System.Text;

namespace Darvoza.Gateway.Configuration;

/// <summary>
/// The validated, immutable policy the gateway enforces (A01-T3): a resolved
/// <c>caller-key → role</c> map plus each role's exact-match tool allow-list. Pure decision logic with
/// no IO — built once at startup by <see cref="PolicyLoader"/> and registered as a singleton.
/// </summary>
/// <remarks>
/// <b>Deny-by-default</b> is the whole point: any tool name not present in the caller-role's allow-list
/// is denied, and an unknown or missing caller key resolves to no role (so its allow-list is empty —
/// everything is denied). Matching is exact, ordinal, case-sensitive (tool names and secret keys are
/// both opaque identifiers).
/// </remarks>
public sealed class Policy
{
    private static readonly IReadOnlySet<string> Empty = new HashSet<string>();

    // A01-T6c (G-13 #1): caller keys are hashed to fixed-width SHA-256 digests at construction and the
    // raw strings are NOT retained here. Lookups compare the presented key's digest against EVERY entry
    // with CryptographicOperations.FixedTimeEquals (no early exit), so the comparison time does not
    // depend on how much of a guessed key matches — closing the timing oracle the old dictionary
    // lookup leaked.
    private readonly (byte[] Digest, string Role)[] _entries;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _roleAllowlists;

    /// <param name="keyToRole">Resolved caller secret-key value → role name.</param>
    /// <param name="roleAllowlists">Role name → the set of tool names that role may use.</param>
    public Policy(
        IReadOnlyDictionary<string, string> keyToRole,
        IReadOnlyDictionary<string, IReadOnlySet<string>> roleAllowlists)
    {
        _entries = keyToRole.Select(pair => (KeyDigest(pair.Key), pair.Value)).ToArray();
        _roleAllowlists = roleAllowlists;
    }

    /// <summary>Digest storage seam for tests: one 32-byte SHA-256 digest per caller, no raw keys.</summary>
    internal IReadOnlyList<(byte[] Digest, string Role)> DigestEntries => _entries;

    /// <summary>SHA-256 over the key's UTF-8 bytes — the fixed-width form every comparison uses.</summary>
    internal static byte[] KeyDigest(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>Resolves a caller key to its role, or <c>null</c> if the key is missing/unknown.</summary>
    public string? RoleForKey(string? callerKey)
    {
        if (callerKey is null)
        {
            return null;
        }

        var probe = KeyDigest(callerKey);
        string? role = null;
        foreach (var (digest, entryRole) in _entries)
        {
            // Scan every entry even after a match: per-key work stays independent of the input.
            if (CryptographicOperations.FixedTimeEquals(probe, digest))
            {
                role = entryRole;
            }
        }

        return role;
    }

    /// <summary>
    /// The set of tool names the given caller key is permitted to use. An unknown/missing key (or a role
    /// with no allow-list) yields the empty set — deny-by-default.
    /// </summary>
    public IReadOnlySet<string> AllowlistFor(string? callerKey)
    {
        var role = RoleForKey(callerKey);
        return role is not null && _roleAllowlists.TryGetValue(role, out var allow) ? allow : Empty;
    }

    /// <summary>True only if the caller key resolves to a role whose allow-list contains the tool.</summary>
    public bool IsAllowed(string? callerKey, string toolName) =>
        AllowlistFor(callerKey).Contains(toolName);
}
