using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.Tests.Common;

public class HostHttpTests
{
    private static HostHttp Http(EnglishFakeHandler handler) => new(new HttpClient(handler));

    [Fact]
    public async Task GetSendsUserAgentAcceptLanguageAndReferer()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok("hello"));
        var text = await Http(handler).GetStringAsync("https://a.test/x", referer: "https://ref.test/", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("hello", text);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(NetworkConstants.AniCliUserAgent, req.Header("User-Agent"));
        Assert.Equal("en-US,en;q=0.9", req.Header("Accept-Language"));
        Assert.Equal("https://ref.test/", req.Header("Referer"));
    }

    [Fact]
    public async Task ExtraHeadersOverrideDefaults()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok(""));
        var headers = new Dictionary<string, string> { ["User-Agent"] = "custom", ["Origin"] = "https://o.test" };
        await Http(handler).GetStringAsync("https://a.test/x", headers: headers, cancellationToken: TestContext.Current.CancellationToken);
        var req = handler.Requests[0];
        Assert.Equal("custom", req.Header("User-Agent"));
        Assert.Equal("https://o.test", req.Header("Origin"));
    }

    [Fact]
    public async Task PostJsonSendsBodyAndHeaders()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok("{}"));
        await Http(handler).PostJsonAsync("https://a.test/p", """{"q":1}""", "https://a.test/", "https://a.test", cancellationToken: TestContext.Current.CancellationToken);
        var req = handler.Requests[0];
        Assert.Equal("POST", req.Method);
        Assert.Equal("""{"q":1}""", req.Body);
        Assert.Equal("https://a.test", req.Header("Origin"));
        Assert.Equal("https://a.test/", req.Header("Referer"));
    }

    [Fact]
    public async Task PostFormEncodesFields()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok("ok"));
        await Http(handler).PostFormAsync("https://a.test/p", [new("a", "1 2"), new("b", "x&y")], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("a=1+2&b=x%26y", handler.Requests[0].Body);
    }

    [Fact]
    public async Task CloudflareChallengeByBody()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Status(HttpStatusCode.Forbidden, "<title>Just a moment...</title>"));
        var e = await Assert.ThrowsAsync<CloudflareChallengeException>(() => Http(handler).GetStringAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("https://a.test/x", e.Url);
    }

    [Fact]
    public async Task CloudflareChallengeByHeader503()
    {
        var handler = new EnglishFakeHandler(_ =>
        {
            var r = EnglishFakeHandler.Status(HttpStatusCode.ServiceUnavailable, "x");
            r.Headers.Add("cf-mitigated", "challenge");
            return r;
        });
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => Http(handler).PostJsonAsync("https://a.test/x", "{}", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Plain403IsProviderException()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Status(HttpStatusCode.Forbidden, "forbidden"));
        var e = await Assert.ThrowsAsync<ProviderException>(() => Http(handler).GetStringAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
        var status = Assert.IsType<HttpStatusException>(e.InnerException);
        Assert.Equal(403, status.Code);
    }

    [Fact]
    public async Task JustAMomentOn200IsIgnored()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok("Just a moment"));
        Assert.Equal("Just a moment", await Http(handler).GetStringAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetCheckedKeepsStatusCode()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Status(HttpStatusCode.NotFound));
        var e = await Assert.ThrowsAsync<HttpStatusException>(() => Http(handler).GetCheckedAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(404, e.Code);
    }

    [Fact]
    public async Task ConnectionFailureIsProviderException()
    {
        var handler = new EnglishFakeHandler(_ => throw new HttpRequestException("refused"));
        var e = await Assert.ThrowsAsync<ProviderException>(() => Http(handler).GetStringAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
        Assert.IsType<HttpRequestException>(e.InnerException);
        Assert.True(e.IsMirrorRetryable());
    }

    [Fact]
    public async Task RedirectTargetFallbackReportsFinalUrl()
    {
        var handler = new EnglishFakeHandler(_ => EnglishFakeHandler.Ok("x"));
        Assert.Null(await Http(handler).RedirectTargetAsync("https://a.test/x", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MirrorsMoveOnAfterConnectionFailureAnd5xx()
    {
        var tried = new List<string>();
        var r = await Mirrors.WithMirrorsAsync<string>(["a", "b", "c"], b =>
        {
            tried.Add(b);
            return b switch
            {
                "a" => throw new ProviderException("x", new IOException()),
                "b" => throw new ProviderException("x", new HttpStatusException(503, "u")),
                _ => Task.FromResult("ok"),
            };
        });
        Assert.Equal("ok", r);
        Assert.Equal(new[] { "a", "b", "c" }, tried);
    }

    [Fact]
    public async Task MirrorsDoNotRetryClientErrors()
    {
        var tried = new List<string>();
        await Assert.ThrowsAsync<ProviderException>(() => Mirrors.WithMirrorsAsync<string>(["a", "b"], b =>
        {
            tried.Add(b);
            throw new ProviderException("x", new HttpStatusException(404, "u"));
        }));
        Assert.Equal(new[] { "a" }, tried);
    }

    [Fact]
    public async Task MirrorsWithNoBaseThrows()
    {
        await Assert.ThrowsAsync<ProviderException>(() => Mirrors.WithMirrorsAsync<string>([], _ => Task.FromResult("x")));
    }
}
