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
            "darvoza-demo", isWindows: true, getEnv: Env([]), fileExists: _ => false));

        Assert.Contains(UpstreamLaunch.NpxCliPathEnvVar, ex.Message);
    }

    [Fact]
    public void On_non_windows_npx_is_a_real_binary_and_is_used_directly()
    {
        var spec = UpstreamLaunch.Resolve(
            "darvoza-demo", isWindows: false, getEnv: Env([]), fileExists: _ => false);

        Assert.Equal("npx", spec.Command);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Package_is_version_pinned_and_org_is_exactly_one_argv_element(bool isWindows)
    {
        var spec = UpstreamLaunch.Resolve(
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

    [Fact]
    public void Argv_never_carries_the_pat()
    {
        // The PAT travels ONLY via the child's environment (PERSONAL_ACCESS_TOKEN) — never argv.
        var spec = UpstreamLaunch.Resolve(
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
