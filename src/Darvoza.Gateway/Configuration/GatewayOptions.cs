using System.Text.RegularExpressions;

namespace Darvoza.Gateway.Configuration;

/// <summary>
/// Startup configuration for the gateway. Resolved from standard .NET configuration (environment
/// variables, optionally seeded from a project-root <c>.env</c> via <see cref="DotEnvLoader"/>).
/// </summary>
/// <remarks>
/// A01-T2c: <c>ADO_ORG</c> is validated fail-fast at startup so a malformed org never reaches the
/// upstream <c>npx</c> launch (where it is a positional argument — an injection/traversal surface).
/// The PAT is deliberately NOT a property here: it is read once at the upstream-client boundary and
/// never persisted in an app-lifetime object (PAT-residency minimization).
/// </remarks>
public sealed partial class GatewayOptions
{
    /// <summary>Environment variable carrying the Azure DevOps organization name.</summary>
    public const string AdoOrgEnvVar = "ADO_ORG";

    /// <summary>Optional environment variable overriding the policy file path (A01-T3).</summary>
    public const string PolicyPathEnvVar = "DARVOZA_POLICY_PATH";

    /// <summary>Default committed policy file name (no secrets — keyEnv names env vars, not key values).</summary>
    public const string DefaultPolicyFileName = "policy.yaml";

    /// <summary>Gitignored local-override policy file; takes precedence over the committed default.</summary>
    public const string LocalPolicyFileName = "policy.local.yaml";

    /// <summary>Optional environment variable overriding the audit-trail file path (A01-T4).</summary>
    public const string AuditPathEnvVar = "DARVOZA_AUDIT_PATH";

    /// <summary>Default (gitignored) directory holding the audit trail.</summary>
    public const string DefaultAuditDirName = "audit";

    /// <summary>Default audit-trail file name within <see cref="DefaultAuditDirName"/>.</summary>
    public const string DefaultAuditFileName = "darvoza-audit.jsonl";

    /// <summary>The validated Azure DevOps organization name (positional arg to the upstream server).</summary>
    public required string AdoOrg { get; init; }

    /// <summary>
    /// Resolves the policy file path. Precedence: <see cref="PolicyPathEnvVar"/> if set, else a local
    /// <see cref="LocalPolicyFileName"/> override under <paramref name="contentRoot"/> when present
    /// (gitignored), else the committed <see cref="DefaultPolicyFileName"/>.
    /// </summary>
    /// <param name="fileExists">Existence probe (injectable for tests); defaults to <see cref="File.Exists"/>.</param>
    public static string ResolvePolicyPath(string contentRoot, Func<string, bool>? fileExists = null)
    {
        var configured = Environment.GetEnvironmentVariable(PolicyPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        fileExists ??= File.Exists;
        var local = Path.Combine(contentRoot, LocalPolicyFileName);
        return fileExists(local) ? local : Path.Combine(contentRoot, DefaultPolicyFileName);
    }

    /// <summary>
    /// Resolves the audit-trail file path. Precedence: <see cref="AuditPathEnvVar"/> if set, else the
    /// committed-gitignored default <c>&lt;contentRoot&gt;/<see cref="DefaultAuditDirName"/>/<see cref="DefaultAuditFileName"/></c>.
    /// </summary>
    /// <param name="getEnv">Environment reader (injectable for tests); defaults to the process environment.</param>
    public static string ResolveAuditPath(string contentRoot, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        var configured = getEnv(AuditPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        return Path.Combine(contentRoot, DefaultAuditDirName, DefaultAuditFileName);
    }

    // Conservative Azure DevOps org-name shape: alphanumeric, interior hyphens allowed, no
    // leading/trailing hyphen, no whitespace/slashes/scheme. Bounds length to a sane 64 chars.
    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?$")]
    private static partial Regex AdoOrgPattern();

    /// <summary>Pure shape check for an Azure DevOps organization name.</summary>
    public static bool IsValidAdoOrg(string? org) =>
        !string.IsNullOrWhiteSpace(org) && AdoOrgPattern().IsMatch(org);
}
