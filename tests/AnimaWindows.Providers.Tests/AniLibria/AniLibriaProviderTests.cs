using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.AniLibria;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.Tests.AniLibria;

/// <summary>Port of AnilibriaProviderTest.kt, with a routing fake handler instead of MockWebServer.</summary>
public sealed class AniLibriaProviderTests : IDisposable
{
    private const string Origin = "https://anilibria.test";

    private readonly RoutingHandler _handler = new() { Route = Serve };
    private readonly HttpClient _http;
    private readonly StubConfig _config = new();
    private readonly AniLibriaProvider _provider;

    public AniLibriaProviderTests()
    {
        _http = new HttpClient(_handler);
        _config.Config = new ProviderRemoteConfig { BaseUrls = [Origin] };
        _provider = new AniLibriaProvider(_http, _config);
    }

    public void Dispose() => _http.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Metadata()
    {
        Assert.Equal("anilibria", _provider.Id);
        Assert.Equal("AniLibria", _provider.DisplayName);
        Assert.True(_provider.ContentLanguages.SetEquals(new[] { "ru" }));
        Assert.Equal(0, _provider.Priority);
        Assert.False(_provider.GfwBlocked);
    }

    [Fact]
    public async Task SearchMapsFields()
    {
        var anime = (await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct))[0];
        Assert.Equal("413", anime.Id);
        Assert.Equal("anilibria", anime.ProviderId);
        Assert.Equal("Наруто Ураганные хроники", anime.Title);
        Assert.Equal(1735, anime.MalId);
        Assert.Equal(2007, anime.Year);
        Assert.Equal(450, anime.SubEpisodes);
        Assert.StartsWith(Origin + "/storage/releases/posters/413/", anime.CoverUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchSendsQueryLanguageAndUserAgent()
    {
        await _provider.SearchAsync("  naruto  ", TranslationMode.Sub, Ct);
        var request = Assert.Single(_handler.Requests);
        Assert.Equal(Origin + "/api/v1/app/search/releases?query=naruto", request.Url);
        Assert.Equal("ru,en;q=0.8", request.AcceptLanguage);
        Assert.Equal(NetworkConstants.AniCliUserAgent, request.UserAgent);
    }

    [Fact]
    public async Task DubIsEmpty()
    {
        Assert.Empty(await _provider.SearchAsync("naruto", TranslationMode.Dub, Ct));
        var anime = (await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct))[0];
        Assert.Empty(await _provider.EpisodesAsync(anime, TranslationMode.Dub, Ct));
        var episode = (await _provider.EpisodesAsync(anime, TranslationMode.Sub, Ct))[0];
        await Assert.ThrowsAsync<ProviderException>(() => _provider.StreamsAsync(anime, episode, TranslationMode.Dub, Ct));
    }

