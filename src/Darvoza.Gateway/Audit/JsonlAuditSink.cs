using System.Text;

namespace Darvoza.Gateway.Audit;

/// <summary>
/// Append-only JSONL audit sink (A01-T4). One line per record, newline-terminated, UTF-8. A single
/// <see cref="SemaphoreSlim"/> serializes writes so concurrent <c>CallToolAsync</c> calls never interleave
/// a partial line, and each record is flushed to the OS buffer before the write returns (durability over
/// throughput). Note this is a <see cref="FileStream.FlushAsync()"/> to the OS page cache, not an
/// <c>fsync</c> — a crash/power loss can still drop the most recent record; full sync durability is a
/// deployment-tier concern beyond this v1.
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
    private volatile bool _disposed;
    private int _disposeGuard; // 0 = not yet disposed; set once via Interlocked so DisposeAsync is idempotent

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
        // Idempotent: only the first call proceeds, so a second dispose can never double-release the gate
        // or fault. (IAsyncDisposable does not guarantee a single call.)
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0)
            return;

        // The gate is never disposed (see below), so this WaitAsync cannot fault — the matching Release in
        // the finally is therefore always balanced.
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
