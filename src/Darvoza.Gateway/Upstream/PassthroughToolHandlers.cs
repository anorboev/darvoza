using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The pass-through stage of the tool pipeline: the gateway's list/call request handlers delegate
/// straight to the resolved (outermost) <see cref="IUpstreamToolClient"/> with no modification.
/// </summary>
/// <remarks>
/// This is the T2 seam in its no-op form. The handlers depend on <see cref="IUpstreamToolClient"/>,
/// so when A01-T3/T4 register policy/audit decorators in front, this class is unchanged — it simply
/// sees the outermost decorator as its <c>upstream</c>. No policy, no audit, no deny-by-default here.
/// </remarks>
public sealed class PassthroughToolHandlers(IUpstreamToolClient upstream)
{
    /// <summary>Returns the upstream tool set, wrapped in a protocol result, unmodified.</summary>
    public async ValueTask<ListToolsResult> ListToolsAsync(CancellationToken ct)
    {
        var tools = await upstream.ListToolsAsync(ct);
        return new ListToolsResult { Tools = [.. tools] };
    }

    /// <summary>Forwards the call request parameters to the upstream verbatim (Decision #1).</summary>
    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
        => new(upstream.CallToolAsync(callParams, ct));
}
