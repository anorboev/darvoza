namespace Darvoza.Gateway.Configuration;

/// <summary>
/// Raw deserialization target for <c>policy.yaml</c> (A01-T3). Mirrors the on-disk schema 1:1 and
/// carries no behavior — <see cref="PolicyLoader"/> validates it and projects it into the immutable
/// <see cref="Policy"/>. Internal: the validated <see cref="Policy"/> is the only type the rest of the
/// app sees.
/// </summary>
internal sealed class PolicyDocument
{
    /// <summary>Caller bindings: each maps an env-var-held secret key to a role.</summary>
    public List<PolicyCaller> Callers { get; set; } = [];

    /// <summary>Role → allow-list definitions, keyed by role name.</summary>
    public Dictionary<string, PolicyRole> Roles { get; set; } = [];
}

/// <summary>A caller binding: the env var holding this caller's secret key, and the role it maps to.</summary>
internal sealed class PolicyCaller
{
    /// <summary>Name of the environment variable that holds this caller's secret key (never the key itself).</summary>
    public string? KeyEnv { get; set; }

    /// <summary>The role this caller is granted (must exist in <see cref="PolicyDocument.Roles"/>).</summary>
    public string? Role { get; set; }
}

/// <summary>A role definition: the explicit allow-list of tool names. Everything else is denied.</summary>
internal sealed class PolicyRole
{
    /// <summary>Tool names this role may list and call. Empty = a deny-all role (valid).</summary>
    public List<string> Allow { get; set; } = [];
}
