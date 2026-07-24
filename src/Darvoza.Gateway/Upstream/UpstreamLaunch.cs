namespace Darvoza.Gateway.Upstream;

/// <summary>The resolved upstream launch: the executable and its exact argv (A01-T6a).</summary>
public sealed record UpstreamLaunchSpec(string Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the upstream <c>@azure-devops/mcp</c> launch spec WITHOUT routing argv through a batch file
/// (A01-T6a, G-10 #1). On Windows a bare <c>"npx"</c> resolves to <c>npx.cmd</c> — a cmd.exe batch
/// script whose argument re-parsing has known .NET escaping gaps — so this launches <c>node</c> with
/// npm's <c>npx-cli.js</c> directly instead: no shell or batch file ever re-parses our arguments. The
/// strict <c>ADO_ORG</c> allowlist (<c>GatewayOptions.IsValidAdoOrg</c>) remains as defense-in-depth,
/// no longer the load-bearing mitigation. On non-Windows, <c>npx</c> is a real binary and is used as-is.
/// </summary>
/// <remarks>
/// <c>npx-cli.js</c> resolution: an explicit <see cref="NpxCliPathEnvVar"/> override wins (exotic npm
/// layouts); otherwise the directory containing <c>node.exe</c> is located on <c>PATH</c> and the
/// standard install-relative <c>node_modules\npm\bin\npx-cli.js</c> is probed. A miss fails startup
/// with guidance — consistent with the gateway's fail-fast posture — rather than silently degrading
/// to the batch-file launch.
/// </remarks>
public static class UpstreamLaunch
{
    /// <summary>Pinned upstream package (Decision #3). Bump deliberately — tool names/arg shapes drift (G-20).</summary>
    public const string PackageSpec = "@azure-devops/mcp@2.7.0";

    /// <summary>Optional env var pointing at <c>npx-cli.js</c> for non-standard npm installs.</summary>
    public const string NpxCliPathEnvVar = "DARVOZA_NPX_CLI_JS";

    /// <summary>Resolves the launch spec. Pure given the injected probes — unit-testable on any OS.</summary>
    public static UpstreamLaunchSpec Resolve(
        string adoOrg,
        bool isWindows,
        Func<string, string?> getEnv,
        Func<string, bool> fileExists)
    {
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
