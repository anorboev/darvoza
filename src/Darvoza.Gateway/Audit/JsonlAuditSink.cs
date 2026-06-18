using System.Text;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// Append-only JSONL audit sink (A01-T4). One line per record, newline-terminated, UTF-8. A single
/// <see cref="SemaphoreSlim"/> serializes writes so concurrent <c>CallToolAsync</c> calls never interleave
/// a partial line, and each record is flushed to the OS before the write returns (durability over
/// throughput — the fail-closed guarantee needs the bytes on disk, not in a buffer).
/// </summary>
/// <remarks>
/// Registered as a container-owned singleton (G-09) implementing <see cref="IAsyncDisposable"/>, so the DI
/// container flushes and closes the file on host shutdown — mirroring the upstream client's lifecycle.
/// </remarks>
public sealed class JsonlAuditSink : IAuditSink, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _path;
    private FileStream? _stream;
    private bool _disposed;

    /// <param name="path">The audit file path. Its directory is created if it does not exist.</param>
    public JsonlAuditSink(string path)
    {
        _path = path;
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    public async ValueTask WriteAsync(string jsonLine, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            // Open lazily on first write and keep the handle for the process lifetime. FileShare.Read lets
            // operators tail the trail live; FileMode.Append keeps every existing line.
            _stream ??= new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);

            var bytes = Encoding.UTF8.GetBytes(jsonLine + '\n');
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            if (_stream is not null)
            {
                await _stream.FlushAsync();
                await _stream.DisposeAsync();
                _stream = null;
            }
        }
        finally
        {
            // Intentionally NOT disposing _gate: a late WriteAsync racing dispose must observe _disposed
            // and throw a clean ObjectDisposedException, not a semaphore-disposed error (same posture as
            // McpUpstreamToolClient's connect gate).
            _gate.Release();
        }
    }
}
