using Darvoza.Gateway.Audit;

namespace Darvoza.Gateway.Tests;

/// <summary>
/// In-memory <see cref="IAuditSink"/> for unit tests: captures the exact JSONL lines the audit decorator
/// would write (so redaction can be asserted against the real bytes), and can simulate a write failure to
/// exercise the fail-closed path.
/// </summary>
internal sealed class FakeAuditSink : IAuditSink
{
    /// <summary>Every line handed to <see cref="WriteAsync"/>, in order.</summary>
    public List<string> Lines { get; } = [];

    /// <summary>When true, <see cref="WriteAsync"/> throws — simulating an unwritable audit trail.</summary>
    public bool ThrowOnWrite { get; set; }

    public ValueTask WriteAsync(string jsonLine, CancellationToken ct)
    {
        if (ThrowOnWrite)
            throw new IOException("audit sink unavailable (test)");

        Lines.Add(jsonLine);
        return ValueTask.CompletedTask;
    }
}
