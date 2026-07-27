using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Darvoza.Gateway.Upstream;

/// <summary>
/// The single concrete <see cref="IUpstreamToolClient"/>: a transparent pass-through to whatever MCP
/// server the policy file configures, over an MCP client session. It OWNS that session and disposes it
/// on shutdown (A01-T2b). Since A01-T7 / ADR-0004 that upstream is operator-configurable — the official
/// Azure DevOps server is the default profile, not a coupling this class knows about.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle: registered as a container-constructed singleton so the DI container disposes it on host
/// shutdown — which disposes the inner <see cref="McpClient"/>, tears down its stdio transport, and
/// terminates the spawned <c>npx</c> child (fixing the spike's leak, where a pre-created instance was
/// never disposed). The connection is established by <see cref="UpstreamConnectionInitializer"/> in
/// <c>StartAsync</c> (fail-fast).
/// </para>
/// <para>
/// <see cref="McpClient"/> has a large abstract surface and cannot be faked, so the owned connection
/// is exposed as an <see cref="IAsyncDisposable"/> collaborator that tests inject directly to verify
/// disposal. The list/call pass-through over a live client is covered by the spike's live acceptance
/// tests, and by <c>PassthroughToolHandlers</c> unit tests at the <see cref="IUpstreamToolClient"/> seam.
/// </para>
/// </remarks>
public sealed class McpUpstreamToolClient : IUpstreamToolClient, IAsyncDisposable
{
    private readonly Func<CancellationToken, Task<McpClient>>? _connect;
    private readonly SemaphoreSlim _connectGate = new(1, 1);

    // volatile so the lock-free fast-path read in ConnectAsync isn't a formally-stale load on
    // weak-memory architectures; the in-gate check remains authoritative.
    private volatile McpClient? _client;
    private IAsyncDisposable? _ownedConnection;
    private volatile bool _disposed;

    /// <summary>Production: connect to the upstream over the given transport when <see cref="ConnectAsync"/> runs.</summary>
    public McpUpstreamToolClient(IClientTransport transport)
        => _connect = ct => McpClient.CreateAsync(transport, cancellationToken: ct);

    /// <summary>Test seam: inject the owned connection directly (no live client) to exercise disposal.</summary>
    internal McpUpstreamToolClient(IAsyncDisposable ownedConnection)
        => _ownedConnection = ownedConnection;

    /// <summary>
    /// Establishes the upstream MCP session (initialize handshake). Idempotent; called once at startup.
    /// A failure here fails host startup (fail-fast) rather than surfacing on the first request.
    /// </summary>
    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_connect is null || _client is not null)
            return;

        await _connectGate.WaitAsync(ct);
        try
        {
            if (_disposed || _client is not null)
                return;
            _client = await _connect(ct);
            _ownedConnection = _client;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken ct)
    {
        var client = Connected();
        var tools = await client.ListToolsAsync(cancellationToken: ct);
        return [.. tools.Select(t => t.ProtocolTool)];
    }

    public ValueTask<CallToolResult> CallToolAsync(CallToolRequestParams callParams, CancellationToken ct)
        => Connected().CallToolAsync(callParams, ct);

    public async ValueTask DisposeAsync()
    {
        // Take the gate so disposal can never race an in-flight ConnectAsync, and mark disposed so a
        // late connect (after teardown) is a no-op rather than re-creating + leaking a client.
        IAsyncDisposable? connection;
        await _connectGate.WaitAsync();
        try
        {
            _disposed = true;
            connection = _ownedConnection;
            _ownedConnection = null;
            _client = null;
        }
        finally
        {
            _connectGate.Release();
        }

        if (connection is not null)
            await connection.DisposeAsync();

        // _connectGate is intentionally NOT disposed: its AvailableWaitHandle is never accessed, so
        // there is nothing to release, and disposing it here could throw inside a concurrent WaitAsync.
    }

    private McpClient Connected() =>
        _client ?? throw new InvalidOperationException(
            "Upstream MCP client is not connected. ConnectAsync must complete before tool calls.");
}
