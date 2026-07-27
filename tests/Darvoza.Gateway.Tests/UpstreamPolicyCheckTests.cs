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

    [Fact]
    public void A_fully_satisfied_policy_logs_nothing_at_all()
    {
        var logger = new RecordingLogger();

        UpstreamPolicyCheck.WarnOnMissing(
            logger, PolicyWith(("analyst", ["wit_get_work_item"])), ["wit_get_work_item"]);

        Assert.Empty(logger.Records);
    }
}
