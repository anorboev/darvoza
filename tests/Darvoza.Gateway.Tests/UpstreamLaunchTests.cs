using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T6a (G-10 #1) — the upstream launch must never route our argv through a batch file. On Windows a
// bare "npx" resolves to npx.cmd, where .NET's argument escaping has known gaps, which made the strict
// ADO_ORG allowlist the load-bearing mitigation. The spec builder instead launches node with npm's
// npx-cli.js directly (no cmd.exe involvement), demoting the allowlist to defense-in-depth. These specs
// pin the whole launch contract: command choice, argv shape, the version pin, and no-PAT-in-argv.
public class UpstreamLaunchTests
{
    private const string NpxCli = @"C:\Program Files\nodejs\node_modules\npm\bin\npx-cli.js";

    private static Func<string, string?> Env(Dictionary<string, string?> map) =>
        key => map.GetValueOrDefault(key);

    [Fact]
    public void On_windows_launches_node_with_npx_cli_js_never_a_batch_file()
    {
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo",
            isWindows: true,
            getEnv: Env(new() { ["PATH"] = @"C:\Program Files\nodejs" }),
            fileExists: path => path == @"C:\Program Files\nodejs\node.exe" || path == NpxCli);

        Assert.Equal("node", spec.Command);
        Assert.Equal(NpxCli, spec.Arguments[0]);
    }

    [Fact]
    public void On_windows_an_explicit_npx_cli_override_wins()
    {
        var overridePath = @"D:\tools\npm\bin\npx-cli.js";
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo",
            isWindows: true,
            getEnv: Env(new() { [UpstreamLaunch.NpxCliPathEnvVar] = overridePath }),
            fileExists: path => path == overridePath);

        Assert.Equal("node", spec.Command);
        Assert.Equal(overridePath, spec.Arguments[0]);
    }

    [Fact]
    public void On_windows_a_missing_npx_cli_fails_fast_naming_the_override_var()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo", isWindows: true, getEnv: Env([]), fileExists: _ => false));

        Assert.Contains(UpstreamLaunch.NpxCliPathEnvVar, ex.Message);
    }

    [Fact]
    public void On_non_windows_npx_is_a_real_binary_and_is_used_directly()
    {
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo", isWindows: false, getEnv: Env([]), fileExists: _ => false);

        Assert.Equal("npx", spec.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Package_is_version_pinned_and_org_is_exactly_one_argv_element(bool isWindows)
    {
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo",
            isWindows,
            getEnv: Env(new() { ["PATH"] = @"C:\Program Files\nodejs" }),
            fileExists: _ => true);

        // Decision #3 pins @azure-devops/mcp@2.7.0; the org travels as ONE positional element (never
        // concatenated into another argument, never quoted/interpolated by us).
        Assert.Contains("-y", spec.Arguments);
        Assert.Contains("@azure-devops/mcp@2.7.0", spec.Arguments);
        Assert.Equal(1, spec.Arguments.Count(arg => arg.Contains("darvoza-demo")));
        Assert.Contains("darvoza-demo", spec.Arguments);
        Assert.Equal(["--authentication", "pat"], spec.Arguments.TakeLast(2));
    }

    // A01-T7 — the upstream server is operator-configurable, so "governance gateway in front of ANY MCP
    // server" is a verifiable claim rather than a slogan. The command + argv come from the config file
    // ONLY, and argv stays an ARRAY the whole way down (G-10 #1 / Decision #17 must stay closed).

    [Fact]
    public void With_no_upstream_configured_the_argv_is_byte_identical_to_the_pre_T7_launch()
    {
        // A regression LOCK, deliberately starting green: it pins the exact pre-A01-T7 (95d13e8) argv so
        // that generalising the upstream cannot quietly alter the demo path. Whole-sequence equality —
        // element count, order, and content — because a per-element spot check would miss a reordering.
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo",
            isWindows: true,
            getEnv: Env(new() { ["PATH"] = @"C:\Program Files\nodejs" }),
            fileExists: path => path == @"C:\Program Files\nodejs\node.exe" || path == NpxCli);

        Assert.Equal("node", spec.Command);
        Assert.Equal(
            [NpxCli, "-y", "@azure-devops/mcp@2.7.0", "darvoza-demo", "--authentication", "pat"],
            spec.Arguments);
    }

    [Fact]
    public void Configured_args_stay_separate_argv_elements_that_no_shell_could_reproduce()
    {
        // This is how "argv never passes through a shell" is ASSERTED rather than asserted-about. Each
        // argument carries characters a shell would act on — a space (word splitting), quotes, a command
        // separator, a glob, a variable reference. They survive as FOUR elements, byte-identical: any
        // shell or batch re-parse on this path would split, unquote, expand, or concatenate them.
        string[] hostile = ["one two", "a'b\"c", "&& rm -rf /", "$HOME/*"];

        var spec = UpstreamLaunch.Resolve(
            new UpstreamOptions { Command = "my-server", Args = hostile },
            adoOrg: null, isWindows: true, getEnv: Env([]), fileExists: _ => false);

        Assert.Equal(hostile, spec.Arguments);
        Assert.Equal(4, spec.Arguments.Count);
    }
    [Fact]
    public void A_configured_custom_command_is_launched_from_config()
    {
        var spec = UpstreamLaunch.Resolve(
            new UpstreamOptions { Command = "my-mcp-server", Args = ["--readonly", "/srv/data"] },
            adoOrg: null,
            isWindows: true,
            getEnv: Env([]),
            fileExists: _ => false);

        Assert.Equal("my-mcp-server", spec.Command);
        Assert.Equal(["--readonly", "/srv/data"], spec.Arguments);
    }

    [Fact]
    public void Argv_never_carries_the_pat()
    {
        // The PAT travels ONLY via the child's environment (PERSONAL_ACCESS_TOKEN) — never argv.
        var spec = UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps,
            "darvoza-demo",
            isWindows: true,
            getEnv: key => key switch
            {
                "PATH" => @"C:\Program Files\nodejs",
                "PERSONAL_ACCESS_TOKEN" => "secret-pat-value",
                "AZURE_DEVOPS_EXT_PAT" => "raw-secret-pat",
                _ => null,
            },
            fileExists: _ => true);

        Assert.DoesNotContain(spec.Arguments, arg => arg.Contains("secret-pat"));
    }
}
