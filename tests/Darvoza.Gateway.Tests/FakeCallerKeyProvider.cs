using Darvoza.Gateway.Upstream;

namespace Darvoza.Gateway.Tests;

/// <summary>Test double for <see cref="ICallerKeyProvider"/> — returns a settable caller key.</summary>
internal sealed class FakeCallerKeyProvider : ICallerKeyProvider
{
    public string? Key { get; set; }

    public string? GetCallerKey() => Key;
}
