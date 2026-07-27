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

    // --- @pr-reviewer: ambiguous config shapes must fail fast, as BuildUpstream's own doc claims -----

    [Fact]
    public void Args_without_a_command_fail_fast_instead_of_being_silently_discarded()
    {
        // Deleting the 'command:' line from an upstream block is an easy slip. Before this fix the args
        // were dropped and the gateway silently launched Azure DevOps instead — the worst outcome, since
        // the operator gets a working gateway pointed at the wrong server.
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              args: ["--readonly"]
            """));

        Assert.Contains("upstream.command", ex.Message);
    }

    [Fact]
    public void A_nested_list_inside_args_fails_fast_instead_of_stringifying_to_garbage()
    {
        // Without this, the element reaches argv as "System.Collections.Generic.List`1[System.Object]".
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              command: node
              args: [["a", "b"], "c"]
            """));

        Assert.Contains("upstream.args", ex.Message);
    }

    [Fact]
    public void A_present_but_blank_profile_fails_fast_rather_than_silently_defaulting()
    {
        // A typo'd profile already failed fast; a whitespace one quietly selected Azure DevOps. Same
        // operator mistake, so it deserves the same answer.
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              profile: "   "
            """));

        Assert.Contains("upstream.profile", ex.Message);
    }

    // --- @security-reviewer HIGH: a configured upstream must not inherit the gateway's secrets -------

    [Fact]
    public void A_configured_upstream_does_not_inherit_the_gateway_environment_by_default()
    {
        var policy = Parse("""
            upstream:
              command: some-third-party-server
            """);

        Assert.False(policy.Upstream.InheritEnvironment);
        Assert.Empty(policy.Upstream.PassEnv);
    }

    [Fact]
    public void The_azure_devops_profile_still_inherits_so_the_demo_path_is_unchanged()
    {
        Assert.True(UpstreamOptions.AzureDevOps.InheritEnvironment);
    }

    [Fact]
    public void PassEnv_names_variables_to_forward_and_never_carries_their_values()
    {
        // Same idiom as 'keyEnv' throughout this project: the config file names an environment variable,
        // the VALUE is read from the environment and never written in the file.
        var policy = Parse("""
            upstream:
              command: some-third-party-server
              passEnv: [MY_SERVER_TOKEN, MY_SERVER_URL]
            """);

        Assert.Equal(["MY_SERVER_TOKEN", "MY_SERVER_URL"], policy.Upstream.PassEnv);
    }

    [Fact]
    public void PassEnv_is_rejected_for_a_profile_upstream_that_inherits_everything_anyway()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Parse("""
            upstream:
              profile: azure-devops
              passEnv: [SOMETHING]
            """));

        Assert.Contains("passEnv", ex.Message);
    }

    [Fact]
    public void The_forwarded_child_environment_contains_only_the_named_variables()
    {
        var policy = Parse("""
            upstream:
              command: some-third-party-server
              passEnv: [MY_SERVER_TOKEN]
            """);

        var child = UpstreamOptions.BuildPassedEnvironment(
            policy.Upstream,
            key => key switch
            {
                "MY_SERVER_TOKEN" => "token-value",
                "DARVOZA_KEY_ANALYST" => "caller-key-that-must-not-travel",
                "DARVOZA_FINGERPRINT_SALT" => "salt-that-must-not-travel",
                "AZURE_DEVOPS_EXT_PAT" => "pat-that-must-not-travel",
                _ => null,
            });

        Assert.Equal(["MY_SERVER_TOKEN"], child.Keys);
        Assert.Equal("token-value", child["MY_SERVER_TOKEN"]);
    }

    [Fact]
    public void An_unset_passEnv_variable_fails_fast_rather_than_launching_half_configured()
    {
        // Matches the loader's posture for a caller whose keyEnv is unset: refuse to start rather than
        // hand the upstream a silently-missing credential and fail on the first call instead.
        var policy = Parse("""
            upstream:
              command: some-third-party-server
              passEnv: [MISSING_TOKEN]
            """);

        var ex = Assert.Throws<InvalidOperationException>(
            () => UpstreamOptions.BuildPassedEnvironment(policy.Upstream, _ => null));

        Assert.Contains("MISSING_TOKEN", ex.Message);
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
    // On Windows the pinned SDK wraps EVERY command in `cmd.exe /c` — including plain `node`, which is
    // what A01-T6a switched to in order to escape `npx.cmd`. The predicate this replaces matched only a
    // literal .cmd/.bat suffix, so it missed `npx` (PATHEXT-resolved) AND implied everything else was
    // shell-free. Both were wrong; see ADR-0004 §"What the pinned SDK does at the spawn boundary".
    [InlineData(true, "node", true)]
    [InlineData(true, "npx", true)]
    [InlineData(true, "wrapper.cmd", true)]
    [InlineData(true, "server.exe", true)]
    [InlineData(true, @"C:\Windows\System32\cmd.exe", false)]
    [InlineData(false, "node", false)]
    [InlineData(false, "npx", false)]
    public void The_pinned_SDK_routes_windows_launches_through_cmd_exe(
        bool isWindows, string command, bool throughShell)
    {
        Assert.Equal(throughShell, UpstreamOptions.LaunchesThroughWindowsShell(isWindows, command));
    }
}
