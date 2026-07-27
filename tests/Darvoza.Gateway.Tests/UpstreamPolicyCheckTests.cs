using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T7 — with the upstream now operator-selectable, a policy allow-list can name tools the connected
// server does not offer (wrong server, or a benign version bump renaming a tool — G-20 shows that has
// already happened TWICE in this project). Startup reports each such name.
//
// WARNING, deliberately not fatal. Fail-fast is the right posture where the failure mode is "starts
// OPEN" — a missing policy, an unset caller key. Here the failure mode is the opposite: deny-by-default
// makes an allow-listed-but-absent tool inert (it can never be called successfully), so the gateway is
// merely MORE closed than the operator intended. Taking a governance gateway down over that would trade
// a harmless misconfiguration for an outage.
public class UpstreamPolicyCheckTests
{
    private static Policy PolicyWith(params (string Role, string[] Allow)[] roles) =>
        new(
            new Dictionary<string, string> { ["k"] = roles[0].Role },
            roles.ToDictionary(
                r => r.Role,
                r => (IReadOnlySet<string>)new HashSet<string>(r.Allow, StringComparer.Ordinal)));

    [Fact]
    public void Allowlisted_tools_the_upstream_does_not_offer_are_reported()
    {
        var policy = PolicyWith(("analyst", ["wit_get_work_item", "ghost_tool_one", "ghost_tool_two"]));

        var missing = UpstreamPolicyCheck.MissingFromUpstream(policy, ["wit_get_work_item"]);

        Assert.Equal(["ghost_tool_one", "ghost_tool_two"], missing.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_tool_allowlisted_by_two_roles_is_reported_once_not_once_per_role()
    {
        var policy = PolicyWith(
            ("analyst", ["ghost_tool"]),
            ("engineer", ["ghost_tool"]));

        Assert.Equal(["ghost_tool"], UpstreamPolicyCheck.MissingFromUpstream(policy, []));
    }

    [Fact]
    public void Nothing_is_reported_when_every_allowlisted_tool_exists_upstream()
    {
        var policy = PolicyWith(("analyst", ["wit_get_work_item"]));

        Assert.Empty(UpstreamPolicyCheck.MissingFromUpstream(
            policy, ["wit_get_work_item", "wit_create_work_item"]));
    }

    [Fact]
    public void Upstream_tools_absent_from_every_allowlist_are_NOT_reported()
    {
        // That is deny-by-default working exactly as designed — the normal state for a server offering
        // 90 tools to a role allowed 6. Reporting it would bury the signal this check exists to give.
        var policy = PolicyWith(("analyst", ["wit_get_work_item"]));

        Assert.Empty(UpstreamPolicyCheck.MissingFromUpstream(
            policy, ["wit_get_work_item", "repo_list_repos_by_project", "build_get_builds"]));
    }

    [Fact]
    public void Matching_is_exact_and_case_sensitive_like_every_other_tool_name_comparison()
    {
        var policy = PolicyWith(("analyst", ["wit_get_work_item"]));

        Assert.Equal(["wit_get_work_item"], UpstreamPolicyCheck.MissingFromUpstream(
            policy, ["WIT_GET_WORK_ITEM"]));
    }

    [Fact]
    public void Exactly_one_warning_is_logged_per_missing_tool()
    {
        // The acceptance criterion is a COUNT: a two-unknown-tool policy produces exactly two warnings.
        var logger = new RecordingLogger();
        var policy = PolicyWith(
            ("analyst", ["wit_get_work_item", "ghost_tool_one"]),
            ("engineer", ["ghost_tool_one", "ghost_tool_two"]));

        UpstreamPolicyCheck.WarnOnMissing(logger, policy, ["wit_get_work_item"]);

        Assert.Equal(2, logger.Warnings.Count());
        Assert.Single(logger.Warnings, w => w.Contains("ghost_tool_one"));
        Assert.Single(logger.Warnings, w => w.Contains("ghost_tool_two"));
    }

    // --- The startup path itself (@pr-reviewer MAJOR / @test-skeptic MEDIUM: previously untested) -----
    //
    // These drive the exact delegate UpstreamConnectionInitializer hands over, so the behaviour that
    // matters at startup — that a diagnostic can never become a new way for the host to fail — is
    // asserted rather than asserted-about.

    [Fact]
    public async Task A_failing_tools_list_warns_and_does_NOT_fail_host_startup()
    {
        var logger = new RecordingLogger();

        await UpstreamPolicyCheck.RunAsync(
            logger,
            PolicyWith(("analyst", ["wit_get_work_item"])),
            _ => throw new InvalidOperationException("upstream refused tools/list"),
            CancellationToken.None);

        Assert.Single(logger.Warnings, w => w.Contains("cross-check"));
    }

    [Fact]
    public async Task A_hanging_tools_list_cannot_stall_startup_indefinitely()
    {
        // The host's startup token has no timeout by default, so without an internal bound a connected-
        // but-wedged upstream would hang boot forever with nothing in the log explaining why.
        var logger = new RecordingLogger();

        await UpstreamPolicyCheck.RunAsync(
            logger,
            PolicyWith(("analyst", ["wit_get_work_item"])),
            async ct => { await Task.Delay(Timeout.Infinite, ct); return []; },
            CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(50));

        Assert.Single(logger.Warnings, w => w.Contains("cross-check"));
    }

    [Fact]
    public async Task Host_shutdown_during_the_check_is_NOT_swallowed_as_a_warning()
    {
        // A cancelled START is the host tearing down, not an upstream defect. Reporting it as an
        // upstream problem would be a misleading log line at exactly the wrong moment.
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var logger = new RecordingLogger();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UpstreamPolicyCheck.RunAsync(
            logger,
            PolicyWith(("analyst", ["wit_get_work_item"])),
            async ct => { await Task.Delay(Timeout.Infinite, ct); return []; },
            cts.Token));

        Assert.Empty(logger.Records);
    }

    [Fact]
    public async Task A_successful_tools_list_produces_the_per_missing_tool_warnings()
    {
        var logger = new RecordingLogger();

        await UpstreamPolicyCheck.RunAsync(
            logger,
            PolicyWith(("analyst", ["wit_get_work_item", "ghost_tool"])),
            _ => Task.FromResult<IReadOnlyList<string>>(["wit_get_work_item"]),
            CancellationToken.None);

        Assert.Single(logger.Warnings, w => w.Contains("ghost_tool"));
    }

    [Fact]
    public void A_fully_satisfied_policy_logs_nothing_at_all()
    {
        var logger = new RecordingLogger();

        UpstreamPolicyCheck.WarnOnMissing(
            logger, PolicyWith(("analyst", ["wit_get_work_item"])), ["wit_get_work_item"]);

        Assert.Empty(logger.Records);
    }
}
