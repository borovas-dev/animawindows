using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.AnimeFire;

namespace AnimaWindows.Providers.Tests.AnimeFire;

/// <summary>Port of AnimefireProviderTest.kt, with a routing fake handler instead of MockWebServer.</summary>
public sealed class AnimeFireProviderTests : IDisposable
{
    private const string Site = "https://animefire.test";
    private const string Api = "https://api.animefire.test";

    private readonly RoutingHandler _handler = new() { Route = Serve };
    private readonly HttpClient _http;
    private readonly StubConfig _config = new();
    private readonly AnimeFireProvider _provider;

    public AnimeFireProviderTests()
    {
        _http = new HttpClient(_handler);
        _config.Config = new ProviderRemoteConfig
        {
            BaseUrls = [Site],
            Params = new Dictionary<string, string> { ["apiUrl"] = Api },
        };
        _provider = new AnimeFireProvider(_http, _config);
    }

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Metadata()
    {
        Assert.Equal("animefire", _provider.Id);
        Assert.Equal("AnimeFire", _provider.DisplayName);
        Assert.True(_provider.ContentLanguages.SetEquals(new[] { "pt" }));
        Assert.Equal(0, _provider.Priority);
        Assert.False(_provider.GfwBlocked);
    }

    [Fact]
    public async Task SearchTrimsQueryMapsTitlesAndSendsVersionParam()
    {
        var results = await _provider.SearchAsync("  naruto  ", TranslationMode.Sub, Ct);
        Assert.Equal(new[] { "naruto", "naruto-shippuden" }, results.Select(a => a.Id));
        Assert.Equal("Naruto", results[0].Title);
        Assert.Equal("Naruto Shippuden", results[1].Title);
        Assert.Equal("https://img.example/naruto.jpg", results[0].CoverUrl);
        Assert.Equal("Um ninja.", results[0].Synopsis);
        Assert.Equal("animefire", results[0].ProviderId);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Api + "/animes/pesquisar?q=naruto&page=1&v=3", request.Url);
        Assert.Equal(Site + "/", request.Referer);
        Assert.Equal("pt-BR,pt;q=0.9,en;q=0.7", request.AcceptLanguage);
        Assert.Equal(NetworkConstants.AniCliUserAgent, request.UserAgent);
    }

    [Fact]
    public async Task BlankQueryDoesNotCallTheApi()
    {
        Assert.Empty(await _provider.SearchAsync("   ", TranslationMode.Sub, Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task EpisodesUseGlobalNumberingAcrossSeasons()
    {
        var episodes = await _provider.EpisodesAsync(new Anime { Id = "naruto", ProviderId = "animefire", Title = "Naruto" }, TranslationMode.Sub, Ct);
        Assert.Equal(new[] { "1", "2", "3" }, episodes.Select(e => e.Number));
        Assert.Equal(new[] { "e1", "e2", "e3" }, episodes.Select(e => e.Id));
        Assert.Equal("Entrada", episodes[0].Title);
        Assert.Equal("Volta", episodes[2].Title);
        Assert.Null(episodes[1].Title);
        Assert.Equal(Api + "/anime/naruto?v=3", Assert.Single(_handler.Requests).Url);
    }

    [Fact]
    public async Task SubStreamsAreHardsubPortuguese()
    {
        var anime = new Anime { Id = "naruto", ProviderId = "animefire", Title = "Naruto" };
        var s = await _provider.StreamsAsync(anime, new Episode("e1", "1"), TranslationMode.Sub, Ct);
        Assert.Equal("pt", s.HardsubLanguage);
        Assert.EndsWith("/hls/sub/master.m3u8", s.MasterUrl, StringComparison.Ordinal);
        Assert.Equal(new int?[] { 1080, 360 }, s.Variants.Select(v => v.Quality));
        Assert.Equal(Site + "/", s.Headers["Referer"]);
        Assert.Equal(Site, s.Headers["Origin"]);
        Assert.Equal(NetworkConstants.AniCliUserAgent, s.Headers["User-Agent"]);
        Assert.Null(s.ExternalAudioUrl);
    }

    [Fact]
    public async Task DubStreamsSkipTheMachineTranslatedAndHaveNoHardsub()
    {
        var anime = new Anime { Id = "naruto", ProviderId = "animefire", Title = "Naruto" };
        var s = await _provider.StreamsAsync(anime, new Episode("e1", "1"), TranslationMode.Dub, Ct);
        Assert.Null(s.HardsubLanguage);
        Assert.EndsWith("/hls/dub/master.m3u8", s.MasterUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDubIsProviderException()
    {
        var body = Fixture.Read("episode.json").Replace("\"dublado\"", "\"x\"", StringComparison.Ordinal).Replace("BASE", Site, StringComparison.Ordinal);
        _handler.Route = _ => Fixture.Text(body);
        var anime = new Anime { Id = "naruto", ProviderId = "animefire", Title = "Naruto" };
        await Assert.ThrowsAsync<ProviderException>(() => _provider.StreamsAsync(anime, new Episode("e1", "1"), TranslationMode.Dub, Ct));
    }

    [Fact]
    public async Task DetailsFillsMissingSynopsisAndCoverFromHero()
    {
        var bare = new Anime { Id = "naruto", ProviderId = "animefire", Title = "Naruto" };
        var full = await _provider.DetailsAsync(bare, Ct);
        Assert.Equal("Um ninja.", full.Synopsis);
        Assert.Equal("https://img.example/naruto.jpg", full.CoverUrl);
        Assert.Equal("Naruto", full.Title);
    }

    [Fact]
    public async Task MalformedSearchBodyIsProviderException()
    {
        _handler.Route = _ => Fixture.Text("<html>not json</html>");
        await Assert.ThrowsAsync<ProviderException>(() => _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
    }

    [Fact]
    public async Task BaseUrlIsReadPerRequest()
    {
        _config.Config = new ProviderRemoteConfig
        {
            BaseUrls = ["https://down.test"],
            Params = new Dictionary<string, string> { ["apiUrl"] = "https://down.test" },
        };
        await Assert.ThrowsAnyAsync<ProviderException>(() => _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));

        _config.Config = new ProviderRemoteConfig
        {
            BaseUrls = [Site],
            Params = new Dictionary<string, string> { ["apiUrl"] = Api },
        };
        Assert.NotEmpty(await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
    }

    private static HttpResponseMessage Serve(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "down.test") throw new HttpRequestException("connection refused");
        if (uri.AbsolutePath.EndsWith("/master.m3u8", StringComparison.Ordinal)) return Fixture.Text(Fixture.Read("master.m3u8"));
        return uri.AbsolutePath switch
        {
            "/animes/pesquisar" => Fixture.Text(Fixture.Read("search.json")),
            "/anime/naruto" => Fixture.Text(Fixture.Read("anime.json")),
            "/episode/e1" => Fixture.Text(Fixture.Read("episode.json").Replace("BASE", Site, StringComparison.Ordinal)),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }
}
