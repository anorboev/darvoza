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

    /// <summary>Default policy file name the gateway resolves to — a local, gitignored copy of the
    /// committed <c>policy.example.yaml</c> template (no secrets either way: keyEnv names env vars,
    /// not key values).</summary>
    public const string DefaultPolicyFileName = "policy.yaml";

    /// <summary>Gitignored local-override policy file; takes precedence over the committed default.</summary>
    public const string LocalPolicyFileName = "policy.local.yaml";

    /// <summary>Optional environment variable overriding the audit-trail file path (A01-T4).</summary>
    public const string AuditPathEnvVar = "DARVOZA_AUDIT_PATH";

    /// <summary>Default (gitignored) directory holding the audit trail.</summary>
    public const string DefaultAuditDirName = "audit";

    /// <summary>Default audit-trail file name within <see cref="DefaultAuditDirName"/>.</summary>
    public const string DefaultAuditFileName = "darvoza-audit.jsonl";

    /// <summary>Optional environment variable configuring the audit-fingerprint salt (A01-T6e).</summary>
    public const string FingerprintSaltEnvVar = "DARVOZA_FINGERPRINT_SALT";

    /// <summary>
    /// The validated Azure DevOps organization name (positional arg to the upstream server), or
    /// <c>null</c> when the operator configured a non-Azure-DevOps upstream (A01-T7) — an ADO org is
    /// meaningless for a server that is not Azure DevOps.
    /// </summary>
    public string? AdoOrg { get; init; }

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

    /// <summary>
    /// Resolves the audit-fingerprint salt (A01-T6e, G-17 #1). A configured
    /// <see cref="FingerprintSaltEnvVar"/> (UTF-8 bytes of its value) gives fingerprints that are stable
    /// across restarts; otherwise a fresh 32-byte random salt is generated, so fingerprints correlate
    /// within a run only. The salt is never logged and never written to the audit trail.
    /// </summary>
    /// <param name="getEnv">Environment reader (injectable for tests); defaults to the process environment.</param>
    /// <param name="randomBytes">Random-byte source (injectable for tests); defaults to a CSPRNG.</param>
    public static byte[] ResolveFingerprintSalt(
        Func<string, string?>? getEnv = null,
        Func<int, byte[]>? randomBytes = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        randomBytes ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes;

        var configured = getEnv(FingerprintSaltEnvVar);
        return string.IsNullOrWhiteSpace(configured)
            ? randomBytes(32)
            : System.Text.Encoding.UTF8.GetBytes(configured);
    }

    /// <summary>
    /// True only for an http/https URL whose host is definitively loopback (A01-T6b). Used by the
    /// startup warning: <c>X-Darvoza-Key</c> is app-layer authorization, NOT transport authentication,
    /// so binding beyond loopback deserves an explicit operator warning. Fail-closed classification:
    /// anything unparseable, wildcard (<c>0.0.0.0</c>, <c>[::]</c>, <c>+</c>, <c>*</c>), non-HTTP, or
    /// empty-hosted counts as NON-loopback. (Per G-22, <see cref="Uri.IsLoopback"/> alone is a trap —
    /// it returns true for empty-host URIs — hence the scheme + explicit-host checks first.)
    /// </summary>
    public static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;
        if (uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            return false;
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
            return false;

        return uri.IsLoopback;
    }

    /// <summary>
    /// True when a Unix mode grants ANY group/other access (A01-T6f) — used by the startup tripwire
    /// that warns when the audit directory is readable beyond its owner. Windows ACLs have no cheap
    /// equivalent check; there the guidance is documentation (<c>icacls</c> recipe in README/RUNBOOK).
    /// </summary>
    public static bool IsGroupOrWorldAccessible(UnixFileMode mode) =>
        (mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
               | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0;

    // Conservative Azure DevOps org-name shape: alphanumeric, interior hyphens allowed, no
    // leading/trailing hyphen, no interior whitespace/slashes/scheme. Bounds length to a sane 64 chars.
    //
    // This allowlist is LOAD-BEARING on Windows, not defense-in-depth: the pinned SDK wraps every
    // Windows launch in `cmd.exe /c`, so this — not an absent shell — is what keeps ADO_ORG out of a
    // shell re-parse. See ADR-0004 §"Ruling on G-10 #1". Do not relax it.
    //
    // KNOWN GAP (issue #17): `$` in .NET also matches before a trailing newline, so `"org\n"` passes.
    // Bounded — the LF can only be final, so nothing attacker-controlled follows it. Fix is `\A…\z`
    // plus a test case, tracked in #17. Stated here because this comment is what a maintainer reads
    // before relaxing the pattern, and it must not claim more than the regex delivers.
    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?$")]
    private static partial Regex AdoOrgPattern();

    /// <summary>Pure shape check for an Azure DevOps organization name.</summary>
    public static bool IsValidAdoOrg(string? org) =>
        !string.IsNullOrWhiteSpace(org) && AdoOrgPattern().IsMatch(org);
}
