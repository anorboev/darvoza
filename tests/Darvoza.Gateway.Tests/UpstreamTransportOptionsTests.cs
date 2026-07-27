using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T7 (@security-reviewer LOW on PR #14) — the environment isolation that closed the HIGH lives in
// the transport options, and those were built inside a local function in Program.cs's top-level
// statements: structurally untestable, so the security-critical wiring had NO coverage. The options
// builder is now a pure function and these specs pin it.
//
// What is being protected: the gateway's environment holds every DARVOZA_KEY_* caller key and the audit
// fingerprint salt. An upstream that received them could authenticate back into Darvoza's own front leg
// as any role and de-anonymize the trail.
public class UpstreamTransportOptionsTests
{
    private static readonly UpstreamLaunchSpec AnyLaunch = new("some-server", ["--flag", "value"]);

    [Fact]
    public void A_configured_upstream_does_not_inherit_and_carries_only_its_named_variables()
    {
        var options = UpstreamLaunch.BuildTransportOptions(
            new UpstreamOptions { Command = "some-server", PassEnv = ["MY_SERVER_TOKEN"] },
            AnyLaunch,
            key => key switch
            {
                "MY_SERVER_TOKEN" => "token-value",
                "DARVOZA_KEY_ANALYST" => "caller-key-that-must-not-travel",
                "DARVOZA_FINGERPRINT_SALT" => "salt-that-must-not-travel",
                "AZURE_DEVOPS_EXT_PAT" => "pat-that-must-not-travel",
                _ => null,
            });

        Assert.False(options.InheritEnvironmentVariables);

        var childEnv = options.EnvironmentVariables!;
        Assert.Equal("token-value", childEnv["MY_SERVER_TOKEN"]);
        Assert.DoesNotContain(childEnv.Keys, key =>
            key.StartsWith("DARVOZA_", StringComparison.OrdinalIgnoreCase)
            || key is "PERSONAL_ACCESS_TOKEN" or "AZURE_DEVOPS_EXT_PAT");
        // No value leaks under a differently-named key either.
        Assert.DoesNotContain(childEnv.Values, value => value?.Contains("must-not-travel") == true);
    }

    [Fact]
    public void A_configured_upstream_still_receives_enough_environment_to_start()
    {
        // Isolation that stops a normal program from running would just get switched off. The SDK's
        // curated allowlist (PATH and friends) is the seed, so ordinary servers still launch.
        var options = UpstreamLaunch.BuildTransportOptions(
            new UpstreamOptions { Command = "some-server" }, AnyLaunch, _ => null);

        Assert.Contains(options.EnvironmentVariables!.Keys, key =>
            key.Equals("PATH", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_azure_devops_profile_inherits_and_gets_the_pat_the_upstream_expects()
    {
        var options = UpstreamLaunch.BuildTransportOptions(
            UpstreamOptions.AzureDevOps,
            AnyLaunch,
            key => key == "AZURE_DEVOPS_EXT_PAT" ? "raw-pat" : null);

        Assert.True(options.InheritEnvironmentVariables);
        Assert.Equal(
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("darvoza:raw-pat")),
            options.EnvironmentVariables!["PERSONAL_ACCESS_TOKEN"]);
    }

    [Fact]
    public void Argv_reaches_the_transport_as_the_same_collection_the_launch_resolved()
    {
        var options = UpstreamLaunch.BuildTransportOptions(
            new UpstreamOptions { Command = "some-server" },
            new UpstreamLaunchSpec("some-server", ["one two", "&& rm -rf /"]),
            _ => null);

        // Element-for-element, so nothing on Darvoza's side joined them into a command line. (What the
        // SDK does with them on Windows is a separate, documented matter — see ADR-0004.)
        Assert.Equal(["one two", "&& rm -rf /"], options.Arguments!);
        Assert.Equal("some-server", options.Command);
    }
}
