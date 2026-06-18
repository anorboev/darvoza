namespace Darvoza.Gateway.Upstream;

/// <summary>
/// Production <see cref="ICallerKeyProvider"/>: reads the caller key from the <c>X-Darvoza-Key</c> request
/// header via <see cref="IHttpContextAccessor"/> (A01-T3). This is the single class that couples policy
/// enforcement to ASP.NET — if a future transport change means <see cref="IHttpContextAccessor.HttpContext"/>
/// is not populated where the decorator runs, only this class changes; the decorator, the policy, and all
/// their tests are untouched.
/// </summary>
public sealed class HttpHeaderCallerKeyProvider(IHttpContextAccessor httpContextAccessor) : ICallerKeyProvider
{
    /// <summary>The header carrying the per-caller secret key.</summary>
    public const string HeaderName = "X-Darvoza-Key";

    /// <inheritdoc />
    public string? GetCallerKey()
    {
        var value = httpContextAccessor.HttpContext?.Request.Headers[HeaderName].ToString();
        return string.IsNullOrEmpty(value) ? null : value;
    }
}
