using Microsoft.Extensions.Logging;

namespace Darvoza.Gateway.Tests;

/// <summary>
/// Captures log records so a spec can assert on their COUNT, not merely their presence — "exactly one
/// warning per missing tool" and "the resolved argv is logged exactly once" are both count claims, and a
/// contains-assertion would pass just as happily on a duplicate-per-role bug.
/// </summary>
internal sealed class RecordingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _records = [];

    public IReadOnlyList<(LogLevel Level, string Message)> Records => Snapshot();

    public IEnumerable<string> Warnings =>
        Snapshot().Where(r => r.Level == LogLevel.Warning).Select(r => r.Message);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // The host logs from many categories on many threads; the e2e factory shares one instance.
        lock (_records)
            _records.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Snapshot of the records so far — safe to enumerate while the host keeps logging.</summary>
    public IReadOnlyList<(LogLevel Level, string Message)> Snapshot()
    {
        lock (_records)
            return [.. _records];
    }
}

/// <summary>Feeds every host log category into one <see cref="RecordingLogger"/> (A01-T7 e2e).</summary>
internal sealed class RecordingLoggerProvider(RecordingLogger logger) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => logger;

    public void Dispose()
    {
    }
}
