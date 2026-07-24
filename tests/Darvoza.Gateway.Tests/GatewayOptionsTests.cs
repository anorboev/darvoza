using Darvoza.Gateway.Configuration;

namespace Darvoza.Gateway.Tests;

// A01-T2c — ADO_ORG must be validated at startup (fail-fast) so a malformed org never reaches the
// upstream launch. These specs pin the shape check: alphanumeric + interior hyphens only.
public class GatewayOptionsTests
{
    [Theory]
    [InlineData(null)]            // unset
    [InlineData("")]              // empty
    [InlineData("   ")]           // whitespace
    [InlineData("org/x")]         // path separator (traversal / arg-injection surface)
    [InlineData("-leading")]      // leading hyphen
    [InlineData("trailing-")]     // trailing hyphen
    [InlineData("has space")]     // whitespace inside
    [InlineData("https://dev.azure.com/org")] // a URL, not an org name
    public void IsValidAdoOrg_rejects_malformed(string? org)
    {
        Assert.False(GatewayOptions.IsValidAdoOrg(org));
    }

    [Theory]
    [InlineData("anorboev")]
    [InlineData("darvoza-demo")]
    [InlineData("a")]             // single char is a valid org
    [InlineData("MixedCase123")]
    public void IsValidAdoOrg_accepts_valid(string org)
    {
        Assert.True(GatewayOptions.IsValidAdoOrg(org));
    }

    // A01-T3 — policy path resolution honors the gitignored local override.
    // (These cases assume DARVOZA_POLICY_PATH is unset, which it is in the test environment.)
    [Fact]
    public void ResolvePolicyPath_prefers_the_local_override_when_present()
    {
        var path = GatewayOptions.ResolvePolicyPath("/root", fileExists: _ => true);

        Assert.Equal(Path.Combine("/root", GatewayOptions.LocalPolicyFileName), path);
    }

    [Fact]
    public void ResolvePolicyPath_falls_back_to_the_committed_default_when_no_local_override()
    {
        var path = GatewayOptions.ResolvePolicyPath("/root", fileExists: _ => false);

        Assert.Equal(Path.Combine("/root", GatewayOptions.DefaultPolicyFileName), path);
    }

    // A01-T6e — fingerprint-salt resolution: a configured DARVOZA_FINGERPRINT_SALT gives stable
    // fingerprints across restarts; otherwise a startup-generated random salt is used (fingerprints
    // then correlate within a run only — the documented default).
    [Fact]
    public void ResolveFingerprintSalt_uses_the_env_vars_utf8_bytes_when_set()
    {
        var salt = GatewayOptions.ResolveFingerprintSalt(
            getEnv: name => name == GatewayOptions.FingerprintSaltEnvVar ? "configured-salt" : null,
            randomBytes: _ => throw new InvalidOperationException("must not generate when configured"));

        Assert.Equal("configured-salt"u8.ToArray(), salt);
    }

    [Fact]
    public void ResolveFingerprintSalt_generates_32_random_bytes_when_unset()
    {
        var generated = new byte[32];
        Random.Shared.NextBytes(generated);

        var salt = GatewayOptions.ResolveFingerprintSalt(
            getEnv: _ => null,
            randomBytes: count => generated.Take(count).ToArray());

        Assert.Equal(32, salt.Length);
        Assert.Equal(generated, salt);
    }

    [Fact]
    public void ResolveFingerprintSalt_treats_a_whitespace_env_value_as_unset()
    {
        var salt = GatewayOptions.ResolveFingerprintSalt(
            getEnv: _ => "   ",
            randomBytes: count => new byte[count]);

        Assert.Equal(new byte[32], salt);
    }
}