    [Fact]
    public async Task BlankQueryDoesNotCallTheApi()
    {
        Assert.Empty(await _provider.SearchAsync("   ", TranslationMode.Sub, Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task EpisodesSortedAndStreams()
    {
        var anime = (await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct))[0];
        var episodes = await _provider.EpisodesAsync(anime, TranslationMode.Sub, Ct);
        Assert.Equal(new[] { "1", "370" }, episodes.Select(e => e.Number));

        var s1 = await _provider.StreamsAsync(anime, episodes[0], TranslationMode.Sub, Ct);
        Assert.Equal(new int?[] { 1080, 720, 480 }, s1.Variants.Select(v => v.Quality));
        Assert.Equal(s1.Variants[0].Url, s1.MasterUrl);
        Assert.Equal(1735, s1.MalId);
        var intro = s1.Intro!;
        var outro = s1.Outro!;
        Assert.Equal(1.0, intro.StartSec);
        Assert.Equal(100.0, intro.EndSec);
        Assert.Equal(1300.0, outro.StartSec);
        Assert.Equal(1390.0, outro.EndSec);
        Assert.Equal(Origin + "/", s1.Headers["Referer"]);
        Assert.Equal(NetworkConstants.AniCliUserAgent, s1.Headers["User-Agent"]);

        var s2 = await _provider.StreamsAsync(anime, episodes[1], TranslationMode.Sub, Ct);
        Assert.Equal(new int?[] { 720, 480 }, s2.Variants.Select(v => v.Quality));
        Assert.Null(s2.Outro);
    }

    [Fact]
    public async Task DetailsFillsMissingFieldsFromRelease()
    {
        var bare = new Anime { Id = "413", ProviderId = "anilibria", Title = "bare" };
        var full = await _provider.DetailsAsync(bare, Ct);
        Assert.Equal("bare", full.Title);
        Assert.Equal(1735, full.MalId);
        Assert.Equal(2007, full.Year);
        Assert.Equal(450, full.SubEpisodes);
        Assert.NotNull(full.Synopsis);
        Assert.StartsWith(Origin, full.CoverUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailsReturnsInputWhenReleaseIsMissing()
    {
        var anime = new Anime { Id = "999", ProviderId = "anilibria", Title = "x" };
        Assert.Same(anime, await _provider.DetailsAsync(anime, Ct));
    }

    [Fact]
    public async Task MissingReleaseIsProviderException()
    {
        var anime = new Anime { Id = "999", ProviderId = "anilibria", Title = "x" };
        await Assert.ThrowsAsync<ProviderException>(() => _provider.EpisodesAsync(anime, TranslationMode.Sub, Ct));
    }

    [Fact]
    public async Task InvalidIdIsRejectedBeforeAnyRequest()
    {
        var anime = new Anime { Id = "../admin", ProviderId = "anilibria", Title = "x" };
        await Assert.ThrowsAsync<ProviderException>(() => _provider.EpisodesAsync(anime, TranslationMode.Sub, Ct));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public async Task BaseUrlIsReadPerRequest()
    {
        _config.Config = new ProviderRemoteConfig { BaseUrls = ["https://down.test"] };
        await Assert.ThrowsAnyAsync<ProviderException>(() => _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));

        _config.Config = new ProviderRemoteConfig { BaseUrls = [Origin] };
        Assert.NotEmpty(await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
    }

    [Fact]
    public async Task ApiPathParamIsRespected()
    {
        _config.Config = new ProviderRemoteConfig
        {
            BaseUrls = [Origin],
            Params = new Dictionary<string, string> { ["apiPath"] = "/v9/" },
        };
        // Routes only know /api/v1, so a different apiPath must produce a 404 ProviderException.
        await Assert.ThrowsAsync<ProviderException>(() => _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
        Assert.StartsWith(Origin + "/v9/app/search/releases", _handler.Requests[0].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MirrorIsTriedOnConnectionFailure()
    {
        _config.Config = new ProviderRemoteConfig { BaseUrls = ["https://down.test", Origin] };
        Assert.NotEmpty(await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
        Assert.StartsWith("https://down.test/", _handler.Requests[0].Url, StringComparison.Ordinal);
        Assert.StartsWith(Origin + "/", _handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerErrorMovesToNextMirror()
    {
        _config.Config = new ProviderRemoteConfig { BaseUrls = ["https://broken.test", Origin] };
        Assert.NotEmpty(await _provider.SearchAsync("naruto", TranslationMode.Sub, Ct));
        Assert.StartsWith(Origin + "/", _handler.Requests[1].Url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CloudflareChallengeIsNotRetriedOnMirrors()
    {
        _config.Config = new ProviderRemoteConfig { BaseUrls = ["https://cf.test", Origin] };
        var anime = new Anime { Id = "413", ProviderId = "anilibria", Title = "x" };
        var ex = await Assert.ThrowsAsync<CloudflareChallengeException>(() => _provider.EpisodesAsync(anime, TranslationMode.Sub, Ct));
        Assert.Equal("https://cf.test/api/v1/anime/releases/413", ex.Url);
        Assert.Single(_handler.Requests);
    }

    private static HttpResponseMessage Serve(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        switch (uri.Host)
        {
            case "down.test":
                throw new HttpRequestException("connection refused");
            case "broken.test":
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            case "cf.test":
                var challenge = new HttpResponseMessage(HttpStatusCode.Forbidden);
                challenge.Headers.Add("cf-mitigated", "challenge");
                return challenge;
        }
        return uri.AbsolutePath switch
        {
            "/api/v1/app/search/releases" => Fixture.Json(Fixture.Read("search.json")),
            "/api/v1/anime/releases/413" => Fixture.Json(Fixture.Read("release.json")),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }
}
