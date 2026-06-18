using Darvoza.Gateway.Configuration;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The A01-T3 governance stage: an <see cref="IUpstreamToolClient"/> decorator that enforces the
/// declarative <see cref="Policy"/> with <b>deny-by-default</b>. This is what makes Darvoza a governance
/// gateway rather than a transparent proxy.
/// </summary>
/// <remarks>
/// <para>
/// Registered as the outermost <see cref="IUpstreamToolClient"/> (ADR-0002 decorator seam), wrapping the
/// concrete <see cref="McpUpstreamToolClient"/>. The request handlers are unchanged — they resolve this
/// instance through the seam. Caller identity comes from <see cref="ICallerKeyProvider"/> (the
/// <c>X-Darvoza-Key</c> header in production); an unknown or missing key resolves to no role, whose
/// allow-list is empty, so everything is denied.
/// </para>
/// <para>
/// <b>Enforcement only — no audit here.</b> Recording every allow/deny decision is A01-T4, a separate
/// decorator around this one.
/// </para>
/// </remarks>
public sealed class PolicyEnforcingToolClient(
    IUpstreamToolClient inner,
    Policy policy,
    ICallerKeyProvider callerKeys) : IUpstreamToolClient
{
    /// <summary>Returns only the tools the caller's role allows. Unknown/missing key → no tools.</summary>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct)
    {
        var allowed = policy.AllowlistFor(callerKeys.GetCallerKey());
        if (allowed.Count == 0)
            return [];   // deny-by-default: an unauthorized caller sees nothing (and we skip the upstream list)

        var tools = await inner.ListToolsAsync(ct);
        return [.. tools.Where(tool => allowed.Contains(tool.Name))];
    }

    /// <summary>
    /// Deny-by-default: a tool not in the caller-role's allow-list returns a clean policy-denial
    /// <see cref="CallToolResult"/> <b>without calling the inner client</b>. Allowed calls forward verbatim.
    /// </summary>
    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
    {
        if (!policy.IsAllowed(callerKeys.GetCallerKey(), callParams.Name))
            return ValueTask.FromResult(Denied(callParams.Name));

        return inner.CallToolAsync(callParams, ct);
    }

    // A clean, non-leaky denial: states the tool and that it is not permitted, without revealing the
    // caller's role or the rest of the allow-list.
    private static CallToolResult Denied(string toolName) => new()
    {
        IsError = true,
        Content = [new TextContentBlock
        {
            Text = $"Policy denied: tool '{toolName}' is not permitted for your caller key.",
        }],
    };
}
