using AnimaWindows.Core.Models;
using AnimaWindows.Providers.AnimeFire;

namespace AnimaWindows.Providers.Tests.AnimeFire;

public sealed class AnimeFireParserTests
{
    [Fact]
    public void StreamSelectionSkipsOfflineAndBlankAndPrefersNonMtl()
    {
        const string json = """
            {"data":{"anime":{},"streams":[
              {"audio":"legendado","url":"https://a.test/offline.m3u8","is_offline":true},
              {"audio":"legendado","url":"  ","is_mtl":false},
              {"audio":"legendado","url":"https://a.test/mtl.m3u8","is_mtl":true},
              {"audio":"legendado","url":"https://a.test/sub.m3u8","is_mtl":false}
            ]}}
            """;
        Assert.Equal("https://a.test/sub.m3u8", AnimeFireParsers.ParseStreamUrl(json, dub: false));
        Assert.Null(AnimeFireParsers.ParseStreamUrl(json, dub: true));
    }

    [Fact]
    public void StreamSelectionFallsBackToMtlWhenNothingElseExists()
    {
        const string json = """{"data":{"streams":[{"audio":"legendado","url":"https://a.test/mtl.m3u8","is_mtl":true}]}}""";
        Assert.Equal("https://a.test/mtl.m3u8", AnimeFireParsers.ParseStreamUrl(json, dub: false));
    }

    [Fact]
    public void MalformedBodiesAreProviderExceptions()
    {
        Assert.Throws<ProviderException>(() => AnimeFireParsers.ParseSearch("not json", "animefire"));
        Assert.Throws<ProviderException>(() => AnimeFireParsers.ParseDetails("{\"data\":{}}", "animefire"));
        Assert.Throws<ProviderException>(() => AnimeFireParsers.ParseEpisodes("{}"));
    }

    [Fact]
    public void NumberOfDropsTrailingZeroDecimals()
    {
        Assert.Equal("2", AnimeFireParsers.NumberOf(2.0));
        Assert.Equal("12.5", AnimeFireParsers.NumberOf(12.5));
    }

    [Fact]
    public void TitleFallsBackToAnyNonBlankTitleThenId()
    {
        const string json = """{"data":[{"id":"x","titles":{"DE":"  ","FR":"Titre"}},{"id":"y","titles":{}}]}""";
        var results = AnimeFireParsers.ParseSearch(json, "animefire");
        Assert.Equal("Titre", results[0].Title);
        Assert.Equal("y", results[1].Title);
    }
}
