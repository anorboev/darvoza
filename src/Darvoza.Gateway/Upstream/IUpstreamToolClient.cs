using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The gateway's boundary to the upstream MCP tool source AND the pipeline seam.
/// </summary>
/// <remarks>
/// <para>
/// A01-T2 ships exactly one implementation — <see cref="McpUpstreamToolClient"/>, a transparent
/// pass-through to the official Azure DevOps MCP server. This is the <b>extensibility seam</b>:
/// A01-T3 (policy) and A01-T4 (audit) register <i>decorators</i> of this interface in front of the
/// concrete client (each wrapping an inner <see cref="IUpstreamToolClient"/>), so the request
/// handlers — which depend only on the DI-resolved outermost instance — never change.
/// </para>
/// <para>
/// T3 will filter <see cref="ListToolsAsync"/> results and deny-by-default in
/// <see cref="CallToolAsync"/> (short-circuit before delegating to the inner client); T4 will record
/// every allowed/denied call (around the inner call). <b>Neither behavior exists yet</b> — T2 is
/// transparent pass-through only.
/// </para>
/// </remarks>
public interface IUpstreamToolClient
{
    /// <summary>Lists the tools the upstream exposes.</summary>
    Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct);

    /// <summary>Invokes a tool, forwarding the request parameters verbatim.</summary>
    /// <remarks><see cref="ValueTask{T}"/> to match the SDK's return type and avoid a wrap/unwrap
    /// allocation through the pass-through stage. Await it at most once (standard ValueTask contract).</remarks>
    ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct);
}
