namespace Darvoza.Gateway.Tests;

/// <summary>
/// A hand-rolled <see cref="TimeProvider"/> test double: <see cref="GetUtcNow"/> and the monotonic
/// timestamp both advance only when the test calls <see cref="Advance"/>, giving deterministic audit
/// timestamps and latency without a real clock (and without a new test dependency).
/// </summary>
/// <remarks>
/// <see cref="TimestampFrequency"/> is set to <see cref="TimeSpan.TicksPerSecond"/> and
/// <see cref="GetTimestamp"/> counts in ticks, so the base <see cref="TimeProvider.GetElapsedTime(long)"/>
/// returns exactly the elapsed <see cref="TimeSpan"/>.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _utcNow = start;
    private long _timestamp;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan by)
    {
        _utcNow = _utcNow.Add(by);
        _timestamp += by.Ticks;
    }
}
