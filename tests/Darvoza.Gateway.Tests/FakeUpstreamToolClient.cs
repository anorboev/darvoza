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

    /// <summary>Optional hook run inside <see cref="CallToolAsync"/> before returning — e.g. to advance a
    /// test clock so an outer decorator observes non-zero latency.</summary>
    public Action? BeforeReturn { get; set; }

    /// <summary>When true, <see cref="CallToolAsync"/> throws — simulating an upstream transport failure
    /// (as opposed to an upstream tool error, which is a returned <see cref="CallToolResult"/> with
    /// <see cref="CallToolResult.IsError"/>).</summary>
    public bool ThrowOnCall { get; set; }

    public Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct) => Task.FromResult(Tools);

    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
    {
        LastCallParams = callParams; // the call reached upstream...
        BeforeReturn?.Invoke();
        if (ThrowOnCall)
            throw new InvalidOperationException("upstream transport failure (test)"); // ...and the transport faulted
        return ValueTask.FromResult(CallResult);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
