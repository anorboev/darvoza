using Darvoza.Gateway.Configuration;
using ModelContextProtocol.Client;

namespace Darvoza.Gateway.Upstream;

/// <summary>The resolved upstream launch: the executable and its exact argv (A01-T6a).</summary>
public sealed record UpstreamLaunchSpec(string Command, IReadOnlyList<string> Arguments);

/// <summary>
/// Builds the upstream launch spec without routing argv through a BATCH FILE (A01-T6a, G-10 #1). Since
/// A01-T7 the upstream is operator-selectable (<see cref="UpstreamOptions"/>): an explicit
/// <c>command</c> + <c>args</c> from the config file, or the built-in <c>azure-devops</c> profile that
/// remains the default. On Windows a bare <c>"npx"</c> resolves to <c>npx.cmd</c> — a cmd.exe batch
/// script whose argument re-parsing has known .NET escaping gaps — so the profile launches <c>node</c>
/// with npm's <c>npx-cli.js</c> directly instead, avoiding the batch file's OWN argument re-parse. The
/// strict <c>ADO_ORG</c> allowlist (<c>GatewayOptions.IsValidAdoOrg</c>) remains load-bearing on Windows
/// (see the correction below) and defense-in-depth elsewhere. On non-Windows, <c>npx</c> is a real
/// binary and is used as-is.
/// </summary>
/// <remarks>
/// <para>
/// <b>Correction to an A01-T6a claim (see ADR-0004).</b> A01-T6a concluded that launching
/// <c>node npx-cli.js</c> removed the shell from the Windows launch path. It did not: the pinned MCP SDK
/// 1.4.0 rewrites <i>every</i> non-<c>cmd.exe</c> Windows launch to <c>cmd.exe /c &lt;command&gt;
/// &lt;args…&gt;</c>. Avoiding <c>npx.cmd</c> still avoids a SECOND re-parse (the batch file's own), which
/// is a real improvement — but on Windows the strict <c>ADO_ORG</c> allowlist is <b>load-bearing, not
/// defense-in-depth</b>. On non-Windows the SDK spawns directly and the demotion holds.
/// </para>
/// <para>
/// <b>What A01-T7 changes about that surface: only who SUPPLIES argv.</b> It moved from partly
/// env-derived (<c>ADO_ORG</c>) to an operator config file — a NARROWER input source, not a wider one —
/// and Darvoza still never builds a command line anywhere.
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
    /// Resolves the credential the <c>azure-devops</c> profile's child reads from
    /// <c>PERSONAL_ACCESS_TOKEN</c> (Decision #3). The upstream's <c>pat</c> mode expects
    /// base64 of <c>"email:pat"</c>, so a pre-encoded <c>PERSONAL_ACCESS_TOKEN</c> is passed through and
    /// a raw <c>AZURE_DEVOPS_EXT_PAT</c> is encoded in-process. Azure DevOps ignores the username, so
    /// <c>"darvoza"</c> is an arbitrary fixed placeholder, not an operator identity.
    /// </summary>
    /// <remarks>
    /// Pure so the encoding contract is directly testable — it previously had only incidental coverage
    /// from the e2e booting the composition root, which A01-T7 removed (@test-skeptic on PR #14). The
    /// token is returned, never stored on an app-lifetime object, preserving PAT-residency minimization.
    /// </remarks>
    public static string ResolveAzureDevOpsToken(Func<string, string?> getEnv)
    {
        var token = getEnv("PERSONAL_ACCESS_TOKEN");
        if (!string.IsNullOrEmpty(token))
            return token;

        var rawPat = getEnv("AZURE_DEVOPS_EXT_PAT")
            ?? throw new InvalidOperationException(
                "Set PERSONAL_ACCESS_TOKEN (base64 of \"email:pat\") or AZURE_DEVOPS_EXT_PAT (raw PAT).");

        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"darvoza:{rawPat}"));
    }

    /// <summary>
    /// Builds the stdio transport options for a resolved launch — command, argv, and the child's
    /// environment. Pure given <paramref name="getEnv"/>, so the security-critical part (which
    /// environment a configured upstream receives) is directly testable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Environment isolation.</b> A configured upstream runs with
    /// <c>InheritEnvironmentVariables = false</c>, seeded from the SDK's curated allowlist (<c>PATH</c>
    /// and friends — enough for a normal program to start) plus exactly the variables named in
    /// <see cref="UpstreamOptions.PassEnv"/>. Without this it would inherit every
    /// <c>DARVOZA_KEY_*</c> caller key and the audit fingerprint salt, and could authenticate back into
    /// Darvoza's own front leg as any role (@security-reviewer HIGH on PR #14).
    /// </para>
    /// <para>
    /// The built-in azure-devops profile inherits, unchanged: it is the pinned, trusted package, and
    /// that is how its PAT reaches it (Decision #3).
    /// </para>
    /// </remarks>
    public static StdioClientTransportOptions BuildTransportOptions(
        UpstreamOptions options, UpstreamLaunchSpec launch, Func<string, string?> getEnv)
    {
        var childEnvironment = options.InheritEnvironment
            ? []
            : StdioClientTransportOptions.GetDefaultEnvironmentVariables();

        if (options.IsCustom)
        {
            foreach (var (name, value) in UpstreamOptions.BuildPassedEnvironment(options, getEnv))
                childEnvironment[name] = value;
        }
        else
        {
            childEnvironment["PERSONAL_ACCESS_TOKEN"] = ResolveAzureDevOpsToken(getEnv);
        }

        return new StdioClientTransportOptions
        {
            Name = options.IsCustom ? "configured-upstream" : "azure-devops-upstream",
            Command = launch.Command,
            // Argv stays a COLLECTION on Darvoza's side of the boundary — never joined into a command
            // line here or anywhere else. What the SDK does with it on Windows (`cmd.exe /c`) is
            // documented in ADR-0004; that is why the ADO_ORG allowlist is load-bearing there.
            Arguments = [.. launch.Arguments],
            InheritEnvironmentVariables = options.InheritEnvironment,
            EnvironmentVariables = childEnvironment,
        };
    }

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
        // arg, never joined and never split by us. No node/npx probing happens on this path at all.
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
