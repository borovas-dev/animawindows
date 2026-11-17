using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.Tests.Common;

public class M3u8Tests
{
    [Fact]
    public void MasterPlaylist()
    {
        var m = M3u8.ParseMaster(EnglishFixtures.Read("Common", "master.m3u8"), "https://hls.example.com/p/a/master.m3u8");
        Assert.Equal(new int?[] { 1080, 720, 360 }, m.Variants.Select(v => v.Quality).ToArray());
        Assert.Equal("https://hls.example.com/p/a/1080/index.m3u8", m.Variants[0].Url);
        Assert.Equal("https://cdn.example.net/abs/720/index.m3u8", m.Variants[1].Url);
        Assert.Equal("https://hls.example.com/p/a/audio/ja/index.m3u8", m.ExternalAudioUrl);
        Assert.DoesNotContain(m.Variants, v => v.Url.Contains("iframes"));
    }

    [Fact]
    public void ResolveKeepsAbsoluteAndResolvesRootRelative()
    {
        Assert.Equal("https://a/b.m3u8", M3u8.Resolve("https://x/y/z.m3u8", "https://a/b.m3u8"));
        Assert.Equal("https://x/m3u8?url=T", M3u8.Resolve("https://x/m3u8?url=Q", "/m3u8?url=T"));
    }
}
