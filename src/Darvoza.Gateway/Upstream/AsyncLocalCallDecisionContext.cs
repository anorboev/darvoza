namespace Darvoza.Gateway.Upstream;

/// <summary>
/// <see cref="System.Threading.AsyncLocal{T}"/>-backed <see cref="ICallDecisionContext"/>. Stateless apart
/// from the per-async-flow slot, so it is safe to register as a singleton under the singleton decorators
/// (G-09 #1 — no captive scoped dependency) and isolates concurrent calls automatically.
/// </summary>
public sealed class AsyncLocalCallDecisionContext : ICallDecisionContext
{
    private readonly AsyncLocal<CallDecisionBox?> _current = new();

    public CallDecisionBox? Current => _current.Value;

    public void Begin(CallDecisionBox box) => _current.Value = box;

    public void End() => _current.Value = null;
}
