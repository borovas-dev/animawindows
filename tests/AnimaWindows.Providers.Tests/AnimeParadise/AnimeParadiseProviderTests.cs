using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.AnimeParadise;
using AnimaWindows.Providers.Tests.Common;

namespace AnimaWindows.Providers.Tests.AnimeParadise;

public class AnimeParadiseProviderTests
{
    private const string Origin = "http://ap.test";
    private static readonly Anime Naruto = new() { Id = "naruto", ProviderId = "animeparadise", Title = "Naruto" };
    private const string EpisodeId = "a9d35391-5437-4b2a-9445-7066f14b15d5|OGCLrfH4Gdr4yGZg";

    private static string Res(string name) => EnglishFixtures.Read("AnimeParadise", name);

    private static HttpResponseMessage Dispatch(EnglishRecordedRequest r)
    {
        if (r.Uri.Host == "unreachable.test") throw new HttpRequestException("connection refused");
        return r.Path switch
        {
            "/search" => EnglishFakeHandler.Ok(Res("search.json")),
            "/anime/naruto" => EnglishFakeHandler.Ok(Res("details.json")),
            "/anime/OGCLrfH4Gdr4yGZg/episode" => EnglishFakeHandler.Ok(Res("episodes.json")),
            "/ep/a9d35391-5437-4b2a-9445-7066f14b15d5" => EnglishFakeHandler.Ok(Res("episode.json")),
            "/m3u8" => EnglishFakeHandler.Ok(Res("master.m3u8")),
            _ => EnglishFakeHandler.Status(HttpStatusCode.NotFound),
        };
    }

    private static (AnimeParadiseProvider Provider, EnglishFakeHandler Handler) Create(bool ass = false)
    {
        var handler = new EnglishFakeHandler(Dispatch);
        var config = new EnglishTestConfig(() => new ProviderRemoteConfig
        {
            BaseUrls = [Origin],
            Params = new Dictionary<string, string> { ["streamBase"] = Origin },
        });
        return (new AnimeParadiseProvider(new HttpClient(handler), config, ass), handler);
    }

    [Fact]
    public void Metadata()
    {
        var (p, _) = Create();
        Assert.Equal("animeparadise", p.Id);
        Assert.Equal(2, p.Priority);
        Assert.Equal("en", Assert.Single(p.ContentLanguages));
        Assert.False(p.GfwBlocked);
    }

    [Fact]
    public async Task Search()
    {
        var (p, h) = Create();
        var r = await p.SearchAsync("naruto shippuden", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Count);
        Assert.Equal("naruto-shippuden", r[0].Id);
        Assert.Equal(500, r[0].SubEpisodes);
        Assert.Equal(2007, r[0].Year);
        Assert.StartsWith("https://s4.anilist.co", r[0].CoverUrl);
        var q = h.Requests[0];
        Assert.Equal("POPULARITY", q.Query["sort"]);
        Assert.Equal("naruto shippuden", q.Query["q"]);
        Assert.Equal("1", q.Query["v"]);
        Assert.Equal("https://www.animeparadise.moe", q.Header("Origin"));
        Assert.Equal("https://www.animeparadise.moe/", q.Header("Referer"));
    }

