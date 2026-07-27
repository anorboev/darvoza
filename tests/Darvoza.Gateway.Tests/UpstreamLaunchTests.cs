using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T6a (G-10 #1) — the upstream launch must never route our argv through a BATCH FILE. On Windows a
// bare "npx" resolves to npx.cmd, where .NET's argument escaping has known gaps, so the spec builder
// launches node with npm's npx-cli.js directly instead. These specs pin the whole launch contract:
// command choice, argv shape, the version pin, and no-PAT-in-argv.
//
// A01-T7 correction: that avoids the batch file's OWN re-parse but does NOT remove cmd.exe — the pinned
// SDK wraps every Windows launch in `cmd.exe /c` (ADR-0004). An earlier version of this header claimed
// "no cmd.exe involvement" and said the ADO_ORG allowlist was thereby demoted to defense-in-depth. Both
// were wrong: on Windows that allowlist is LOAD-BEARING, and GatewayOptionsTests pins it accordingly.
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
    public void Configured_args_stay_separate_argv_elements_through_darvozas_own_layer()
    {
        // SCOPE, stated honestly (@test-skeptic HIGH on PR #14): this asserts that DARVOZA never joins,
        // splits, or rewrites argv — it is a lock against a future refactor doing so, and nothing more.
        // It does NOT prove "argv never passes through a shell", because on Windows that claim is FALSE
        // one layer below: the pinned MCP SDK rewrites every launch to `cmd.exe /c …` (ADR-0004,
        // §"What the pinned SDK does at the spawn boundary"). The earlier version of this comment
        // claimed the stronger property; that was an overclaim.
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

    // --- Azure DevOps PAT marshalling (Decision #3) --------------------------------------------------
    //
    // @test-skeptic MEDIUM on PR #14: this had only INCIDENTAL coverage from the e2e booting the real
    // composition root, and A01-T7's move to a configured e2e upstream removed it — production behaviour
    // surviving with nothing testing it. Pulled out of Program.cs into a pure function and pinned here.

    [Fact]
    public void A_raw_pat_is_base64_encoded_as_the_basic_auth_shape_the_upstream_expects()
    {
        // The upstream's "pat" mode reads PERSONAL_ACCESS_TOKEN = base64("email:pat"). Azure DevOps
        // ignores the username, so "darvoza" is a fixed placeholder, not an operator identity.
        var token = UpstreamLaunch.ResolveAzureDevOpsToken(
            key => key == "AZURE_DEVOPS_EXT_PAT" ? "raw-pat-value" : null);

        Assert.Equal(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("darvoza:raw-pat-value")),
            token);
    }

    [Fact]
    public void A_pre_encoded_personal_access_token_is_passed_through_untouched()
    {
        var token = UpstreamLaunch.ResolveAzureDevOpsToken(
            key => key == "PERSONAL_ACCESS_TOKEN" ? "already-base64" : null);

        Assert.Equal("already-base64", token);
    }

    [Fact]
    public void A_pre_encoded_token_wins_over_a_raw_pat()
    {
        var token = UpstreamLaunch.ResolveAzureDevOpsToken(key => key switch
        {
            "PERSONAL_ACCESS_TOKEN" => "already-base64",
            "AZURE_DEVOPS_EXT_PAT" => "raw-pat-value",
            _ => null,
        });

        Assert.Equal("already-base64", token);
    }

    [Fact]
    public void No_credential_at_all_fails_fast_naming_both_accepted_variables()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => UpstreamLaunch.ResolveAzureDevOpsToken(_ => null));

        Assert.Contains("PERSONAL_ACCESS_TOKEN", ex.Message);
        Assert.Contains("AZURE_DEVOPS_EXT_PAT", ex.Message);
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
