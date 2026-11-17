using System.Net;
using System.Text;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.HiAnime;
using AnimaWindows.Providers.Tests.Common;

namespace AnimaWindows.Providers.Tests.HiAnime;

public class HianimeProviderTests
{
    private const string Origin = "http://hianime.test";
    private bool _cloudflare;

    private static string Res(string name) => EnglishFixtures.Read("HiAnime", name);
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));
    private static readonly Anime Show = new() { Id = "one-piece-1", ProviderId = "hianime", Title = "One Piece" };

    private HttpResponseMessage Dispatch(EnglishRecordedRequest r)
    {
        if (r.Uri.Host == "unreachable.test") throw new HttpRequestException("connection refused");
        var path = r.Path;
        if (_cloudflare) return EnglishFakeHandler.Status(HttpStatusCode.Forbidden, "<title>Just a moment...</title>");
        if (path == "/search") return EnglishFakeHandler.Ok(Res("search.html"));
        if (path.StartsWith("/api/theme/episode/list/", StringComparison.Ordinal)) return EnglishFakeHandler.Ok(Res("episodes.json"));
        if (path == "/api/theme/episode/servers")
        {
            var hash = B64($"{Origin}/stream/mal/21/1/sub");
            var html = $$"""<div class="item server-item" data-type="sub" data-server-name="HD-1" data-hash="x"></div><div class="item server-item" data-type="sub" data-server-name="ZokoAnime" data-hash="{{hash}}"></div>""";
            return EnglishFakeHandler.Ok("""{"status":true,"html":""" + System.Text.Json.JsonSerializer.Serialize(html) + "}");
        }
        if (path.StartsWith("/stream/", StringComparison.Ordinal))
        {
            var cfg = $$"""{"src":"{{Origin}}/p/x/master.m3u8","subtitles":[{"lang":"pl","label":"Polish","default":false,"src":"{{Origin}}/s/pl.vtt"},{"lang":"en","label":"English","default":true,"src":"{{Origin}}/s/en.vtt"}]}""";
            var blob = Convert.ToBase64String(HianimeParsers.Xor(Encoding.UTF8.GetBytes(cfg)));
            return EnglishFakeHandler.Ok($"<html><script>window.__P=\"{blob}\";</script></html>");
        }
        if (path == "/p/x/master.m3u8") return EnglishFakeHandler.Ok(Res("master.m3u8"));
        if (path == "/one-piece-1") return EnglishFakeHandler.Ok(Res("search.html").Replace("film-detail", "x"));
        return EnglishFakeHandler.Status(HttpStatusCode.NotFound);
    }

    private (HianimeProvider Provider, EnglishFakeHandler Handler) Create(
        Func<string>? baseUrl = null, IReadOnlyList<string>? mirrors = null, IReadOnlyDictionary<string, string>? params_ = null)
    {
        var handler = new EnglishFakeHandler(Dispatch);
        var config = new EnglishTestConfig(() => new ProviderRemoteConfig
        {
            BaseUrls = mirrors ?? [(baseUrl ?? (() => Origin))()],
            Params = params_ ?? new Dictionary<string, string>(),
        });
        return (new HianimeProvider(new HttpClient(handler), config), handler);
    }

    [Fact]
    public void Metadata()
    {
        var (p, _) = Create();
        Assert.Equal("hianime", p.Id);
        Assert.Equal("HiAnime", p.DisplayName);
        Assert.Equal("en", Assert.Single(p.ContentLanguages));
        Assert.True(p.ReportsDubAvailability);
        Assert.Equal(0, p.Priority);
        Assert.False(p.GfwBlocked);
    }

    [Fact]
    public async Task Search()
    {
        var (p, h) = Create();
        var r = await p.SearchAsync("one piece", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, r.Count);
        Assert.Equal("keyword=one+piece", h.Requests[0].Uri.Query.TrimStart('?'));
        Assert.Equal("/search", h.Requests[0].Path);
    }

    [Fact]
    public async Task Episodes()
    {
        var (p, h) = Create();
        var eps = await p.EpisodesAsync(Show, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "1", "2", "10", "11.5" }, eps.Select(e => e.Number).ToArray());
        Assert.Equal("/api/theme/episode/list/1", h.Requests[0].Path);
    }

    [Fact]
    public async Task DubEpisodesCappedToDubCount()
    {
        var (p, _) = Create();
        var eps = await p.EpisodesAsync(Show with { DubEpisodes = 2 }, TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "1", "2" }, eps.Select(e => e.Number).ToArray());
    }

    [Fact]
    public async Task EpisodesRejectIdWithoutNumericSuffix()
    {
        var (p, _) = Create();
        await Assert.ThrowsAsync<ProviderException>(() => p.EpisodesAsync(Show with { Id = "one-piece" }, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StreamsFullPipeline()
    {
        var (p, h) = Create();
        var s = await p.StreamsAsync(Show, new Episode("1", "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        const string origin = Origin + "/";
        Assert.Equal($"{origin}p/x/master.m3u8", s.MasterUrl);
        Assert.Equal(new int?[] { 1080, 720, 360 }, s.Variants.Select(v => v.Quality).ToArray());
        Assert.Equal($"{origin}p/x/1080/index.m3u8", s.Variants[0].Url);
        Assert.Equal($"{origin}p/x/audio/ja/index.m3u8", s.ExternalAudioUrl);
        Assert.Equal(origin, s.Headers["Referer"]);
        Assert.StartsWith("Mozilla/5.0", s.Headers["User-Agent"]);
        Assert.Equal(21, s.MalId);
        Assert.Equal(new[] { "en", "pl" }, s.Subtitles.Select(t => t.Language).ToArray());
        Assert.True(s.Subtitles[0].IsDefault);
        // embed request carried the site as referer; master carried the embed origin
        Assert.Equal(origin, h.Requests.First(r => r.Path.StartsWith("/stream/", StringComparison.Ordinal)).Header("Referer"));
        Assert.Equal(origin, h.Requests.First(r => r.Path == "/p/x/master.m3u8").Header("Referer"));
    }

    [Fact]
    public async Task DubMissingGivesProviderError()
    {
        var (p, _) = Create();
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("1", "1"), TranslationMode.Dub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("dub", e.Message);
    }

    [Fact]
    public async Task CloudflareMapsToChallengeException()
    {
        _cloudflare = true;
        var (p, _) = Create();
        var e = await Assert.ThrowsAsync<CloudflareChallengeException>(() => p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("/search", e.Url);
    }

    [Fact]
    public async Task DetailsNeverThrows()
    {
        _cloudflare = true;
        var (p, _) = Create();
        Assert.Equal(Show, await p.DetailsAsync(Show, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BaseUrlIsReadPerRequest()
    {
        var baseUrl = "http://unreachable.test";
        var (p, _) = Create(baseUrl: () => baseUrl);
        await Assert.ThrowsAsync<ProviderException>(() => p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        baseUrl = Origin;
        Assert.NotEmpty(await p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MirrorIsTriedOnConnectionFailure()
    {
        var (p, _) = Create(mirrors: ["http://unreachable.test", Origin]);
        Assert.NotEmpty(await p.SearchAsync("x", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ServerNameParamIsUsed()
    {
        var (p, _) = Create(params_: new Dictionary<string, string> { ["serverName"] = "HD-1" });
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("1", "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("HD-1", e.Message);
    }

    [Fact]
    public async Task XorKeyParamIsUsed()
    {
        var (p, _) = Create(params_: new Dictionary<string, string> { ["xorKey"] = "another-key" });
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(Show, new Episode("1", "1"), TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Matches("decode|config", e.Message);
    }

    /// <summary>Hits the real site.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSearchEpisodesStreams()
    {
        var p = new HianimeProvider(new HttpClient(), new EnglishTestConfig(() => ProviderRemoteConfig.Default));
        var anime = (await p.SearchAsync("one piece", TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken))[0];
        var eps = await p.EpisodesAsync(anime, TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(eps);
        var s = await p.StreamsAsync(anime, eps[0], TranslationMode.Sub, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEmpty(s.Variants);
    }
}
