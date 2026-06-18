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
}