    [Fact]
    public async Task DubIsEmpty()
    {
        var (p, h) = Create();
        Assert.Empty(await p.SearchAsync("naruto", TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProviderException>(() => p.EpisodesAsync(Naruto, TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Details()
    {
        var (p, _) = Create();
        var d = await p.DetailsAsync(Naruto, cancellationToken: TestContext.Current.CancellationToken);
        Assert.StartsWith("Naruto Uzumaki", d.Synopsis);
        Assert.Equal(2002, d.Year);
        Assert.Equal(20, d.MalId);
    }

    [Fact]
    public async Task Episodes()
    {
        var (p, h) = Create();
        var eps = await p.EpisodesAsync(Naruto, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "/anime/naruto", "/anime/OGCLrfH4Gdr4yGZg/episode" }, h.Requests.Select(r => r.Path).ToArray());
        Assert.Equal(new[] { "1", "2", "3" }, eps.Select(e => e.Number).ToArray());
        Assert.Equal(EpisodeId, eps[0].Id);
        Assert.Equal("Enter: Naruto Uzumaki!", eps[0].Title);
    }

    [Fact]
    public async Task Streams()
    {
        var (p, h) = Create();
        var s = await p.StreamsAsync(Naruto, new Episode(EpisodeId, "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        var epReq = h.Requests.First(r => r.Path.StartsWith("/ep/", StringComparison.Ordinal));
        Assert.Equal("OGCLrfH4Gdr4yGZg", epReq.Query["origin"]);
        var m3u8Req = h.Requests.First(r => r.Path == "/m3u8");
        Assert.StartsWith("Yi7ml55vdtSC1B9H6IvudeFcCyy3C0u_", m3u8Req.Query["url"]);
        Assert.StartsWith($"{Origin}/m3u8?url=Yi7ml55", s.MasterUrl);
        Assert.Equal(800, Assert.Single(s.Variants).Quality);
        Assert.Equal($"{Origin}/m3u8?url=VARIANTTOKEN", s.Variants[0].Url);
        Assert.Equal("https://www.animeparadise.moe", s.Headers["Origin"]);
        Assert.Equal("https://www.animeparadise.moe/", s.Headers["Referer"]);
        // intro 0..0 is "none"; outro maps to seconds
        Assert.Null(s.Intro);
        Assert.Equal(1280.0, s.Outro!.StartSec);
        Assert.Equal(1369.0, s.Outro!.EndSec);
        var t = Assert.Single(s.Subtitles);
        Assert.Equal("en", t.Language);
        Assert.True(t.IsDefault);
        Assert.StartsWith("https://stream.animeparadise.moe/captions?url=", t.Url);
    }

    [Fact]
    public async Task AssTracksOptIn()
    {
        var (p, _) = Create(ass: true);
        var subs = (await p.StreamsAsync(Naruto, new Episode(EpisodeId, "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken)).Subtitles;
        Assert.Equal(new[] { "en", "en", "es", "pt", "it" }.Order().ToArray(), subs.Select(t => t.Language ?? "").Order().ToArray());
        Assert.Contains("/captions", subs[0].Url); // vtt first
        Assert.Single(subs, t => t.IsDefault);
        Assert.Contains(subs, t => t.Url == $"{Origin}/stream/file/13KMJl5GaxxuyYzoqV-aASt7SiMGyqgqm");
    }

    [Fact]
    public async Task BaseUrlReadPerRequest()
    {
        var baseUrl = "http://unreachable.test";
        var handler = new EnglishFakeHandler(Dispatch);
        var p = new AnimeParadiseProvider(
            new HttpClient(handler),
            new EnglishTestConfig(() => new ProviderRemoteConfig { BaseUrls = [baseUrl] }));
        await Assert.ThrowsAsync<ProviderException>(() => p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        baseUrl = Origin;
        Assert.Equal(2, (await p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task HttpErrorIsProviderException()
    {
        var (p, _) = Create();
        // details is best-effort
        Assert.Equal(Naruto with { Id = "x" }, await p.DetailsAsync(Naruto with { Id = "x" }, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProviderException>(() => p.EpisodesAsync(Naruto with { Id = "x" }, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Hits the real site.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSearchEpisodesStreams()
    {
        var p = new AnimeParadiseProvider(new HttpClient(), new EnglishTestConfig(() => ProviderRemoteConfig.Default));
        var anime = (await p.SearchAsync("naruto", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken))[0];
        var eps = await p.EpisodesAsync(anime, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(eps);
        var s = await p.StreamsAsync(anime, eps[0], TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(s.Variants);
    }
}
