using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Tests;

// A01-T3 — parse + validation + fail-fast. The loader is the "never start open" guard: every invalid,
// missing, or half-configured policy must throw so the host refuses to start.
public class PolicyLoaderTests
{
    private const string ValidYaml = """
        callers:
          - keyEnv: TEST_ANALYST_KEY
            role: analyst
          - keyEnv: TEST_ENGINEER_KEY
            role: engineer
        roles:
          analyst:
            allow:
              - wit_get_work_item
              - repo_list
          engineer:
            allow:
              - wit_get_work_item
              - wit_create_work_item
        """;

    private static Func<string, string?> Env(Dictionary<string, string?> map) => key => map.GetValueOrDefault(key);

    [Fact]
    public void Parses_a_valid_policy_resolving_each_keyEnv_to_its_role()
    {
        var env = Env(new() { ["TEST_ANALYST_KEY"] = "a-secret", ["TEST_ENGINEER_KEY"] = "e-secret" });

        var policy = PolicyLoader.Parse(ValidYaml, "test", env);

        Assert.Equal("analyst", policy.RoleForKey("a-secret"));
        Assert.Equal("engineer", policy.RoleForKey("e-secret"));
        Assert.True(policy.IsAllowed("e-secret", "wit_create_work_item"));
        Assert.False(policy.IsAllowed("a-secret", "wit_create_work_item")); // deny-by-default
    }

    [Fact]
    public void Missing_file_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"darvoza-no-policy-{Guid.NewGuid():N}.yaml");

        var ex = Assert.Throws<InvalidOperationException>(() => PolicyLoader.Load(path));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public void Unparseable_yaml_throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => PolicyLoader.Parse("callers: [ unterminated", "test", _ => "x"));
        Assert.Contains("not valid YAML", ex.Message);
    }

    [Fact]
    public void Caller_referencing_an_undefined_role_throws()
    {
        var yaml = """
            callers:
              - keyEnv: K
                role: ghost
            roles:
              analyst:
                allow: [repo_list]
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => PolicyLoader.Parse(yaml, "test", _ => "secret"));
        Assert.Contains("undefined role", ex.Message);
    }

    [Fact]
    public void Two_callers_resolving_to_the_same_key_value_throws()
    {
        var yaml = """
            callers:
              - keyEnv: K1
                role: analyst
              - keyEnv: K2
                role: analyst
            roles:
              analyst:
                allow: [repo_list]
            """;

        // Both env vars resolve to the same secret — an ambiguous caller mapping.
        var ex = Assert.Throws<InvalidOperationException>(() => PolicyLoader.Parse(yaml, "test", _ => "same-secret"));
        Assert.Contains("same key value", ex.Message);
    }

    [Fact]
    public void Caller_whose_keyEnv_is_unset_throws_fail_fast()
    {
        // TEST_ENGINEER_KEY is unset — the host must not start half-configured (locked decision).
        var env = Env(new() { ["TEST_ANALYST_KEY"] = "a-secret", ["TEST_ENGINEER_KEY"] = null });

        var ex = Assert.Throws<InvalidOperationException>(() => PolicyLoader.Parse(ValidYaml, "test", env));
        Assert.Contains("TEST_ENGINEER_KEY", ex.Message);
    }

    [Fact]
    public void Role_with_an_empty_allow_list_is_valid_and_denies_all()
    {
        var yaml = """
            callers:
              - keyEnv: K
                role: locked
            roles:
              locked:
                allow: []
            """;

        var policy = PolicyLoader.Parse(yaml, "test", _ => "secret");

        Assert.Equal("locked", policy.RoleForKey("secret"));
        Assert.False(policy.IsAllowed("secret", "anything"));
        Assert.Empty(policy.AllowlistFor("secret"));
    }
}
