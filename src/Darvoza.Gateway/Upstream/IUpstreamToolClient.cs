using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The gateway's boundary to the upstream MCP tool source AND the pipeline seam.
/// </summary>
/// <remarks>
/// <para>
/// There is exactly one implementation — <see cref="McpUpstreamToolClient"/>, a transparent
/// pass-through to the configured upstream MCP server (A01-T7 / ADR-0004; the official Azure DevOps
/// server is the default profile). This is the <b>extensibility seam</b>:
/// A01-T3 (policy) and A01-T4 (audit) register <i>decorators</i> of this interface in front of the
/// concrete client (each wrapping an inner <see cref="IUpstreamToolClient"/>), so the request
/// handlers — which depend only on the DI-resolved outermost instance — never change.
/// </para>
/// <para>
/// <b>Both decorators now ship.</b> A01-T3 (PR #5) filters <see cref="ListToolsAsync"/> results and
/// denies by default in <see cref="CallToolAsync"/>, short-circuiting before it delegates to the inner
/// client; A01-T4 (PR #6) records every allowed AND denied call around the inner call — exactly one
/// audit record per call, the 100%-coverage NFR. The concrete client below stays a transparent
/// pass-through; the governance lives entirely in the decorators, which is what lets the upstream be
/// swapped (A01-T7 / ADR-0004) without policy or audit knowing.
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
