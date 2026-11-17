#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;

namespace AnimaWindows.Core.Tests.Net;

public class CloudflareHandlerTests
{
    private sealed class FakeSolver(bool result) : ICloudflareSolver
    {
        public int Calls;
        public Func<Task>? OnSolve;
        public event EventHandler? Solved;

        public async Task<bool> SolveAsync(Uri url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Calls);
            if (OnSolve is not null) await OnSolve();
            if (result) Solved?.Invoke(this, EventArgs.Empty);
            return result;
        }
    }

    private static HttpClient Client(FakeHttpMessageHandler inner, ICloudflareSolver? solver) =>
        new(new CloudflareHandler(solver, inner));

    private const string Url = "https://site.example/x";

    [Fact]
    public async Task PassesThroughOk()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "hello");
        using var c = Client(h, null);
        Assert.Equal("hello", await c.GetStringAsync(Url));
    }

    [Fact]
    public async Task DetectsJustAMoment403()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Forbidden, "<title>Just a moment...</title>");
        using var c = Client(h, null);
        var ex = await Assert.ThrowsAsync<CloudflareChallengeException>(() => c.GetAsync(Url));
        Assert.Equal(Url, ex.Url);
    }

    [Fact]
    public async Task DetectsMitigatedHeader503()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.ServiceUnavailable, "x", ("cf-mitigated", "challenge"));
        using var c = Client(h, null);
        var ex = await Assert.ThrowsAsync<CloudflareChallengeException>(() => c.GetAsync(Url));
        Assert.Contains(Url, ex.Message);
    }

    [Fact]
    public async Task Plain403IsNotChallenge()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Forbidden, "forbidden");
        using var c = Client(h, new FakeSolver(true));
        using var r = await c.GetAsync(Url);
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task JustAMomentOn200IsIgnored()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "Just a moment");
        using var c = Client(h, null);
        using var r = await c.GetAsync(Url);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }

    [Fact]
    public async Task SolvesAndRetriesOnce()
    {
        var h = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.Forbidden, "Just a moment")
            .Enqueue(HttpStatusCode.OK, "real");
        var solver = new FakeSolver(true);
        using var c = Client(h, solver);
        Assert.Equal("real", await c.GetStringAsync(Url));
        Assert.Equal(1, solver.Calls);
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task ThrowsWhenSolverFailsOrRetryStillBlocked()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.Forbidden, "Just a moment");
        using var c1 = Client(h, new FakeSolver(false));
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => c1.GetAsync(Url));

        var h2 = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.Forbidden, "Just a moment")
            .Enqueue(HttpStatusCode.Forbidden, "Just a moment");
        var solver = new FakeSolver(true);
        using var c2 = Client(h2, solver);
        await Assert.ThrowsAsync<CloudflareChallengeException>(() => c2.GetAsync(Url));
        Assert.Equal(1, solver.Calls);
        Assert.Equal(2, h2.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentChallengesSolveOnce()
    {
        var gate = new TaskCompletionSource();
        var h = new FakeHttpMessageHandler();
        for (var i = 0; i < 2; i++) h.Enqueue(HttpStatusCode.Forbidden, "Just a moment");
        for (var i = 0; i < 2; i++) h.Enqueue(HttpStatusCode.OK, "ok");
        var solver = new FakeSolver(true) { OnSolve = () => gate.Task };
        using var c = Client(h, solver);
        var a = c.GetStringAsync(Url + "1");
        var b = c.GetStringAsync(Url + "2");
        await Task.Delay(100);
        gate.SetResult();
        Assert.Equal(new[] { "ok", "ok" }, await Task.WhenAll(a, b));
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public void FactoryBuildsClientWithUserAgent()
    {
        using var client = AnimaHttpClientFactory.Create(new CookieContainer());
        Assert.Equal(TimeSpan.FromSeconds(15), client.Timeout);
        Assert.Contains("Chrome/", client.DefaultRequestHeaders.UserAgent.ToString());
    }
}
