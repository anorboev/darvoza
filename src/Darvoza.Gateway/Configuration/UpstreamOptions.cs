namespace Darvoza.Gateway.Configuration;

/// <summary>
/// The validated upstream-server selection (A01-T7): either a built-in <see cref="Profile"/> or an
/// explicit operator-supplied <see cref="Command"/> + <see cref="Args"/>. Resolved from the gateway's
/// config file <b>only</b> — never from an environment variable, HTTP header, query string, or request
/// body (ADR-0004).
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "a governance gateway in front of ANY MCP server" a verifiable claim: the front
/// leg, the policy engine (ADR-0002) and the audit trail (ADR-0003) were already upstream-independent —
/// only the launch path was pinned to <c>@azure-devops/mcp</c>.
/// </para>
/// <para>
/// <b>Trust boundary.</b> The config file is a trust boundary equal to the rest of <c>policy.yaml</c>:
/// whoever can edit it can already mint a role allow-listing every upstream tool and bind a caller key
/// to it, so they already command every tool call the gateway can make. See ADR-0004 for the full
/// argument, including the residual case it does NOT cover (a deployment where the config file is
/// writable by a party who cannot write the gateway's binaries — keep it owner-writable only).
/// </para>
/// <para>
/// <b>Credentials belong in the environment, never in <see cref="Args"/>.</b> The upstream child process
/// inherits the gateway's environment, which is how the Azure DevOps PAT reaches it today (Decision #3).
/// Argv is logged once at startup, so a secret written into <see cref="Args"/> would land in the logs.
/// </para>
/// </remarks>
public sealed record UpstreamOptions
{
    /// <summary>The built-in Azure DevOps profile — the default when no upstream is configured.</summary>
    public const string AzureDevOpsProfile = "azure-devops";

    /// <summary>The default: Microsoft's official Azure DevOps MCP server (Decision #3, pinned package).</summary>
    public static UpstreamOptions AzureDevOps { get; } = new();

    /// <summary>Built-in profile name. Mutually exclusive with <see cref="Command"/>.</summary>
    public string Profile { get; init; } = AzureDevOpsProfile;

    /// <summary>
    /// Explicit executable to launch. <c>null</c> selects <see cref="Profile"/>. Never shell-interpreted:
    /// it is passed to the process launcher as the command, with <see cref="Args"/> as a separate array.
    /// </summary>
    public string? Command { get; init; }

    /// <summary>
    /// Arguments for <see cref="Command"/>, as a collection. Each element is one argv slot — Darvoza never
    /// joins these into a string and never splits one, so no shell parsing can occur (G-10 #1).
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>True when the operator opted in to an explicit non-profile upstream.</summary>
    public bool IsCustom => Command is not null;

    /// <summary>
    /// True for a Windows command that a process launcher routes through <c>cmd.exe</c>. There is no
    /// injection here — the operator supplies the command AND the args from the same trusted file, so no
    /// untrusted input reaches the batch re-parse — but .NET's argument escaping for batch files has known
    /// gaps, so startup says so out loud rather than leaving the operator to discover it (ADR-0004).
    /// </summary>
    public static bool IsWindowsBatchCommand(string command) =>
        command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
        || command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
}
