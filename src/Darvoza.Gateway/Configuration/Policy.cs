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

    private readonly IReadOnlyDictionary<string, string> _keyToRole;
    private readonly IReadOnlyDictionary<string, IReadOnlySet<string>> _roleAllowlists;

    /// <param name="keyToRole">Resolved caller secret-key value → role name.</param>
    /// <param name="roleAllowlists">Role name → the set of tool names that role may use.</param>
    public Policy(
        IReadOnlyDictionary<string, string> keyToRole,
        IReadOnlyDictionary<string, IReadOnlySet<string>> roleAllowlists)
    {
        _keyToRole = keyToRole;
        _roleAllowlists = roleAllowlists;
    }

    /// <summary>Resolves a caller key to its role, or <c>null</c> if the key is missing/unknown.</summary>
    /// <remarks>
    /// The dictionary lookup is not constant-time, so it leaks a small timing signal on the caller key.
    /// Acceptable while the front leg is loopback/trusted-network only (repo is private until A01-T6);
    /// a fixed-time comparison (or hashing the key to a fixed-width digest first) is tracked for T6b,
    /// alongside front-leg transport authentication (G-10).
    /// </remarks>
    public string? RoleForKey(string? callerKey) =>
        callerKey is not null && _keyToRole.TryGetValue(callerKey, out var role) ? role : null;

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
