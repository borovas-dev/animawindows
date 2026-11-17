using AnimaWindows.Core.Models;
using AnimaWindows.Providers.AniLibria;

namespace AnimaWindows.Providers.Tests.AniLibria;

public sealed class AniLibriaParserTests
{
    [Fact]
    public void NumberOfDropsTrailingZeroDecimals()
    {
        Assert.Equal("3", AniLibriaParsers.NumberOf(3.0));
        Assert.Equal("12.5", AniLibriaParsers.NumberOf(12.5));
    }

    [Fact]
    public void ToIntervalNeedsBothBoundsAndStopAfterStart()
    {
        Assert.Null(AniLibriaParsers.ToInterval(null));
        Assert.Null(AniLibriaParsers.ToInterval(new AniLibriaSkip { Start = 10, Stop = 10 }));
        Assert.Null(AniLibriaParsers.ToInterval(new AniLibriaSkip { Start = null, Stop = 90 }));
        var interval = AniLibriaParsers.ToInterval(new AniLibriaSkip { Start = 1, Stop = 90 });
        Assert.Equal(1.0, interval!.StartSec);
        Assert.Equal(90.0, interval.EndSec);
    }

    [Fact]
    public void MalformedReleaseIsProviderException()
    {
        Assert.Throws<ProviderException>(() => AniLibriaParsers.ParseRelease("{not json"));
        Assert.Throws<ProviderException>(() => AniLibriaParsers.ParseSearch("null", "https://x.test"));
    }

    [Fact]
    public void EpisodeWithoutIdIsRejected()
    {
        const string json = """{"id":1,"episodes":[{"name":"no id","ordinal":1}]}""";
        Assert.Throws<ProviderException>(() => AniLibriaParsers.ParseRelease(json));
    }

    [Fact]
    public void HlsVariantsSkipBlankUrlsAndKeepBestFirst()
    {
        var episode = new AniLibriaEpisode { Id = "a", Hls1080 = " ", Hls720 = "https://c.test/720.m3u8", Hls480 = "https://c.test/480.m3u8" };
        var variants = AniLibriaParsers.HlsVariants(episode);
        Assert.Equal(new int?[] { 720, 480 }, variants.Select(v => v.Quality));
    }
}
