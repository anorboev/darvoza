using System.Text.Json;
using Darvoza.Gateway.Audit;
using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Tests;

// A01-T4 — audit-path resolution precedence (env override > gitignored default) and the redacted
// argument-summary contract (key names + count + digest, never the values).
public class AuditConfigAndArgsTests
{
    [Fact]
    public void ResolveAuditPath_prefers_the_env_override()
    {
        var path = GatewayOptions.ResolveAuditPath("/root", getEnv: _ => "/custom/trail.jsonl");

        Assert.Equal("/custom/trail.jsonl", path);
    }

    [Fact]
    public void ResolveAuditPath_falls_back_to_the_gitignored_default_under_content_root()
    {
        var path = GatewayOptions.ResolveAuditPath("/root", getEnv: _ => null);

        Assert.Equal(Path.Combine("/root", "audit", "darvoza-audit.jsonl"), path);
    }

    [Fact]
    public void ResolveAuditPath_ignores_a_whitespace_env_override()
    {
        var path = GatewayOptions.ResolveAuditPath("/root", getEnv: _ => "   ");

        Assert.Equal(Path.Combine("/root", "audit", "darvoza-audit.jsonl"), path);
    }

    [Fact]
    public void AuditArgs_from_null_yields_an_empty_summary_with_no_digest()
    {
        var args = AuditArgs.From(null);

        Assert.Empty(args.Keys);
        Assert.Equal(0, args.Count);
        Assert.Null(args.Sha256);
    }

    [Fact]
    public void AuditArgs_sorts_keys_ordinally_and_produces_a_digest()
    {
        var args = AuditArgs.From(new Dictionary<string, JsonElement>
        {
            ["zebra"] = JsonSerializer.SerializeToElement("SECRET-Z"),
            ["alpha"] = JsonSerializer.SerializeToElement(7),
        });

        Assert.Equal(["alpha", "zebra"], args.Keys);
        Assert.Equal(2, args.Count);
        Assert.False(string.IsNullOrEmpty(args.Sha256));
    }

    [Fact]
    public void AuditArgs_digest_is_stable_for_equal_values_and_changes_with_them()
    {
        AuditArgs Of(int v) =>
            AuditArgs.From(new Dictionary<string, JsonElement> { ["id"] = JsonSerializer.SerializeToElement(v) });

        Assert.Equal(Of(1).Sha256, Of(1).Sha256);    // same arguments → same digest (correlation)
        Assert.NotEqual(Of(1).Sha256, Of(2).Sha256); // different values → different digest
    }
}
