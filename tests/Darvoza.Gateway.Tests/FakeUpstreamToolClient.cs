using Darvoza.Gateway.Upstream;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Tests;

/// <summary>
/// In-memory <see cref="IUpstreamToolClient"/> for unit tests — lets the gateway's pass-through and
/// lifecycle be exercised without spawning <c>npx</c> or reaching live Azure DevOps.
/// </summary>
public sealed class FakeUpstreamToolClient : IUpstreamToolClient, IAsyncDisposable
{
    /// <summary>Tools that <see cref="ListToolsAsync"/> returns.</summary>
    public IReadOnlyList<Tool> Tools { get; set; } = [];

    /// <summary>Result that <see cref="CallToolAsync"/> returns.</summary>
    public CallToolResult CallResult { get; set; } = new() { Content = [] };

    /// <summary>The exact params object handed to the most recent <see cref="CallToolAsync"/> call.</summary>
    public CallToolRequestParams? LastCallParams { get; private set; }

    /// <summary>Set once <see cref="DisposeAsync"/> runs.</summary>
    public bool Disposed { get; private set; }

    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct) => Task.FromResult(Tools);

    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
    {
        LastCallParams = callParams;
        return ValueTask.FromResult(CallResult);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
