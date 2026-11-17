#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Net;
using AnimaWindows.Core.AniSkip;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tests.Net;

namespace AnimaWindows.Core.Tests.AniSkip;

public class AniSkipRepositoryTests
{
    private const string Ok = """
        {"found":true,"results":[
        {"interval":{"start_time":5.5,"end_time":95.0},"skip_type":"op","episode_length":1400},
        {"interval":{"start_time":1300.0,"end_time":1390.0},"skip_type":"ed"}]}
        """;

    private static AniSkipRepository Repo(FakeHttpMessageHandler h) => new(new HttpClient(h), "https://aniskip.test");

    [Fact]
    public async Task ParsesAndCaches()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, Ok);
        var r = Repo(h);
        var t = await r.SkipTimesAsync(1, 2);
        Assert.NotNull(t);
        Assert.Equal(5.5, t.Opening!.StartSec);
        Assert.Equal(1390.0, t.Ending!.EndSec);
        var req = Assert.Single(h.Requests);
        Assert.Equal("https://aniskip.test/v1/skip-times/1/2?types=op&types=ed", req.Uri.ToString());
        Assert.NotNull(await r.SkipTimesAsync(1, 2));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task EdBeforeOpEndDropped()
    {
        const string body = """
            {"found":true,"results":[
            {"interval":{"start_time":10,"end_time":100},"skip_type":"op"},
            {"interval":{"start_time":50,"end_time":120},"skip_type":"ed"}]}
            """;
        var t = await Repo(new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, body)).SkipTimesAsync(1, 1);
        Assert.NotNull(t);
        Assert.Null(t.Ending);
        Assert.NotNull(t.Opening);
    }

    [Fact]
    public void InvalidOpDropped()
    {
        Assert.Null(AniSkipParsing.BuildSkipTimes([new SkipResult(50.0, 40.0, "op")]));
    }

    [Fact]
    public async Task FailuresReturnNull()
    {
        var h = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.NotFound, "{}")
            .Enqueue(HttpStatusCode.InternalServerError, "x")
            .Enqueue(HttpStatusCode.OK, "garbage")
            .Enqueue(HttpStatusCode.OK, """{"found":false}""");
        var r = Repo(h);
        Assert.Null(await r.SkipTimesAsync(1, 1));
        Assert.Null(await r.SkipTimesAsync(2, 1));
        Assert.Null(await r.SkipTimesAsync(3, 1));
        Assert.Null(await r.SkipTimesAsync(4, 1));
    }

    [Fact]
    public async Task NegativeResultsAreCachedButFailuresAreNot()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.NotFound).Enqueue(HttpStatusCode.InternalServerError).Enqueue(HttpStatusCode.OK, Ok);
        var r = Repo(h);
        Assert.Null(await r.SkipTimesAsync(1, 1));
        Assert.Null(await r.SkipTimesAsync(1, 1)); // cached 404
        Assert.Single(h.Requests);
        Assert.Null(await r.SkipTimesAsync(2, 1)); // 500, not cached
        Assert.NotNull(await r.SkipTimesAsync(2, 1));
    }

    [Fact]
    public async Task LruEvictsOldest()
    {
        var h = new FakeHttpMessageHandler();
        for (var i = 0; i < 10; i++) h.Enqueue(HttpStatusCode.OK, Ok);
        var r = new AniSkipRepository(new HttpClient(h), "https://aniskip.test", cacheSize: 2);
        await r.SkipTimesAsync(1, 1);
        await r.SkipTimesAsync(2, 1);
        await r.SkipTimesAsync(1, 1); // touch 1
        await r.SkipTimesAsync(3, 1); // evicts 2
        Assert.Equal(3, h.Requests.Count);
        await r.SkipTimesAsync(1, 1);
        Assert.Equal(3, h.Requests.Count);
        await r.SkipTimesAsync(2, 1);
        Assert.Equal(4, h.Requests.Count);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Repo(new FakeHttpMessageHandler()).SkipTimesAsync(1, 1, cts.Token));
    }
}

public class MalIdResolverTests
{
    private static MalIdResolver Resolver(FakeHttpMessageHandler h) => new(new HttpClient(h), "https://mal.test");

    [Fact]
    public async Task PicksExact()
    {
        const string body = """
            {"categories":[{"type":"anime","items":[
            {"id":1,"name":"Naruto Shippuden"},{"id":2,"name":"Naruto"}]}]}
            """;
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, body);
        Assert.Equal(2, await Resolver(h).ResolveTitleAsync("Naruto!"));
        Assert.Equal("https://mal.test/search/prefix.json?type=anime&keyword=Naruto", h.Requests[0].Uri.ToString());
    }

    [Fact]
    public async Task ContainsAndFailure()
    {
        var h = new FakeHttpMessageHandler()
            .Enqueue(HttpStatusCode.OK, """{"categories":[{"type":"anime","items":[{"id":7,"name":"Foo"},{"id":8,"name":"Bar Baz Season 2"}]}]}""")
            .Enqueue(HttpStatusCode.InternalServerError);
        var r = Resolver(h);
        Assert.Equal(8, await r.ResolveTitleAsync("Bar Baz"));
        Assert.Null(await r.ResolveTitleAsync("x"));
    }

    [Fact]
    public async Task UsesProvidedMalIdWithoutNetwork()
    {
        var h = new FakeHttpMessageHandler();
        var a = new Anime { Id = "a", ProviderId = "p", Title = "T", MalId = 42 };
        Assert.Equal(42, await Resolver(h).ResolveAsync(a));
        Assert.Empty(h.Requests);
    }
}
