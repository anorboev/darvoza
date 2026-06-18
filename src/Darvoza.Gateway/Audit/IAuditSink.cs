namespace Darvoza.Gateway.Audit;

/// <summary>
/// The durable destination for audit lines. Deliberately dumb: it appends one already-serialized JSONL
/// line. The audit decorator owns the record schema and serialization; the sink owns durability and
/// concurrency. A write that fails must throw — the caller (the audit decorator) is fail-closed and
/// relies on the exception to refuse an unaudited success.
/// </summary>
public interface IAuditSink
{
    /// <summary>Durably appends one audit line. Throws if the line cannot be written.</summary>
    ValueTask WriteAsync(string jsonLine, CancellationToken ct);
}
