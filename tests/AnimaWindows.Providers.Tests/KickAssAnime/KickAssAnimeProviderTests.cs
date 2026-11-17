using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.KickAssAnime;
using AnimaWindows.Providers.Tests.Common;

namespace AnimaWindows.Providers.Tests.KickAssAnime;

public class KickAssAnimeProviderTests
{
    private const string Srv = "http://kaa.test";
    private Func<string, string> _masterBody = m => m;

    private static string Res(string name) => EnglishFixtures.Read("KickAssAnime", name);
    private static readonly Anime Show = new() { Id = "sousou-no-frieren-2d15", ProviderId = "kickassanime", Title = "Frieren" };

    private HttpResponseMessage Dispatch(EnglishRecordedRequest r)
    {
        if (r.Uri.Host == "unreachable.test") throw new HttpRequestException("connection refused");
        var path = r.Path;
        var q = r.Uri.Query;
        HttpResponseMessage Ok(string body) => EnglishFakeHandler.Ok(body.Replace("{{SRV}}", Srv));
        if (path == "/api/fsearch") return Ok(Res("search.json"));
        if (path.EndsWith("/episodes", StringComparison.Ordinal) && q.Contains("lang=en-US", StringComparison.Ordinal))
        {
            return Ok(Res(q.Contains("page=2", StringComparison.Ordinal) ? "episodes_en_p2.json" : "episodes_en_p1.json"));
        }
        if (path.EndsWith("/episodes", StringComparison.Ordinal) && q.Contains("lang=ja-JP", StringComparison.Ordinal)) return Ok(Res("episodes_ja.json"));
        if (path.EndsWith("/episode/ep-1-aa83b7", StringComparison.Ordinal)) return Ok(Res("episode_en.json"));
        if (path.EndsWith("/episode/ep-1-f897b3", StringComparison.Ordinal)) return Ok(Res("episode_ja.json"));
        if (path == "/cat-player/player") return Ok(Res("player.html"));
        if (path.EndsWith("/master.m3u8", StringComparison.Ordinal))
        {
            return r.Header("Origin") == Srv
                ? Ok(_masterBody(Res("master.m3u8")))
                : EnglishFakeHandler.Status(HttpStatusCode.Forbidden);
        }
        return EnglishFakeHandler.Status(HttpStatusCode.NotFound);
    }

    private (KickAssAnimeProvider Provider, EnglishFakeHandler Handler) Create(Func<string>? baseUrl = null)
    {
        var handler = new EnglishFakeHandler(Dispatch);
        var config = new EnglishTestConfig(() => new ProviderRemoteConfig { BaseUrls = [(baseUrl ?? (() => Srv))()] });
        return (new KickAssAnimeProvider(new HttpClient(handler), config), handler);
    }

    [Fact]
    public void Metadata()
    {
        var (p, _) = Create();
        Assert.Equal("kickassanime", p.Id);
        Assert.Equal("KickAssAnime", p.DisplayName);
        Assert.Equal("en", Assert.Single(p.ContentLanguages));
        Assert.Equal(1, p.Priority);
        Assert.False(p.GfwBlocked);
    }

