using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Hosts;
using AnimaWindows.Providers.Shinden;
using AnimaWindows.Providers.Tests.Hosts;

namespace AnimaWindows.Providers.Tests.Shinden;

/// <summary>Fixtures named *_players*, search*, series, episodes* and player_show_external are trimmed real shinden.pl responses (2026-11-01, guest). Port of ShindenParsersTest.kt.</summary>
public class ShindenParsersTests
{
    private static string Res(string name) => HostFixtures.Read("Shinden", name);

    [Fact]
    public void SearchParsesIdsCoversYearAndEpisodes()
    {
        var r = ShindenParsers.ParseSearch(Res("search.html"), "shinden", "https://shinden.pl");
        Assert.Equal(4, r.Count);
        Assert.Equal("61064-sousou-no-frieren", r[0].Id);
        Assert.Equal("Sousou no Frieren", r[0].Title);
        Assert.Equal(2023, r[0].Year);
        Assert.Equal(28, r[0].SubEpisodes);
        Assert.Equal("https://shinden.pl/res/images/280x400/372465.webp?v=1792160755", r[0].CoverUrl);
        Assert.Null(r[3].SubEpisodes); // data-episodes="0"
        Assert.Equal("shinden", r[0].ProviderId);
    }

    [Fact]
    public void SearchAcceptsTitlesPrefix()
    {
        var r = ShindenParsers.ParseSearch(Res("search_titles.html"), "shinden", "https://shinden.pl");
        Assert.Equal("2395-death-note", r[0].Id);
    }

    [Fact]
    public void DetailsHaveSynopsisCoverAndNoMalId()
    {
        var d = ShindenParsers.ParseDetails(Res("series.html"));
        Assert.NotNull(d.Synopsis);
        Assert.StartsWith("Król demonów został pokonany", d.Synopsis);
        Assert.Equal("https://cdn.shinden.eu/cdn1/images/genuine/372465.webp?v=1791852320", d.Cover);
        Assert.Null(d.MalId);
    }

    [Fact]
    public void DetailsFindMalIdWhenLinked()
    {
        var d = ShindenParsers.ParseDetails("""<a href="https://myanimelist.net/anime/52991/Sousou_no_Frieren">MAL</a>""");
        Assert.Equal(52991, d.MalId);
    }

    [Fact]
    public void EpisodeRowsAndPaging()
    {
        var html = Res("episodes.html");
        var rows = ShindenParsers.ParseEpisodeRows(html);
        Assert.Equal(6, rows.Count);
        Assert.Equal("28", rows[0].Number);
        Assert.Equal("227851", rows[0].Id);
        Assert.Equal("To byłoby zawstydzające, gdybyśmy spotkali się ponownie", rows[0].Title);
        Assert.Equal(2, ShindenParsers.PageCount(html));
        Assert.Equal(1, ShindenParsers.PageCount("<p>none</p>"));
    }

    [Fact]
    public void RowsWithoutLinksAreSkipped()
    {
        Assert.Empty(ShindenParsers.ParseEpisodeRows(Res("episodes_nolinks.html")));
        Assert.Equal(3, ShindenParsers.ParseEpisodeRows(Res("episodes_langs.html")).Count);
    }

    [Fact]
    public void PlayersFromJsonBlock()
    {
        var p = ShindenParsers.ParsePlayers(Res("episode_players.html"));
        Assert.Equal(19, p.Count);
        Assert.Equal(new PlayerEntry("1824661", "Gdrive", "jp", "pl", 1080, "Tłumaczenie:  Zailex | Korekta: anaskor2201", false), p[0]);
        Assert.Equal(
            new[] { "Gdrive", "Mega", "Vidara", "Byse", "Byse", "Yourupload", "Gdrive", "Okru", "Gdrive", "Cda", "Voesx" },
            p.Take(11).Select(x => x.Player).ToArray());
        Assert.Equal(8, p.Count(x => x.Player == "Crunchyroll"));
        var n = Assert.Single(ShindenParsers.ParsePlayers(Res("episode_players_netflix.html")));
        Assert.Equal("Netflix", n.Player);
        Assert.Equal("pl", n.Sub);
        Assert.Equal(1080, n.Res);
        Assert.Equal(8, ShindenParsers.ParsePlayers(Res("episode_players_external_only.html")).Count);
    }

    [Fact]
    public void PlayersFromLegacyButtons()
    {
        var html = """<table><tr><td><a data-episode='{"online_id":"77","player":"Sibnet","lang_audio":"pl","lang_subs":"","max_res":"720p","subs_author":null}'>x</a></td></tr></table>""";
        var p = Assert.Single(ShindenParsers.ParsePlayers(html));
        Assert.Equal(new PlayerEntry("77", "Sibnet", "pl", "", 720, null, false), p);
    }

    [Fact]
    public void PlayerShowExternal()
    {
        var s = Assert.IsType<PlayerShow.External>(ShindenParsers.ParsePlayerShow(Res("player_show_external.html")));
        Assert.Equal("http://www.crunchyroll.com/media-913131", s.Url);
        Assert.Equal("Przejdź do Crunchyroll", s.Label);
    }

