using Darvoza.Gateway.Audit;
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
/// Registered on the <see cref="IUpstreamToolClient"/> decorator seam (ADR-0002,
/// <c>docs/adr/ADR-0002-tool-pipeline-decorator-seam.md</c>), wrapping the concrete
/// <see cref="McpUpstreamToolClient"/> and, as of A01-T4, itself wrapped by the audit decorator. The
/// request handlers are unchanged — they resolve the outermost decorator through the seam. Caller identity
/// comes from <see cref="ICallerKeyProvider"/> (the
/// <c>X-Darvoza-Key</c> header in production); an unknown or missing key resolves to no role, whose
/// allow-list is empty, so everything is denied.
/// </para>
/// <para>
/// <b>Enforcement only — no audit here.</b> Recording every allow/deny decision is A01-T4, a separate
/// decorator around this one. This stage only <i>publishes</i> the decision it already made (decision,
/// reason, role, caller fingerprint) into the per-call <see cref="CallDecisionBox"/> the outer audit
/// decorator flowed down via <see cref="ICallDecisionContext"/> — it does not write any record itself, and
/// it adds no policy logic. When no audit decorator is present, <see cref="ICallDecisionContext.Current"/>
/// is <c>null</c> and the publish is a no-op.
/// </para>
/// </remarks>
public sealed class PolicyEnforcingToolClient(
    IUpstreamToolClient inner,
    Policy policy,
    ICallerKeyProvider callerKeys,
    ICallDecisionContext decisions) : IUpstreamToolClient
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
        var callerKey = callerKeys.GetCallerKey();
        var role = policy.RoleForKey(callerKey);
        var fingerprint = CallerFingerprint.Of(callerKey);

        if (!policy.IsAllowed(callerKey, callParams.Name))
        {
            var reason = $"tool '{callParams.Name}' is not permitted";
            decisions.Current?.RecordDeny(reason, role, fingerprint);
            return ValueTask.FromResult(Denied(callParams.Name));
        }

        decisions.Current?.RecordAllow(role, fingerprint);
        return inner.CallToolAsync(callParams, ct);
    }

    // A clean, non-leaky denial: states the tool and that it is not permitted, without revealing the
    // caller's role, the rest of the allow-list, or the existence of key-based auth.
    private static CallToolResult Denied(string toolName) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = $"Policy denied: tool '{toolName}' is not permitted." }],
    };
}