    [Fact]
    public async Task SearchMapsFieldsAndPostsJson()
    {
        var (p, h) = Create();
        var r = await p.SearchAsync("frieren", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        var a = r[0];
        Assert.Equal("sousou-no-frieren-2d15", a.Id);
        Assert.Equal("kickassanime", a.ProviderId);
        Assert.Equal("Frieren: Beyond Journey's End", a.Title);
        Assert.Equal(2023, a.Year);
        Assert.Equal($"{Srv}/image/poster/sousou-no-frieren-f6d4-hq.webp", a.CoverUrl);
        var req = h.Requests.First(x => x.Path == "/api/fsearch");
        Assert.Equal("POST", req.Method);
        Assert.Equal("""{"query":"frieren"}""", req.Body);
    }

    [Fact]
    public async Task BlankQueryIsEmptyWithoutRequest()
    {
        var (p, h) = Create();
        Assert.Empty(await p.SearchAsync("  ", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task DubSearchKeepsOnlyShowsWithEnglishDub()
    {
        var (p, _) = Create();
        var sub = await p.SearchAsync("frieren", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        var dub = await p.SearchAsync("frieren", TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(dub.Count > 0 && dub.Count < sub.Count);
        Assert.DoesNotContain(dub, a => a.Id == "sousou-no-frieren-no-mahou-561b");
    }

    [Fact]
    public async Task BaseUrlIsReadPerRequest()
    {
        var baseUrl = "http://unreachable.test";
        var (p, _) = Create(() => baseUrl);
        await Assert.ThrowsAsync<ProviderException>(() => p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        baseUrl = Srv;
        Assert.NotEmpty(await p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EpisodesPaginateAndSort()
    {
        var (p, h) = Create();
        var eps = await p.EpisodesAsync(Show, TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "1", "2", "3" }, eps.Select(e => e.Number).ToArray());
        Assert.Equal("ep-1-aa83b7", eps[0].Id);
        Assert.Equal("The Journey's End", eps[0].Title);
        Assert.Equal(2, h.Requests.Count(r => r.Path.EndsWith("/episodes", StringComparison.Ordinal)));
        var sub = await p.EpisodesAsync(Show, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("ep-1-f897b3", Assert.Single(sub).Id);
    }

    [Fact]
    public async Task DubStreamsSelectEnglishAudioAndSendOrigin()
    {
        var (p, _) = Create();
        var s = await p.StreamsAsync(Show, (await p.EpisodesAsync(Show, TranslationMode.Dub))[0], TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new int?[] { 1080, 720, 360 }, s.Variants.Select(v => v.Quality).ToArray());
        Assert.Equal($"{Srv}/manifest/67d0c079169c31976b8d7970/67d0c149a60e80857fb0ed26/playlist.m3u8", s.ExternalAudioUrl);
        Assert.Null(s.MasterUrl);
        Assert.Equal(Srv, s.Headers["Origin"]);
        Assert.Equal($"{Srv}/", s.Headers["Referer"]);
        Assert.Contains("Chrome", s.Headers["User-Agent"]);
        Assert.Equal("English dub", s.LanguageLabel);
        var en = s.Subtitles[0];
        Assert.Equal("en", en.Language);
        Assert.True(en.IsDefault);
        Assert.Single(s.Subtitles, t => t.IsDefault);
        Assert.Equal($"{Srv}/subs/67d0c079169c31976b8d7970/67e0fa641967208e6c20882f.vtt", en.Url);
        Assert.True(s.Subtitles.Count >= 8);
    }

    [Fact]
    public async Task SubStreamsUseJapaneseAudio()
    {
        var (p, _) = Create();
        var s = await p.StreamsAsync(Show, new Episode("ep-1-f897b3", "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.EndsWith("/67d0c149a60e80857fb0ed28/playlist.m3u8", s.ExternalAudioUrl);
        Assert.Equal("Japanese audio + subs", s.LanguageLabel);
    }

    [Fact]
    public async Task MissingDubIsProviderException()
    {
        var (p, _) = Create();
        // a ja-JP episode requested as DUB
        await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("ep-1-f897b3", "1"), TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken));
        // master without an English rendition
        _masterBody = m => string.Join("\n", m.Split('\n').Where(l => !l.Contains("LANGUAGE=\"eng\"")));
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("ep-1-aa83b7", "1"), TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("English dub", e.Message);
    }

    [Fact]
    public async Task BadIdsAreRejected()
    {
        var (p, _) = Create();
        await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("../x", "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ProviderException>(() => p.EpisodesAsync(Show with { Id = "a/b" }, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    /// <summary>Hits the real site.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSearchEpisodesStreams()
    {
        var p = new KickAssAnimeProvider(new HttpClient(), new EnglishTestConfig(() => ProviderRemoteConfig.Default));
        foreach (var mode in new[] { TranslationMode.Sub, TranslationMode.Dub })
        {
            var anime = (await p.SearchAsync("frieren", mode, cancellationToken: TestContext.Current.CancellationToken))[0];
            var eps = await p.EpisodesAsync(anime, mode, cancellationToken: TestContext.Current.CancellationToken);
            Assert.NotEmpty(eps);
            var s = await p.StreamsAsync(anime, eps[0], mode, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(s.Variants.Count > 0 && s.ExternalAudioUrl is not null);
        }
    }
}
