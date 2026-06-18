namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Out-of-band channel carrying the per-call policy decision from the inner policy decorator up to the
/// outer audit decorator. The seam method signature (<see cref="IUpstreamToolClient.CallToolAsync"/>) is
/// fixed and the decision cannot ride the return value, so it travels through this ambient context.
/// </summary>
/// <remarks>
/// The audit decorator calls <see cref="Begin"/> with a fresh <see cref="CallDecisionBox"/> before
/// invoking the inner client (the box flows DOWN to the policy stage), the policy stage reads
/// <see cref="Current"/> and records its decision into the box, and the audit decorator calls
/// <see cref="End"/> when the call returns. Implementations must be safe for concurrent calls
/// (each in-flight call sees only its own box).
/// <para>
/// <b>Not re-entrant:</b> a single async flow must not nest <see cref="Begin"/>/<see cref="End"/> scopes —
/// <see cref="Begin"/> overwrites the current slot without saving the previous box. The decorator chain
/// calls each at most once per tool call, so this holds today; a future decorator that re-enters
/// <c>CallToolAsync</c> on the same flow would need a save/restore stack here.
/// </para>
/// </remarks>
public interface ICallDecisionContext
{
    /// <summary>The decision box for the in-flight call on this async flow, or <c>null</c> outside a call.</summary>
    CallDecisionBox? Current { get; }

    /// <summary>Begins a call scope, installing <paramref name="box"/> as <see cref="Current"/> for this flow.</summary>
    void Begin(CallDecisionBox box);

    /// <summary>Ends the call scope, clearing <see cref="Current"/> for this flow.</summary>
    void End();
}
