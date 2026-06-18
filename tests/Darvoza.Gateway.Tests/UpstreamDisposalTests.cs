using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

// A01-T2b — the spike registered a pre-created McpClient as a singleton and never disposed it, so the
// upstream npx child leaked on shutdown. The real wrapper OWNS its upstream connection and must
// dispose it. (Container-driven disposal of the singleton on host shutdown is .NET DI behavior; this
// spec pins the wrapper's own contract: DisposeAsync disposes the connection it owns.)
public class UpstreamDisposalTests
{
    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task DisposeAsync_disposes_the_owned_upstream_connection()
    {
        var connection = new TrackingAsyncDisposable();
        var client = new McpUpstreamToolClient(connection); // test seam: inject the owned connection

        await client.DisposeAsync();

        Assert.True(connection.Disposed);
    }

    [Fact]
    public async Task DisposeAsync_is_safe_to_call_more_than_once()
    {
        var connection = new TrackingAsyncDisposable();
        var client = new McpUpstreamToolClient(connection);

        await client.DisposeAsync();
        await client.DisposeAsync(); // idempotent — no throw

        Assert.True(connection.Disposed);
    }
}
