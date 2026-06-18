using Darvoza.Gateway.Upstream;
using Microsoft.AspNetCore.Http;

namespace Darvoza.Gateway.Tests;

// A01-T3 — the X-Darvoza-Key reader is the single ASP.NET coupling in the policy path. It must map a
// well-formed single header to the key, and EVERY ambiguous/absent case to null (→ deny-by-default).
public class HttpHeaderCallerKeyProviderTests
{
    private static HttpHeaderCallerKeyProvider For(Action<HttpContext>? configure)
    {
        var accessor = new HttpContextAccessor();
        if (configure is not null)
        {
            var ctx = new DefaultHttpContext();
            configure(ctx);
            accessor.HttpContext = ctx;
        }

        return new HttpHeaderCallerKeyProvider(accessor);
    }

    [Fact]
    public void Returns_the_key_for_a_single_header_value()
    {
        var provider = For(ctx => ctx.Request.Headers[HttpHeaderCallerKeyProvider.HeaderName] = "secret-key");

        Assert.Equal("secret-key", provider.GetCallerKey());
    }

    [Fact]
    public void Returns_null_when_the_header_is_absent()
    {
        var provider = For(_ => { });

        Assert.Null(provider.GetCallerKey());
    }

    [Fact]
    public void Returns_null_when_there_is_no_http_context()
    {
        var provider = For(configure: null);

        Assert.Null(provider.GetCallerKey());
    }

    [Fact]
    public void Returns_null_for_a_whitespace_only_header()
    {
        var provider = For(ctx => ctx.Request.Headers[HttpHeaderCallerKeyProvider.HeaderName] = "   ");

        Assert.Null(provider.GetCallerKey());
    }

    [Fact]
    public void Returns_null_for_an_ambiguous_multi_value_header()
    {
        // A proxy or misconfigured client could send the header twice. Joining the values would never
        // match a real key (and could silently lock out a legitimate caller) — so deny instead.
        var provider = For(ctx =>
            ctx.Request.Headers[HttpHeaderCallerKeyProvider.HeaderName] = new[] { "key-one", "key-two" });

        Assert.Null(provider.GetCallerKey());
    }
}
