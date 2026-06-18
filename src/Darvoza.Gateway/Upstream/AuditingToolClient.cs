using Darvoza.Gateway.Audit;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The A01-T4 audit stage: the OUTERMOST <see cref="IUpstreamToolClient"/> decorator (ADR-0002 /
/// ADR-0003), wrapping the T3 <see cref="PolicyEnforcingToolClient"/>. It records <b>exactly one</b>
/// JSONL audit record for every <c>CallToolAsync</c> — allowed→upstream-ok, allowed→upstream-error, and
/// policy-denied — the headline 100%-coverage NFR.
/// </summary>
/// <remarks>
/// <para>
/// It does not re-decide anything: the inner policy decorator publishes its decision (allow/deny, reason,
/// role, caller fingerprint) into a <see cref="CallDecisionBox"/> this decorator created and flowed down
/// via <see cref="ICallDecisionContext"/>. This decorator reads that box plus the returned
/// <see cref="CallToolResult"/> to classify the outcome.
/// </para>
/// <para>
/// <b>Fail-closed:</b> if the audit record cannot be written, the call does not return a successful result
/// — a non-leaky error result is returned instead, so no unaudited success is ever served (an allowed
/// upstream side-effect may already have committed; that trade-off is documented in ADR-0003).
/// </para>
/// </remarks>
public sealed class AuditingToolClient(
    IUpstreamToolClient inner,
    IAuditSink sink,
    ICallDecisionContext decisions,
    TimeProvider time) : IUpstreamToolClient
{
    /// <summary>Listing tools is not a tool <i>call</i>; it is forwarded unaudited (A01-T4 scope = CallToolAsync).</summary>
    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct) => inner.ListToolsAsync(ct);

    public async ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
    {
        var box = new CallDecisionBox();
        decisions.Begin(box);
        var startedAt = time.GetTimestamp();
        var timestamp = time.GetUtcNow();
        try
        {
            CallToolResult result;
            try
            {
                result = await inner.CallToolAsync(callParams, ct);
            }
            catch
            {
                // The inner call threw (e.g. the upstream transport failed). Still record — 100% coverage —
                // then let the failure propagate. The write is best-effort here: the call is already
                // failing, so a write failure cannot leak an unaudited success.
                try
                {
                    var faulted = BuildRecord(box, callParams, timestamp, startedAt, upstreamStatus: "error");
                    await sink.WriteAsync(faulted.ToJsonLine(), ct);
                }
                catch
                {
                    // swallow — the original exception below is the meaningful failure
                }
                throw;
            }

            var upstreamStatus = box.Decision == CallDecisionKind.Deny
                ? null                                          // policy denial never reached upstream
                : result.IsError == true ? "error" : "ok";

            var record = BuildRecord(box, callParams, timestamp, startedAt, upstreamStatus);
            try
            {
                await sink.WriteAsync(record.ToJsonLine(), ct);
            }
            catch
            {
                // Fail-closed: refuse to hand back a result we could not audit.
                return AuditWriteFailed();
            }

            return result;
        }
        finally
        {
            decisions.End();
        }
    }

    private AuditRecord BuildRecord(
        CallDecisionBox box,
        CallToolRequestParams callParams,
        DateTimeOffset timestamp,
        long startedAt,
        string? upstreamStatus) => new()
        {
            Ts = timestamp.ToUniversalTime().ToString("O"),
            Tool = callParams.Name,
            Caller = new AuditCaller { Role = box.Role, KeyFingerprint = box.CallerFingerprint },
            Decision = box.Decision == CallDecisionKind.Deny ? "deny" : "allow",
            Reason = box.Reason,
            Args = AuditArgs.From(callParams.Arguments),
            Upstream = upstreamStatus is null ? null : new AuditUpstream { Status = upstreamStatus },
            LatencyMs = (long)time.GetElapsedTime(startedAt).TotalMilliseconds,
        };

    // A clean, non-leaky result for an audit-write failure: it states only that the call could not be
    // recorded, never why or what the policy decision was.
    private static CallToolResult AuditWriteFailed() => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = "Audit unavailable: the call was refused because it could not be recorded." }],
    };
}
