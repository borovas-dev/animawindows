using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Common;
using AnimaWindows.Providers.Hosts;
using AnimaWindows.Providers.Shinden;
using AnimaWindows.Providers.Tests.Hosts;

namespace AnimaWindows.Providers.Tests.Shinden;

internal sealed class FakeProviderConfig(Func<ProviderRemoteConfig> current) : IProviderConfig
{
    public ProviderRemoteConfig ForProvider(string id) => current();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}

/// <summary>Provider flow against a fake HTTP handler serving the saved fixtures; offline. Host pages are synthetic (see HostExtractorsTests). Port of ShindenProviderTest.kt.</summary>
public class ShindenProviderTests
{
    private const string Origin = "http://shinden.test";
    private const string Guest = "X2d1ZXN0XzowLDUsMjEwMDAwMDAsMjU1LDQxNzQyOTM2NDQ%3D";

    private string _episodePlayers = "episode_players_external_only.html";
    private string _showBody = Res("player_show_external.html");
    private readonly List<string> _delays = [];
    private readonly HostsFakeHandler _handler;
    private readonly HttpClient _client;

    private static string Res(string name) => HostFixtures.Read("Shinden", name);

    private static readonly Anime TheAnime = new() { Id = "61064-sousou-no-frieren", ProviderId = "shinden", Title = "Frieren" };

    public ShindenProviderTests()
    {
        _handler = new HostsFakeHandler(Dispatch);
        _client = new HttpClient(_handler);
    }

