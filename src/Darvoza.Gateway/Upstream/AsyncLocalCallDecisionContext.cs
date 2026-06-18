namespace Darvoza.Gateway.Upstream;

/// <summary>
/// <see cref="System.Threading.AsyncLocal{T}"/>-backed <see cref="ICallDecisionContext"/>. Stateless apart
/// from the per-async-flow slot, so it is safe to register as a singleton under the singleton decorators
/// (G-09 #1 — no captive scoped dependency) and isolates concurrent calls automatically.
/// </summary>
/// <remarks>
/// <b>Invariant:</b> the slot type (<see cref="CallDecisionBox"/>) MUST remain a reference type. The whole
/// design relies on the inner policy stage mutating the <i>same heap object</i> the outer audit stage holds
/// — a callee's write to an <see cref="System.Threading.AsyncLocal{T}"/> does not propagate back up. If the
/// box were ever made a struct, the <c>AsyncLocal&lt;CallDecisionBox?&gt;</c> would copy by value and the
/// audit stage would silently observe an unmodified box.
/// </remarks>
public sealed class AsyncLocalCallDecisionContext : ICallDecisionContext
{
    private readonly AsyncLocal<CallDecisionBox?> _current = new();

    public CallDecisionBox? Current => _current.Value;

    public void Begin(CallDecisionBox box) => _current.Value = box;

    public void End() => _current.Value = null;
}
