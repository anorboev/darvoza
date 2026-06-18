namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Supplies the current request's caller key — the per-caller secret the policy decorator resolves to a
/// role (A01-T3). One-method seam so the policy decorator stays transport-agnostic and singleton-safe:
/// the production implementation reads the <c>X-Darvoza-Key</c> header via <see cref="IHttpContextAccessor"/>,
/// while tests inject a trivial fake. Isolating the HTTP dependency here keeps the decorator and all its
/// contract tests free of ASP.NET.
/// </summary>
public interface ICallerKeyProvider
{
    /// <summary>The current caller's key, or <c>null</c> when absent (→ deny-by-default).</summary>
    string? GetCallerKey();
}
