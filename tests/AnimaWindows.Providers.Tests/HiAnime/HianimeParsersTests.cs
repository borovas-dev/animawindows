using System.Text;
using AnimaWindows.Providers.HiAnime;
using AnimaWindows.Providers.Tests.Common;

namespace AnimaWindows.Providers.Tests.HiAnime;

public class HianimeParsersTests
{
    private static string Res(string name) => EnglishFixtures.Read("HiAnime", name);

    [Fact]
    public void SearchParsesTitlesIdsCoversCountsAndSkipsSidebar()
    {
        var r = HianimeParsers.ParseSearch(Res("search.html"), "hianime");
        Assert.Equal(new[] { "one-piece-1", "one-piece-film-red-2825" }, r.Select(a => a.Id).ToArray());
        Assert.Equal("One Piece", r[0].Title);
        Assert.Equal("https://cdn.example.net/thumb/one-piece.jpg", r[0].CoverUrl);
        Assert.Equal(1180, r[0].SubEpisodes);
        Assert.Equal(1157, r[0].DubEpisodes);
        Assert.Equal("One Piece Film: Red & Co", r[1].Title);
        Assert.Equal("https://cdn.example.net/thumb/red.jpg", r[1].CoverUrl);
        Assert.Equal(1, r[1].SubEpisodes);
        Assert.Null(r[1].DubEpisodes);
        Assert.DoesNotContain(r, a => a.Id.StartsWith("sidebar"));
    }

    [Fact]
    public void EpisodesFromJsonSortedAndFilteredBySlug()
    {
        var eps = HianimeParsers.ParseEpisodes(Res("episodes.json"), "one-piece-1");
        Assert.Equal(new[] { "1", "2", "10", "11.5" }, eps.Select(e => e.Number).ToArray());
        Assert.Equal(new[] { "1", "2", "10", "115" }, eps.Select(e => e.Id).ToArray());
    }

    [Fact]
    public void EpisodesFromRawEscapedHtml()
    {
        const string raw = """<a class=\"ep-item\" data-number=\"3\" data-id=\"33\" href=\"https:\/\/hianime.at\/watch\/x-7?ep=33\">""";
        var eps = HianimeParsers.ParseEpisodes(raw, "x-7");
        Assert.Equal(new[] { ("3", "33") }, eps.Select(e => (e.Number, e.Id)).ToArray());
    }

    [Fact]
    public void EmbedUrlFromRealServersFixture()
    {
        Assert.Equal("https://zokoanime.video/stream/mal/21/1/sub", HianimeParsers.ParseEmbedUrl(Res("servers.json"), "sub"));
        Assert.Equal("https://zokoanime.video/stream/mal/21/1/dub", HianimeParsers.ParseEmbedUrl(Res("servers.json"), "dub"));
        Assert.Equal("https://zokoanime.video/", HianimeParsers.EmbedOrigin("https://zokoanime.video/stream/mal/21/1/sub"));
        Assert.Equal(21, HianimeParsers.MalId("https://zokoanime.video/stream/mal/21/1/sub"));
    }

    [Fact]
    public void RealEmbedBlobDecodes()
    {
        var blob = HianimeParsers.ExtractBlob(Res("embed.html"))!;
        var cfg = HianimeParsers.ParseEmbedConfig(HianimeParsers.DeobfuscateBlob(blob));
        Assert.EndsWith("master.m3u8", cfg.MasterUrl);
        Assert.Single(cfg.Subtitles);
        Assert.True(cfg.Subtitles[0].IsDefault);
        Assert.Equal("en", cfg.Subtitles[0].Language);
        Assert.Equal(31.0, cfg.Intro!.StartSec);
        Assert.Equal(111.0, cfg.Intro!.EndSec);
        Assert.Equal(1376.0, cfg.Outro!.StartSec);
        Assert.Equal(1447.0, cfg.Outro!.EndSec);
    }

    [Fact]
    public void EmbedSkipMissingOrInvalidIsNull()
    {
        var none = HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8"}""");
        Assert.Null(none.Intro);
        var bad = HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8","skip":{"intro":{"start":10,"end":5},"outro":{"start":0,"end":0}}}""");
        Assert.Null(bad.Intro);
        Assert.Null(bad.Outro);
    }

    [Fact]
    public void SpriteVttParsedAndEmptyIsNull()
    {
        const string url = "https://hls.example.com/v/a/b/sprite.vtt";
        Assert.Equal(url, HianimeParsers.ParseEmbedConfig($$"""{"src":"https://a/b.m3u8","sprite_vtt":"{{url}}"}""").SpriteVttUrl);
        Assert.Null(HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8","sprite_vtt":""}""").SpriteVttUrl);
        Assert.Null(HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8","sprite_vtt":null}""").SpriteVttUrl);
        Assert.Null(HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8","sprite_vtt":"  "}""").SpriteVttUrl);
        Assert.Null(HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8"}""").SpriteVttUrl);
        Assert.Null(HianimeParsers.ParseEmbedConfig("""{"src":"https://a/b.m3u8","sprite_vtt":5}""").SpriteVttUrl);
    }

    [Fact]
    public void XorRoundTrip()
    {
        var plain = Encoding.UTF8.GetBytes("""{"src":"https://a/b.m3u8"}""");
        Assert.Equal(Encoding.UTF8.GetString(plain), Encoding.UTF8.GetString(HianimeParsers.Xor(HianimeParsers.Xor(plain))));
    }

    [Fact]
    public void HtmlPayloadHandlesJsonAndRaw()
    {
        Assert.Equal("<b>x</b>", HianimeParsers.HtmlPayload("""{"html":"<b>x<\/b>"}"""));
        Assert.Equal("<b class=\"a\">", HianimeParsers.HtmlPayload("""<b class=\"a\">"""));
    }

    [Fact]
    public void B64DecodeToleratesUrlSafeAndMissingPadding()
    {
        Assert.Equal("hello?>", Encoding.UTF8.GetString(HianimeParsers.B64Decode("aGVsbG8_Pg")));
    }

    [Fact]
    public void ParseDetailsPrefersPageElementsThenOpenGraph()
    {
        var (synopsis, cover) = HianimeParsers.ParseDetails(
            """<html><head><meta property="og:image" content="https://i/og.jpg"></head><body><div class="film-description"><div class="text"> A  story. </div></div></body></html>""");
        Assert.Equal("A story.", synopsis);
        Assert.Equal("https://i/og.jpg", cover);
    }
}
