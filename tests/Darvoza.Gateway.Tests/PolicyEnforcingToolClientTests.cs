using Darvoza.Gateway.Audit;
using Darvoza.Gateway.Configuration;
using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Tests;

// A01-T3 — the deny-by-default decorator IS the artifact. The headline contract: a tool not in the
// caller-role's allow-list is denied WITHOUT the inner client being called. Plus: allowed forwards,
// list-tools is filtered to the role, and unknown/missing keys see nothing and call nothing.
public class PolicyEnforcingToolClientTests
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

    private static (PolicyEnforcingToolClient sut, FakeUpstreamToolClient inner) Build(string? callerKey)
    {
        var inner = new FakeUpstreamToolClient
        {
            Tools =
            [
                new Tool { Name = "wit_get_work_item" },
                new Tool { Name = "repo_list" },
                new Tool { Name = "wit_create_work_item" },
            ],
            CallResult = new CallToolResult { Content = [new TextContentBlock { Text = "upstream-ok" }] },
        };
        // No audit decorator in these T3-focused tests: an empty decision context (Current == null) makes
        // the decision-publish a no-op, so enforcement behavior is unchanged.
        var sut = new PolicyEnforcingToolClient(
            inner, TwoRolePolicy(), new FakeCallerKeyProvider { Key = callerKey },
            new AsyncLocalCallDecisionContext(), new CallerFingerprint("test-salt"u8.ToArray()));
        return (sut, inner);
    }

    [Fact]
    public async Task Denied_tool_returns_isError_and_never_calls_the_inner_client()
    {
        var (sut, inner) = Build("analyst-key"); // analyst may not create work items

        var result = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_create_work_item" }, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Null(inner.LastCallParams); // short-circuit BEFORE the inner call — the whole point
    }

    [Fact]
    public async Task Allowed_tool_is_forwarded_verbatim_and_returns_the_inner_result()
    {
        var (sut, inner) = Build("engineer-key");
        var callParams = new CallToolRequestParams { Name = "wit_create_work_item" };

        var result = await sut.CallToolAsync(callParams, CancellationToken.None);

        Assert.Same(callParams, inner.LastCallParams); // verbatim forward
        Assert.NotEqual(true, result.IsError);         // IsError is bool?: inner result carries no error
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.Equal("upstream-ok", text.Text);        // the inner result is returned unchanged
    }

    [Fact]
    public async Task ListTools_is_filtered_to_the_callers_role_allowlist()
    {
        var (sut, _) = Build("analyst-key");

        var tools = await sut.ListToolsAsync(CancellationToken.None);

        Assert.Equal(["repo_list", "wit_get_work_item"], tools.Select(t => t.Name).OrderBy(name => name));
    }

    [Fact]
    public async Task Unknown_key_denies_the_call_and_lists_no_tools()
    {
        var (sut, inner) = Build("intruder-key");

        var call = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);
        var tools = await sut.ListToolsAsync(CancellationToken.None);

        Assert.True(call.IsError);
        Assert.Null(inner.LastCallParams);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task Missing_key_denies_the_call_and_lists_no_tools()
    {
        var (sut, inner) = Build(null);

        var call = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_get_work_item" }, CancellationToken.None);
        var tools = await sut.ListToolsAsync(CancellationToken.None);

        Assert.True(call.IsError);
        Assert.Null(inner.LastCallParams);
        Assert.Empty(tools);
    }

    [Fact]
    public async Task Denial_result_carries_a_non_empty_text_message()
    {
        var (sut, _) = Build("analyst-key");

        var result = await sut.CallToolAsync(
            new CallToolRequestParams { Name = "wit_create_work_item" }, CancellationToken.None);

        Assert.True(result.IsError);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content));
        Assert.False(string.IsNullOrWhiteSpace(text.Text));
    }
}
