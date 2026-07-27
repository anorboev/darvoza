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
    // A01-T7 (@security-reviewer): ADO_ORG is the one ENVIRONMENT-derived element of the azure-devops
    // profile's argv, and the pinned SDK routes that argv through `cmd.exe /c` on Windows (ADR-0004).
    // This allowlist is therefore the load-bearing mitigation there — not the absent shell A01-T6a
    // believed it had achieved. Pinning every cmd.exe metacharacter so a future relaxation of
    // AdoOrgPattern fails a TEST rather than depending on someone reading a comment.
    [InlineData("org&whoami")]    // command separator
    [InlineData("org|whoami")]    // pipe
    [InlineData("org>out.txt")]   // redirect out
    [InlineData("org<in.txt")]    // redirect in
    [InlineData("org^x")]         // cmd.exe escape character
    [InlineData("org%PATH%")]     // cmd.exe variable expansion
    [InlineData("org\"x")]        // quote
    [InlineData("org(x)")]        // grouping
    [InlineData("org;x")]         // separator
    [InlineData("org$x")]         // POSIX expansion (non-Windows launch path)
    [InlineData("org`x")]         // POSIX command substitution
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

    // A01-T6b — the non-loopback startup warning's URL classifier. X-Darvoza-Key is app-layer
    // authorization, NOT transport authentication, so the operator must be told when the gateway
    // binds beyond loopback. Anything unparseable or odd classifies as NON-loopback (warn) — and,
    // per G-22, an empty host must never count as loopback.
    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    [InlineData("https://localhost")]
    public void IsLoopbackUrl_accepts_loopback_binds(string url)
    {
        Assert.True(GatewayOptions.IsLoopbackUrl(url));
    }

    // A01-T6f — the Unix audit-dir permission tripwire: any group/other access bit on the audit
    // directory means a local reader could read (or tamper with the visibility of) the trail.
    [Theory]
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, false)] // 700 — fine
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead, true)]                                                            // 740 — group can read
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, true)]                                // 705 — world can read
    [InlineData(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupWrite, true)]                                                           // group can write
    public void IsGroupOrWorldAccessible_flags_any_non_owner_access(UnixFileMode mode, bool tooOpen)
    {
        Assert.Equal(tooOpen, GatewayOptions.IsGroupOrWorldAccessible(mode));
    }

    [Theory]
    [InlineData("http://0.0.0.0:5000")]      // wildcard IPv4
    [InlineData("http://[::]:5000")]         // wildcard IPv6
    [InlineData("http://+:5000")]            // Kestrel wildcard (unparseable as Uri)
    [InlineData("http://*:5000")]            // Kestrel wildcard
    [InlineData("http://192.168.1.20:5000")] // a real network address
    [InlineData("http://darvoza.internal")]  // a hostname that isn't localhost
    [InlineData("file:///C:/x")]             // non-HTTP scheme + empty host (the G-22 trap)
    [InlineData("")]                         // unparseable
    public void IsLoopbackUrl_treats_everything_else_as_non_loopback(string url)
    {
        Assert.False(GatewayOptions.IsLoopbackUrl(url));
    }
}
