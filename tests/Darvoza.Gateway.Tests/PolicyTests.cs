using System.Security.Cryptography;
using System.Text;
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

    [Fact]
    public void AllowlistForRole_matches_AllowlistFor_without_a_second_key_scan()
    {
        // The role-keyed overload exists so CallToolAsync pays the constant-time key scan ONCE:
        // resolve the role, then consult the allow-list by role. Same answers as the key-keyed path.
        var policy = TwoRolePolicy();

        Assert.Equal(policy.AllowlistFor("analyst-key"), policy.AllowlistForRole("analyst"));
        Assert.Equal(policy.AllowlistFor("engineer-key"), policy.AllowlistForRole("engineer"));
        Assert.Empty(policy.AllowlistForRole(null));
        Assert.Empty(policy.AllowlistForRole("not-a-role"));
    }

    [Fact]
    public void Keys_are_stored_as_fixed_width_sha256_digests_not_raw_strings()
    {
        // T6c (G-13 #1): RoleForKey must not compare raw key strings — the key is hashed to a
        // fixed-width SHA-256 digest at construction and lookups run CryptographicOperations
        // .FixedTimeEquals over every stored digest. Pin the storage half of that contract:
        // exactly one 32-byte digest per caller, each the SHA-256 of its key.
        var entries = TwoRolePolicy().DigestEntries;

        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(32, e.Digest.Length));
        Assert.Contains(entries, e =>
            e.Digest.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("analyst-key")))
            && e.Role == "analyst");
        Assert.Contains(entries, e =>
            e.Digest.SequenceEqual(SHA256.HashData(Encoding.UTF8.GetBytes("engineer-key")))
            && e.Role == "engineer");
    }

    [Fact]
    public void A_key_differing_only_in_a_trailing_byte_is_unknown()
    {
        // Guards the digest path against prefix-match shortcuts: near-miss keys resolve to no role.
        var policy = TwoRolePolicy();
        Assert.Null(policy.RoleForKey("analyst-key2"));
        Assert.Null(policy.RoleForKey("analyst-ke"));
        Assert.Null(policy.RoleForKey(""));
    }
}
