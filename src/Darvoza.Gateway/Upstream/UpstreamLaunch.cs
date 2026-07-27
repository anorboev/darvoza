using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Upstream;

/// <summary>The resolved upstream launch: the executable and its exact argv (A01-T6a).</summary>
public sealed record UpstreamLaunchSpec(string Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the upstream launch spec WITHOUT routing argv through a batch file (A01-T6a, G-10 #1). Since
/// A01-T7 the upstream is operator-selectable (<see cref="UpstreamOptions"/>): an explicit
/// <c>command</c> + <c>args</c> from the config file, or the built-in <c>azure-devops</c> profile that
/// remains the default. On Windows a bare <c>"npx"</c> resolves to <c>npx.cmd</c> — a cmd.exe batch
/// script whose argument re-parsing has known .NET escaping gaps — so the profile launches <c>node</c>
/// with npm's <c>npx-cli.js</c> directly instead: no shell or batch file ever re-parses our arguments.
/// The strict <c>ADO_ORG</c> allowlist (<c>GatewayOptions.IsValidAdoOrg</c>) remains as defense-in-depth,
/// no longer the load-bearing mitigation. On non-Windows, <c>npx</c> is a real binary and is used as-is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why A01-T7 does not reopen G-10 #1.</b> That gate closed on two structural properties: no shell or
/// batch file re-parses our argv, and argv is an array end to end. Both are preserved verbatim here.
/// What changed is only who SUPPLIES argv, and it moved from partly env-derived (<c>ADO_ORG</c>) to an
/// operator config file — a NARROWER input source, not a wider one. A custom command bypasses the
/// npx/node path entirely, so the batch-file surface is absent rather than merely avoided. See ADR-0004.
/// </para>
/// <para>
/// <c>npx-cli.js</c> resolution (azure-devops profile only): an explicit <see cref="NpxCliPathEnvVar"/>
/// override wins (exotic npm layouts); otherwise the directory containing <c>node.exe</c> is located on
/// <c>PATH</c> and the standard install-relative <c>node_modules\npm\bin\npx-cli.js</c> is probed. A miss
/// fails startup with guidance — consistent with the gateway's fail-fast posture — rather than silently
/// degrading to the batch-file launch.
/// </para>
/// </remarks>
public static class UpstreamLaunch
{
    /// <summary>Pinned upstream package (Decision #3). Bump deliberately — tool names/arg shapes drift (G-20).</summary>
    public const string PackageSpec = "@azure-devops/mcp@2.7.0";

    /// <summary>Optional env var pointing at <c>npx-cli.js</c> for non-standard npm installs.</summary>
    public const string NpxCliPathEnvVar = "DARVOZA_NPX_CLI_JS";

    /// <summary>
    /// Resolves the launch spec. Pure given the injected probes — unit-testable on any OS. The only
    /// environment this reads is the npx-cli override and <c>PATH</c>, and only for the azure-devops
    /// profile: the upstream command itself is never environment-derived (ADR-0004).
    /// </summary>
    public static UpstreamLaunchSpec Resolve(
        UpstreamOptions options,
        string? adoOrg,
        bool isWindows,
        Func<string, string?> getEnv,
        Func<string, bool> fileExists)
    {
        // A01-T7: an explicit operator command is launched verbatim — one argv element per configured
        // arg, never joined, never split, no shell. No node/npx probing happens on this path at all.
        if (options.IsCustom)
            return new UpstreamLaunchSpec(options.Command!, [.. options.Args]);

        if (options.Profile != UpstreamOptions.AzureDevOpsProfile)
        {
            throw new InvalidOperationException(
                $"Unknown upstream profile '{options.Profile}'. Known profiles: " +
                $"'{UpstreamOptions.AzureDevOpsProfile}'. To launch a different MCP server, set " +
                "'upstream.command' + 'upstream.args' in the policy file instead (see README).");
        }

        if (string.IsNullOrWhiteSpace(adoOrg))
        {
            throw new InvalidOperationException(
                $"The '{UpstreamOptions.AzureDevOpsProfile}' upstream profile requires a validated " +
                $"{GatewayOptions.AdoOrgEnvVar}.");
        }

        // The org is exactly ONE positional argv element — never concatenated or quoted by us; the PAT
        // never appears here at all (it travels via the child's environment).
        string[] packageArgs = ["-y", PackageSpec, adoOrg, "--authentication", "pat"];

        if (!isWindows)
            return new UpstreamLaunchSpec("npx", packageArgs);

        var npxCli = ResolveNpxCliPath(getEnv, fileExists)
            ?? throw new InvalidOperationException(
                "Could not locate npm's npx-cli.js next to node.exe on PATH. Darvoza launches the " +
                "upstream via `node npx-cli.js` on Windows (never npx.cmd — argument-injection " +
                $"hardening, A01-T6a). Set {NpxCliPathEnvVar} to your npm install's npx-cli.js.");

        return new UpstreamLaunchSpec("node", [npxCli, .. packageArgs]);
    }

    private static string? ResolveNpxCliPath(Func<string, string?> getEnv, Func<string, bool> fileExists)
    {
        var configured = getEnv(NpxCliPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
            return fileExists(configured) ? configured : null;

        // Note a benign asymmetry: the launch command is the bare "node" (OS PATH resolution at spawn
        // time), while npx-cli.js is taken from the FIRST PATH dir containing node.exe + the adjacent
        // npm layout. With two node installs on PATH these could differ — same trust boundary either
        // way (an attacker writing an early PATH dir already owns the node binary), so no new surface.
        foreach (var dir in (getEnv("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!fileExists(Path.Combine(dir, "node.exe")))
                continue;

            // Standard layout for the official installer and nvm-windows alike.
            var candidate = Path.Combine(dir, "node_modules", "npm", "bin", "npx-cli.js");
            if (fileExists(candidate))
                return candidate;
        }

        return null;
    }
}
