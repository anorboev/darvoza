using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Tests;

// A01-T3 — pure decision logic of the validated Policy. Deny-by-default: a tool not in the caller-role's
// allow-list is denied, and an unknown/missing key resolves to no role (empty allow-list = deny all).
public class PolicyTests
{
    private static Policy TwoRolePolicy() => new(
        keyToRole: new Dictionary<string, string>
        {
            ["analyst-key"] = "analyst",
            ["engineer-key"] = "engineer",
        },
        roleAllowlists: new Dictionary<string, IReadOnlySet<string>>
        {
            ["analyst"] = new HashSet<string> { "wit_get_work_item", "repo_list" },
            ["engineer"] = new HashSet<string> { "wit_get_work_item", "repo_list", "wit_create_work_item" },
        });

    [Fact]
    public void IsAllowed_is_true_when_tool_is_in_the_role_allowlist()
    {
        Assert.True(TwoRolePolicy().IsAllowed("analyst-key", "wit_get_work_item"));
    }

    [Fact]
    public void IsAllowed_is_false_when_tool_is_not_in_allowlist_deny_by_default()
    {
        // The analyst role has no write tool — denied even though the key is valid.
        Assert.False(TwoRolePolicy().IsAllowed("analyst-key", "wit_create_work_item"));
    }

    [Fact]
    public void Unknown_key_resolves_to_no_role_and_denies_everything()
    {
        var policy = TwoRolePolicy();
        Assert.Null(policy.RoleForKey("not-a-real-key"));
        Assert.False(policy.IsAllowed("not-a-real-key", "wit_get_work_item"));
        Assert.Empty(policy.AllowlistFor("not-a-real-key"));
    }

    [Fact]
    public void Null_key_resolves_to_no_role_and_denies_everything()
    {
        var policy = TwoRolePolicy();
        Assert.Null(policy.RoleForKey(null));
        Assert.False(policy.IsAllowed(null, "wit_get_work_item"));
        Assert.Empty(policy.AllowlistFor(null));
    }

    [Fact]
    public void AllowlistFor_returns_exactly_the_roles_tool_names()
    {
        Assert.Equal(
            ["repo_list", "wit_get_work_item"],
            TwoRolePolicy().AllowlistFor("analyst-key").OrderBy(name => name));
    }
}