    private HttpResponseMessage Dispatch(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == "down.test") throw new HttpRequestException("connection refused");
        var path = uri.AbsolutePath;
        var query = uri.Query;
        string body;
        if (path == "/series") body = Res("search.html");
        else if (path == "/series/61064-sousou-no-frieren") body = Res("series.html");
        else if (path == "/series/61064-sousou-no-frieren/episodes") body = query.Contains("strona=", StringComparison.Ordinal) ? Res("episodes_langs.html") : Res("episodes.html");
        else if (path.StartsWith("/titles/2395-", StringComparison.Ordinal)) body = "<html></html>";
        else if (path.StartsWith("/episode/", StringComparison.Ordinal)) body = Res(_episodePlayers);
        else if (path.EndsWith("/player_load", StringComparison.Ordinal)) body = "5";
        else if (path.EndsWith("/player_show", StringComparison.Ordinal)) body = _showBody;
        else if (path == "/e/T1R2ZAa8nyc08") body = "<html></html>";
        else if (path == "/api/stream") body = Res("vidara_stream.json");
        else if (path == "/embed/1") body = """player.src([{src: "/v/h/1.mp4", type: "video/mp4"}])""";
        else if (path == "/v/h/1.mp4") return HostsFakeHandler.Status(HttpStatusCode.Found, $"{Origin}/cdn/final.mp4");
        else return HostsFakeHandler.Status(HttpStatusCode.NotFound);
        return HostsFakeHandler.Ok(body);
    }

    private ShindenProvider Provider(IReadOnlyList<string>? mirrors = null, Func<string>? baseUrl = null, int maxAttempts = 3) =>
        new(
            new HostHttp(_client, ShindenProvider.AcceptLanguage, _client),
            new FakeProviderConfig(() => new ProviderRemoteConfig
            {
                BaseUrls = mirrors ?? [baseUrl?.Invoke() ?? Origin],
                Params = new Dictionary<string, string> { ["apiUrl"] = Origin },
            }),
            delay: (t, _) =>
            {
                _delays.Add(t.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return Task.CompletedTask;
            },
            maxResolveAttempts: maxAttempts);

    [Fact]
    public async Task SearchAndDetails()
    {
        var p = Provider();
        var r = await p.SearchAsync("frieren", TranslationMode.Sub);
        Assert.Equal(4, r.Count);
        Assert.StartsWith("/series?search=frieren", Assert.Single(_handler.Seen));
        var d = await p.DetailsAsync(r[0]);
        Assert.StartsWith("Król", d.Synopsis);
    }

    [Fact]
    public async Task EpisodesFollowPaginationAndAscend()
    {
        var eps = await Provider().EpisodesAsync(TheAnime, TranslationMode.Sub);
        // page 1 fixture has 28..23 (6 rows), page 2 fixture has 3 more; reversed to ascending overall
        Assert.Equal(9, eps.Count);
        Assert.Equal("28", eps[^1].Number);
        Assert.Contains(_handler.Seen, s => s.Contains("strona=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TitlesPrefixFallbackOn404()
    {
        var p = Provider();
        await Assert.ThrowsAsync<ProviderException>(() =>
            p.EpisodesAsync(new Anime { Id = "2395-death-note", ProviderId = "shinden", Title = "DN" }, TranslationMode.Sub));
        // /titles page is empty -> no episodes, but the 404 on /series was handled
        Assert.StartsWith("/series/2395-death-note", _handler.Seen[0]);
        Assert.Contains(_handler.Seen, s => s.StartsWith("/titles/2395-death-note", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuestOnlyExternalLinksGiveClearError()
    {
        var e = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider().StreamsAsync(TheAnime, new Episode("226988", "1"), TranslationMode.Sub));
        Assert.Contains("Crunchyroll", e.Message);
        Assert.Contains("external service", e.Message);
        Assert.DoesNotContain(_handler.Seen, s => s.Contains("player_show", StringComparison.Ordinal)); // link-out services are skipped without API calls
    }

    [Fact]
    public async Task DubModeWithoutPolishAudioExplains()
    {
        var e = await Assert.ThrowsAsync<ProviderException>(() =>
            Provider().StreamsAsync(TheAnime, new Episode("226988", "1"), TranslationMode.Dub));
        Assert.StartsWith("No Polish dub/lektor players", e.Message);
    }

    [Fact]
    public async Task RealPlayerListPicksVidaraHlsForPolishSubs()
    {
        _episodePlayers = "episode_players.html";
        _showBody = $"""<iframe src="{Origin}/e/T1R2ZAa8nyc08"></iframe>""";
        var set = await Provider().StreamsAsync(TheAnime, new Episode("226988", "1"), TranslationMode.Sub);
        Assert.Contains("master.m3u8", set.MasterUrl);
        Assert.Equal("JP audio + PL subs", set.LanguageLabel);
        Assert.Equal($"{Origin}/e/T1R2ZAa8nyc08", set.Headers["Referer"]);
        Assert.Equal(Origin, set.Headers["Origin"]);
        // exactly one player_load/show pair: the best native host (Vidara, 1080p) won first try
        Assert.Equal(1, _handler.Seen.Count(s => s.EndsWith($"player_show?auth={Guest}&width=0&height=-1", StringComparison.Ordinal)));
        Assert.Contains(_handler.Seen, s => s.StartsWith("/xhr/1879637/player_load", StringComparison.Ordinal));
        // the antibot pause between player_load and player_show is 5000 ms by default
        Assert.Equal("5000", Assert.Single(_delays));
    }

    [Fact]
    public async Task EmbeddedHostResolvesToStreamWithHeadersAndLabel()
    {
        _episodePlayers = "episode_players_sibnet.html";
        _showBody = $"""<iframe src="{Origin}/embed/1"></iframe>""";
        var p = Provider();
        p.HostResolver = _ => SibnetExtractor.Instance; // fake host stands in for video.sibnet.ru
        var set = await p.StreamsAsync(TheAnime, new Episode("1", "1"), TranslationMode.Dub);
        Assert.Equal($"{Origin}/cdn/final.mp4", Assert.Single(set.Variants).Url);
        Assert.Equal("Polish dub/lektor", set.LanguageLabel);
        Assert.Equal("https://video.sibnet.ru/", set.Headers["Referer"]);
        Assert.Contains(_handler.Seen, s => s.Contains("/xhr/9001/player_load", StringComparison.Ordinal) && s.Contains("auth=X2d1ZXN0XzowLDUs", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MaxThreePlayersAreTriedAndErrorsAreCombined()
    {
        var handler = new HostsFakeHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path.StartsWith("/episode/", StringComparison.Ordinal))
            {
                var players = string.Join(",", Enumerable.Range(1, 5).Select(i => $$"""{"online_id":"{{i}}","player":"Cda","audio":"jp","sub":"pl","res":"{{1000 - i}}p"}"""));
                players += ",{\"online_id\":\"9\",\"player\":\"Mega\",\"audio\":\"jp\",\"sub\":\"pl\",\"res\":\"1080p\"}";
                return HostsFakeHandler.Ok("<script id=\"episode-players-data\" type=\"application/json\">{\"players\":[" + players + "]}</script>");
            }
            return HostsFakeHandler.Ok(path.EndsWith("/player_load", StringComparison.Ordinal) ? "5" : """<div class="error player-message">nope</div>""");
        });
        var p = new ShindenProvider(
            new HostHttp(new HttpClient(handler)),
            new FakeProviderConfig(() => new ProviderRemoteConfig { BaseUrls = [Origin], Params = new Dictionary<string, string> { ["apiUrl"] = Origin } }),
            delay: (_, _) => Task.CompletedTask);
        var e = await Assert.ThrowsAsync<ProviderException>(() => p.StreamsAsync(TheAnime, new Episode("1", "1"), TranslationMode.Sub));
        Assert.StartsWith("No playable Shinden source for episode 1: ", e.Message);
        Assert.Contains("Mega: MEGA is end-to-end encrypted", e.Message);
        Assert.Equal(3, handler.Seen.Count(s => s.Contains("/player_show", StringComparison.Ordinal)));
        Assert.Contains("not tried (attempt limit)", e.Message);
    }

    [Fact]
    public async Task BaseUrlIsReadPerRequest()
    {
        var baseUrl = "http://down.test";
        var p = Provider(baseUrl: () => baseUrl);
        await Assert.ThrowsAsync<ProviderException>(() => p.SearchAsync("x", TranslationMode.Sub));
        baseUrl = Origin;
        Assert.NotEmpty(await p.SearchAsync("x", TranslationMode.Sub));
    }

    [Fact]
    public async Task MirrorIsTriedOnConnectionFailure()
    {
        var p = Provider(mirrors: ["http://down.test", Origin]);
        Assert.NotEmpty(await p.SearchAsync("x", TranslationMode.Sub));
    }

    /// <summary>Hits the real site; only with SHINDEN_LIVE=1 (never in CI). SHINDEN_Q and SHINDEN_MODE=DUB|SUB tune it.</summary>
    [Fact]
    [Trait("Category", "Live")]
    public async Task LiveSearchEpisodesStreams()
    {
        if (Environment.GetEnvironmentVariable("SHINDEN_LIVE") != "1") return;
        var mode = Environment.GetEnvironmentVariable("SHINDEN_MODE") == "DUB" ? TranslationMode.Dub : TranslationMode.Sub;
        using var client = new HttpClient(new SocketsHttpHandler { UseCookies = true });
        var p = ShindenProvider.Create(client, new FakeProviderConfig(() => ProviderRemoteConfig.Default));
        var anime = (await p.SearchAsync(Environment.GetEnvironmentVariable("SHINDEN_Q") ?? "frieren", mode)).First();
        var eps = await p.EpisodesAsync(anime, mode);
        Assert.NotEmpty(eps);
        try
        {
            _ = await p.StreamsAsync(anime, eps[0], mode);
        }
        catch (ProviderException)
        {
        }
    }
}
