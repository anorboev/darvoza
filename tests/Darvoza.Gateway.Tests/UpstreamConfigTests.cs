using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T7 — the upstream MCP server is operator-configurable from the CONFIG FILE ONLY (ADR-0004). These
// specs pin the config surface: what the file may say, what it may NOT say, and — critically — that no
// environment variable, header, or other ambient input can select the upstream command. The whole point
// of A01-T6a (G-10 #1 / Decision #17) was that launching an external process with influenced argv is
// dangerous; generalising the upstream must narrow that input source, not widen it.
public class UpstreamConfigTests
{
    private const string Callers = """
        callers:
          - keyEnv: TEST_ANALYST_KEY
            role: analyst
        roles:
          analyst:
            allow: [wit_get_work_item]
        """;

    private static Func<string, string?> Env() => key => key == "TEST_ANALYST_KEY" ? "a-secret" : null;

    private static Policy Parse(string upstreamSection) =>
        PolicyLoader.Parse($"{Callers}\n{upstreamSection}", "test", Env());

    [Fact]
    public void An_absent_upstream_section_defaults_to_the_azure_devops_profile()
    {
        var policy = PolicyLoader.Parse(Callers, "test", Env());

        Assert.Equal(UpstreamOptions.AzureDevOpsProfile, policy.Upstream.Profile);
        Assert.False(policy.Upstream.IsCustom);
    }

    [Fact]
    public void An_explicit_azure_devops_profile_is_identical_to_no_configuration()
    {
        var configured = Parse("""
            upstream:
              profile: azure-devops
            """);

        Assert.Equal(UpstreamOptions.AzureDevOps, configured.Upstream);
    }

    [Fact]
    public void A_custom_command_and_args_are_read_from_the_config_file()
    {
        var policy = Parse("""
            upstream:
              command: node
              args: ["/srv/my-mcp-server/index.js", "--readonly"]
            """);

        Assert.True(policy.Upstream.IsCustom);
        Assert.Equal("node", policy.Upstream.Command);
        Assert.Equal(["/srv/my-mcp-server/index.js", "--readonly"], policy.Upstream.Args);
    }

    [Fact]
    public void Args_given_as_a_single_string_are_REJECTED_never_split_on_whitespace()
    {
        // The anti-shell contract in config form: Darvoza never turns a string into an argv array,
        // because that is precisely the word-splitting step a shell performs. An operator who writes a
        // command line as one string gets a startup failure, not a silent, differently-parsed launch.
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              command: node
              args: "server.js --readonly"
            """));

        Assert.Contains("list", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Declaring_both_a_profile_and_a_command_fails_fast_rather_than_picking_one()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              profile: azure-devops
              command: node
            """));

        Assert.Contains("mutually exclusive", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_blank_command_fails_fast()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              command: "   "
            """));

        Assert.Contains("command", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unknown_profile_fails_fast_pointing_at_the_custom_command_escape_hatch()
    {
        var policy = Parse("""
            upstream:
              profile: not-a-real-profile
            """);

        var ex = Assert.Throws<InvalidOperationException>(() => UpstreamLaunch.Resolve(
            policy.Upstream, adoOrg: "darvoza-demo", isWindows: false,
            getEnv: _ => null, fileExists: _ => false));

        Assert.Contains("upstream.command", ex.Message);
    }

    [Fact]
    public void The_upstream_command_is_NOT_readable_from_any_environment_variable()
    {
        // Constraint 3 / ADR-0004: config file only. A hostile environment — every variable answering with
        // an attacker-chosen command — must not change what gets launched. Note the header/body cases are
        // closed STRUCTURALLY rather than by a check: the launch spec is resolved before the web host is
        // built, so no request can exist yet, let alone influence it.
        var policy = Parse("""
            upstream:
              command: the-configured-server
              args: ["--configured"]
            """);

        var spec = UpstreamLaunch.Resolve(
            policy.Upstream, adoOrg: "darvoza-demo", isWindows: true,
            getEnv: _ => "attacker-controlled-command",
            fileExists: _ => true);

        Assert.Equal("the-configured-server", spec.Command);
        Assert.Equal(["--configured"], spec.Arguments);
    }

    [Fact]
    public void Resolving_the_default_upstream_reads_ONLY_the_npx_override_and_PATH()
    {
        // Pins the environment surface itself: if a future change ever started reading, say,
        // DARVOZA_UPSTREAM_COMMAND, this test fails. That is the honest form of "not settable from an
        // environment variable" — an assertion about what is read, not a comment claiming it.
        var queried = new List<string>();

        UpstreamLaunch.Resolve(
            UpstreamOptions.AzureDevOps, "darvoza-demo", isWindows: true,
            getEnv: key => { queried.Add(key); return key == "PATH" ? @"C:\nodejs" : null; },
            fileExists: _ => true);

        Assert.Equal(
            [UpstreamLaunch.NpxCliPathEnvVar, "PATH"],
            queried.Distinct().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_custom_upstream_never_probes_the_filesystem_for_node_or_npx()
    {
        // Closes G-29 by construction: with a configured upstream the resolver does no node/npx discovery,
        // so `dotnet test` (whose e2e boots the real composition root) stops requiring Node on PATH.
        var probed = new List<string>();

        var spec = UpstreamLaunch.Resolve(
            new UpstreamOptions { Command = "stub-mcp-server" },
            adoOrg: null, isWindows: true,
            getEnv: _ => throw new InvalidOperationException("must not read the environment"),
            fileExists: path => { probed.Add(path); return true; });

        Assert.Empty(probed);
        Assert.Equal("stub-mcp-server", spec.Command);
        Assert.Empty(spec.Arguments);
    }

    [Fact]
    public void The_shipped_policy_example_still_resolves_to_the_default_azure_devops_upstream()
    {
        // G-14: policy.yaml is gitignored and policy.example.yaml is what a fresh clone copies. So the
        // example IS the default path — if A01-T7's documentation block ever broke its parse, or its
        // commented-out upstream section became live, every first run would break. Asserted, not assumed.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Darvoza.slnx")))
            root = root.Parent;

        Assert.NotNull(root);
        var example = Path.Combine(root.FullName, "policy.example.yaml");

        var policy = PolicyLoader.Parse(
            File.ReadAllText(example),
            example,
            key => key.StartsWith("DARVOZA_KEY_", StringComparison.Ordinal) ? $"value-of-{key}" : null);

        Assert.Equal(UpstreamOptions.AzureDevOps, policy.Upstream);
        Assert.Equal("analyst", policy.RoleForKey("value-of-DARVOZA_KEY_ANALYST"));
        Assert.Equal("engineer", policy.RoleForKey("value-of-DARVOZA_KEY_ENGINEER"));
    }

    [Theory]
    [InlineData("wrapper.cmd", true)]
    [InlineData("wrapper.BAT", true)]
    [InlineData("node", false)]
    [InlineData("server.exe", false)]
    public void Windows_batch_commands_are_identified_so_startup_can_warn(string command, bool isBatch)
    {
        // Not a gate failure — the operator supplies the command AND the args from the same trusted file,
        // so no untrusted input reaches the batch re-parse. But .NET's escaping for batch files has known
        // gaps (the very reason A01-T6a bypassed npx.cmd), so the operator is told rather than left to
        // discover it. See ADR-0004.
        Assert.Equal(isBatch, UpstreamOptions.IsWindowsBatchCommand(command));
    }
}
