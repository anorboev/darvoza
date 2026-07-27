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
/// <b>Credentials belong in the environment, never in <see cref="Args"/>.</b> Argv is logged once at
/// startup, so a secret written into <see cref="Args"/> would land in the logs. Name the variable in
/// <see cref="PassEnv"/> instead — the same idiom as a caller's <c>keyEnv</c>: the config file carries
/// the variable's NAME, never its value.
/// </para>
/// <para>
/// <b>A configured upstream does not inherit the gateway's environment</b> (@security-reviewer HIGH on
/// PR #14). That environment holds every <c>DARVOZA_KEY_*</c> caller key and
/// <c>DARVOZA_FINGERPRINT_SALT</c>; handing them to an operator-chosen third-party server would let it
/// authenticate back into Darvoza's own front leg as any role and de-anonymize the audit trail —
/// defeating the per-role policy guarantee the gateway exists to provide. Custom upstreams therefore get
/// the SDK's curated default environment plus exactly the variables named in <see cref="PassEnv"/>. The
/// built-in azure-devops profile keeps inheriting, unchanged from before A01-T7.
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
    /// joins these into a string and never splits one. (On Windows the SDK still wraps the launch in
    /// <c>cmd.exe /c</c> below this layer — see <see cref="LaunchesThroughWindowsShell"/> and ADR-0004.)
    /// </summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>
    /// Names of environment variables to forward to a configured upstream — names only, never values
    /// (the same contract as a caller's <c>keyEnv</c>). Empty for the built-in profile, which inherits.
    /// </summary>
    public IReadOnlyList<string> PassEnv { get; init; } = [];

    /// <summary>True when the operator opted in to an explicit non-profile upstream.</summary>
    public bool IsCustom => Command is not null;

    /// <summary>
    /// Whether the child process inherits the gateway's whole environment. True for the built-in
    /// azure-devops profile (unchanged pre-A01-T7 behaviour — the official server is the trusted,
    /// pinned package and the PAT reaches it this way). False for a configured upstream, which the
    /// operator chose and Darvoza does not vouch for.
    /// </summary>
    public bool InheritEnvironment => !IsCustom;

    /// <summary>
    /// Resolves <see cref="PassEnv"/> names to the values the child should receive. Fails fast on an
    /// unset variable, matching the loader's posture for an unset caller <c>keyEnv</c>: refuse to start
    /// rather than launch an upstream that is silently missing a credential.
    /// </summary>
    /// <param name="getEnv">Environment reader (injectable for tests).</param>
    public static IReadOnlyDictionary<string, string?> BuildPassedEnvironment(
        UpstreamOptions options, Func<string, string?> getEnv)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in options.PassEnv)
        {
            var value = getEnv(name);
            if (string.IsNullOrEmpty(value))
            {
                throw new InvalidOperationException(
                    $"'upstream.passEnv' names environment variable '{name}', but it is unset/empty. " +
                    "Set it, or remove it from passEnv — Darvoza refuses to start half-configured.");
            }

            resolved[name] = value;
        }

        return resolved;
    }

    /// <summary>
    /// True when the pinned MCP SDK will route this launch through <c>cmd.exe /c</c> — which on Windows
    /// is <b>every</b> command except <c>cmd.exe</c> itself.
    /// </summary>
    /// <remarks>
    /// This is not a property of the command; it is a property of the SDK. Verified against the pinned
    /// <c>ModelContextProtocol.Core</c> 1.4.0: <c>StdioClientTransport.ConnectAsync</c> rewrites the
    /// launch to <c>cmd.exe /c &lt;command&gt; &lt;args…&gt;</c> whenever
    /// <c>IsOSPlatform(Windows) &amp;&amp; Path.GetFileName(command) != "cmd.exe"</c>.
    /// <para>
    /// An earlier version of this method warned only about a literal <c>.cmd</c>/<c>.bat</c> suffix. That
    /// was doubly wrong — it missed extension-less commands that resolve through <c>PATHEXT</c> (such as
    /// <c>npx</c> → <c>npx.cmd</c>, the exact case A01-T6a set out to avoid), and it implied every other
    /// command was shell-free, which is not true on Windows for any command at all. See ADR-0004.
    /// </para>
    /// </remarks>
    public static bool LaunchesThroughWindowsShell(bool isWindows, string command) =>
        isWindows && !string.Equals(
            Path.GetFileName(command), "cmd.exe", StringComparison.OrdinalIgnoreCase);
}