    [Fact]
    public void PlayerShowRealIframes()
    {
        static string Embed(string n) => Assert.IsType<PlayerShow.Embed>(ShindenParsers.ParsePlayerShow(Res($"player_show_{n}.html"))).Url;
        Assert.Equal("https://ebd.cda.pl/800x450/1639263768", Embed("cda"));
        Assert.Equal("https://ykfrtza.com/e/T1R2ZAa8nyc08", Embed("vidara"));
        Assert.Equal("https://www.yourupload.com/embed/ecxsc4c856jE", Embed("yourupload"));
        Assert.Equal("https://voe.sx/e/e0ljp4icxhgy", Embed("voesx"));
        Assert.Equal("https://bysesukior.com/e/942k3dhlk95d", Embed("byse"));
        Assert.StartsWith("https://mega.nz/embed/", Embed("mega"));
        Assert.StartsWith("https://drive.google.com/file/d/", Embed("gdrive"));
    }

    [Fact]
    public void PlayerShowIframeAndFailure()
    {
        var e = Assert.IsType<PlayerShow.Embed>(ShindenParsers.ParsePlayerShow("""<div><iframe src="//video.sibnet.ru/shell.php?videoid=1"></iframe></div>"""));
        Assert.Equal("https://video.sibnet.ru/shell.php?videoid=1", e.Url);
        var f = Assert.IsType<PlayerShow.Failure>(ShindenParsers.ParsePlayerShow("""<div class="error message box player-message">Błędne wejście, nie wyświetlam odtwarzacza.</div>"""));
        Assert.StartsWith("Błędne wejście", f.Message);
        Assert.IsType<PlayerShow.Failure>(ShindenParsers.ParsePlayerShow(""));
    }

    [Fact]
    public void IdValidation()
    {
        Assert.True(ShindenParsers.IsValidId("61064-sousou-no-frieren"));
        Assert.False(ShindenParsers.IsValidId("../etc"));
        Assert.False(ShindenParsers.IsValidId("frieren"));
    }

    private static PlayerEntry P(string name, string audio, string sub, int? res, bool machine = false) =>
        new("1", name, audio, sub, res, null, machine);

    [Fact]
    public void RankingMapsModesToLanguages()
    {
        var all = new List<PlayerEntry>
        {
            P("Crunchyroll", "jp", "en", 480), P("Sibnet", "jp", "pl", 720), P("Cda", "pl", "", 1080), P("Streamwish", "jp", "en", 1080),
            P("Vk", "jp", "pl", 1080), P("Cda", "pl", "", 480), P("Gdrive", "jp", "pl", 1080),
        };
        var sub = ShindenSelection.Rank(all, TranslationMode.Sub);
        Assert.Equal(new[] { "Vk", "Sibnet", "Streamwish", "Gdrive", "Crunchyroll" }, sub.Select(x => x.Player).ToArray());
        var dub = ShindenSelection.Rank(all, TranslationMode.Dub);
        Assert.Equal(new int?[] { 1080, 480 }, dub.Select(x => x.Res).ToArray());
        Assert.Empty(ShindenSelection.Rank([P("Cda", "pl", "", 720)], TranslationMode.Sub));
    }

    [Fact]
    public void Labels()
    {
        Assert.Equal("Polish dub/lektor", ShindenSelection.Label(P("Cda", "pl", "", 1)));
        Assert.Equal("JP audio + PL subs", ShindenSelection.Label(P("Cda", "jp", "pl", 1)));
    }

    [Fact]
    public void ExternalAndUnsupportedHostReasons()
    {
        Assert.NotNull(HostRegistry.ExternalReason("Crunchyroll"));
        Assert.NotNull(HostRegistry.ExternalReason("Netflix"));
        Assert.Contains("end-to-end", HostRegistry.ExternalReason("Mega"));
        Assert.Null(HostRegistry.ExternalReason("Sibnet"));
        Assert.Null(HostRegistry.ExternalReason("Cda"));
    }

    [Fact]
    public void ParseAuth_reads_page_token_and_url_encodes_it()
    {
        const string html = "<div class=\"sh_island\" data-sh-module=\"episode-players\" data-player-auth=\"X2d1ZXN0XzowLDUsMjEwMDAwMDAsMjU1LDEyMzQ1Njc4OTA=\" data-x=\"1\"></div>";
        Assert.Equal("X2d1ZXN0XzowLDUsMjEwMDAwMDAsMjU1LDEyMzQ1Njc4OTA%3D", ShindenParsers.ParseAuth(html));
    }

    [Fact]
    public void ParseAuth_returns_null_without_token() => Assert.Null(ShindenParsers.ParseAuth("<html></html>"));

    [Theory]
    [InlineData("_Storage.XHRService = '//shinden.pl/xhr';", "https://shinden.pl/xhr")]
    [InlineData("_Storage.XHRService = '/xhr/';", "https://shinden.pl/xhr")]
    [InlineData("<html></html>", null)]
    public void ParseXhrService_resolves_page_endpoint(string html, string? expected) =>
        Assert.Equal(expected, ShindenParsers.ParseXhrService(html, "https://shinden.pl"));
}
