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

    /// <summary>The validated Azure DevOps organization name (positional arg to the upstream server).</summary>
    public required string AdoOrg { get; init; }

    // Conservative Azure DevOps org-name shape: alphanumeric, interior hyphens allowed, no
    // leading/trailing hyphen, no whitespace/slashes/scheme. Bounds length to a sane 64 chars.
    [GeneratedRegex("^[A-Za-z0-9]([A-Za-z0-9-]{0,62}[A-Za-z0-9])?$")]
    private static partial Regex AdoOrgPattern();

    /// <summary>Pure shape check for an Azure DevOps organization name.</summary>
    public static bool IsValidAdoOrg(string? org) =>
        !string.IsNullOrWhiteSpace(org) && AdoOrgPattern().IsMatch(org);
}
