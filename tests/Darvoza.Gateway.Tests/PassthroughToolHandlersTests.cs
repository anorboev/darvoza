using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Tests;

// A01-T2 — the gateway's tool handlers must be a TRANSPARENT pass-through over the seam: list
// returns the upstream set unmodified, call forwards params verbatim, and an upstream error surfaces
// as a clean isError result. (Mirrors spike AC2–AC4; NO policy/audit yet.)
public class PassthroughToolHandlersTests
{
    [Fact]
    public async Task ListTools_returns_the_upstream_set_unmodified()
    {
        var fake = new FakeUpstreamToolClient
        {
            Tools = [new Tool { Name = "core_list_projects" }, new Tool { Name = "wit_get_work_item" }],
        };
        var handlers = new PassthroughToolHandlers(fake);

        var result = await handlers.ListToolsAsync(CancellationToken.None);

        Assert.Equal(["core_list_projects", "wit_get_work_item"], result.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task CallTool_forwards_the_params_object_verbatim()
    {
        var fake = new FakeUpstreamToolClient();
        var handlers = new PassthroughToolHandlers(fake);
        var callParams = new CallToolRequestParams { Name = "core_list_projects" };

        await handlers.CallToolAsync(callParams, CancellationToken.None);

        // Verbatim forward = the SAME object, no copy/conversion (Decision #1).
        Assert.Same(callParams, fake.LastCallParams);
    }

    [Fact]
    public async Task CallTool_surfaces_an_upstream_isError_result_unchanged()
    {
        var fake = new FakeUpstreamToolClient
        {
            CallResult = new CallToolResult { IsError = true, Content = [] },
        };
        var handlers = new PassthroughToolHandlers(fake);

        var result = await handlers.CallToolAsync(
            new CallToolRequestParams { Name = "bad_tool" }, CancellationToken.None);

        Assert.True(result.IsError);
    }
}
