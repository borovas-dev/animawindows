using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Common;
using AnimaWindows.Providers.Hosts;

namespace AnimaWindows.Providers.Tests.Hosts;

/// <summary>
/// Vidara, YourUpload and CDA (embed_cda.html) fixtures are real captures (2026-11-01). The sibnet/jwplayer/vk pages and the
/// legacy-<c>file</c> CDA page below are SYNTHETIC: those hosts had no working video among the sampled live episodes, so the
/// pages are hand-written from each host's documented shape, not captured. Port of HostExtractorsTest.kt.
/// </summary>
public class HostExtractorsTests
{
    private static string Res(string name) => HostFixtures.Read("Hosts", name);

    [Fact]
    public void SibnetShell()
    {
        var html = """<script>player.src([{src: "/v/abc123def/4567890.mp4", type: "video/mp4"}]);</script>""";
        Assert.Equal("/v/abc123def/4567890.mp4", SibnetExtractor.Parse(html));
        Assert.Null(SibnetExtractor.Parse("<html>removed</html>"));
    }

    [Fact]
    public async Task SibnetFollowsExactlyOneRedirectHopAndSendsSibnetReferer()
    {
        var handler = new HostsFakeHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/shell.php" => HostsFakeHandler.Ok("""player.src([{src: "/v/h/1.mp4", type: "video/mp4"}])"""),
            "/v/h/1.mp4" => HostsFakeHandler.Status(HttpStatusCode.Found, "https://cdn.example/final.mp4"),
            _ => HostsFakeHandler.Status(HttpStatusCode.NotFound),
        });
        using var client = new HttpClient(handler);
        var http = new HostHttp(client, noRedirectClient: client); // the fake handler never follows redirects
        var r = await SibnetExtractor.Instance.ExtractAsync("https://video.sibnet.ru/shell.php?videoid=1", http, "https://shinden.pl/");
        Assert.Equal("https://cdn.example/final.mp4", r.Url);
        Assert.False(r.Hls);
        Assert.Equal("https://video.sibnet.ru/", r.Headers["Referer"]);
        Assert.Equal(2, handler.Seen.Count);
        Assert.Equal("https://video.sibnet.ru/shell.php?videoid=1", handler.Requests[1].Referer);
    }

    [Fact]
    public void JwPlayerSources()
    {
        var html = """jwplayer("vplayer").setup({sources:[{file:"https://cdn.example/hls/master.m3u8?t=1",type:"hls"}]});""";
        Assert.Equal("https://cdn.example/hls/master.m3u8?t=1", JwPlayerExtractor.Parse(html, "https://streamwish.to/e/xyz"));
    }

    [Fact]
    public void JwPlayerHlsKeysPreferHighestAndResolveRelative()
    {
        var html = """var links={"hls2":"/stream/a.m3u8","hls4":"/stream/b.m3u8?x=1\/y"};""";
        Assert.Equal("https://filemoon.sx/stream/b.m3u8?x=1/y", JwPlayerExtractor.Parse(html, "https://filemoon.sx/e/abc"));
    }

    [Fact]
    public void JwPlayerPackedScript()
    {
        // eval(function(p,a,c,k,e,d){...}('0.1({2:[{3:"https://cdn.example/v.mp4"}]})',4,4,'jwplayer|setup|sources|file'.split('|')))
        var html = """eval(function(p,a,c,k,e,d){}('0.1({2:[{3:"https://cdn.example/v.mp4"}]})',4,4,'jwplayer|setup|sources|file'.split('|')))""";
        Assert.Equal("https://cdn.example/v.mp4", JwPlayerExtractor.Parse(html, "https://streamwish.to/e/1"));
        Assert.Null(JwPlayerExtractor.Parse("<html></html>", "https://streamwish.to/e/1"));
    }

    private static string Rot(string s) =>
        new(s.Select(c => c is >= (char)33 and <= (char)126 ? (char)(33 + (c + 14) % 94) : c).ToArray());

    [Fact]
    public void CdaDecryptsScrambledFile()
    {
        const string target = "vfm123.cda.pl/xyz/abc";
        Assert.Equal($"https://{target}.mp4", CdaExtractor.DecryptFile(Rot(target)));
        Assert.Equal("https://vf.cda.pl/a.mp4/upstream", CdaExtractor.DecryptFile(Rot("vf.2cda.pl/a/upstream")));
    }

    [Fact]
    public void CdaPlayerDataAttribute()
    {
        var file = WebUtility.UrlEncode(Rot("vfm123.cda.pl/xyz/abc"));
        var json = ("{\"video\":{\"file\":\"" + file + "\",\"quality\":\"720p\",\"ts\":1}}").Replace("\"", "&quot;");
        var src = CdaExtractor.Parse($"""<div id="mediaplayer" player_data="{json}"></div>""");
        Assert.NotNull(src);
        Assert.Equal("https://vfm123.cda.pl/xyz/abc.mp4", src.Url);
        Assert.Equal(720, src.Height);
        Assert.False(src.Hls);
        Assert.Null(CdaExtractor.Parse("<html></html>"));
    }

    [Fact]
    public void CdaRealEmbedUsesHlsManifest()
    {
        var src = CdaExtractor.Parse(Res("embed_cda.html"));
        Assert.NotNull(src);
        Assert.True(src.Hls);
        Assert.Equal("https://vwaw620.cda.pl/Jg7FOlU3MwWTHqfsXKg_cg/1793560813/16na/aaab22bf775/33/13442650raw/13442650.m3u8", src.Url);
        Assert.Equal(1080, src.Height);
    }

    [Fact]
    public void VkVariantsAndHls()
    {
        var html = """{"url240":"https:\/\/cs.vk.me\/240.mp4?a=1&b=2","url720":"https:\/\/cs.vk.me\/720.mp4","hls":"https:\/\/vk.me\/m.m3u8"}""";
        var (mp4, hls) = VkExtractor.Parse(html);
        Assert.Equal(new int?[] { 720, 240 }, mp4.Select(v => v.Quality).ToArray());
        Assert.Equal("https://cs.vk.me/240.mp4?a=1&b=2", mp4[1].Url);
        Assert.Equal("https://vk.me/m.m3u8", hls);
    }

    [Fact]
    public void VidaraStreamingUrlFromRealApiShape()
    {
        var url = VidaraExtractor.ParseStreamingUrl(Res("vidara_stream.json"));
        Assert.NotNull(url);
        Assert.StartsWith("https://s6-s251765.97bf1.com/hls/", url);
        Assert.Contains("master.m3u8", url);
        Assert.Null(VidaraExtractor.ParseStreamingUrl("""{"error":"not found"}"""));
        Assert.Null(VidaraExtractor.ParseStreamingUrl("not json"));
    }

    [Fact]
    public async Task VidaraPostsFilecodeAndReturnsHls()
    {
        var handler = new HostsFakeHandler(_ => HostsFakeHandler.Ok(Res("vidara_stream.json")));
        var http = new HostHttp(new HttpClient(handler));
        var r = await VidaraExtractor.Instance.ExtractAsync("https://ykfrtza.com/e/T1R2ZAa8nyc08", http, "https://shinden.pl/");
        Assert.True(r.Hls);
        Assert.Equal("https://ykfrtza.com", r.Headers["Origin"]);
        Assert.Equal("https://ykfrtza.com/e/T1R2ZAa8nyc08", r.Headers["Referer"]);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("/api/stream", req.Uri.AbsolutePath);
        Assert.Equal("""{"filecode":"T1R2ZAa8nyc08","device":"web"}""", req.Body);
    }

    [Fact]
    public void YouruploadOgVideoFromRealEmbed()
    {
        Assert.Equal("https://vidcache.net:8161/a20261101lQL2pQ62w26/video.mp4", YouruploadExtractor.Parse(Res("embed_yourupload.html")));
        Assert.Equal("https://x.net/v.mp4", YouruploadExtractor.Parse("""file: 'https://x.net/v.mp4'"""));
        Assert.Null(YouruploadExtractor.Parse("<html></html>"));
    }

    [Fact]
    public void RegistryRoutesHostsAndNames()
    {
        Assert.Same(SibnetExtractor.Instance, HostRegistry.ByHost("video.sibnet.ru"));
        Assert.Same(CdaExtractor.Instance, HostRegistry.ByHost("ebd.cda.pl"));
        Assert.Same(VkExtractor.Instance, HostRegistry.ByHost("vk.com"));
        Assert.Null(HostRegistry.ByHost("hgcloud.to"));
        Assert.NotNull(HostRegistry.ByHost("streamwish.to"));
        Assert.Same(JwPlayerExtractor.Instance, HostRegistry.ByPlayerName("Streamwish"));
        Assert.Same(CdaExtractor.Instance, HostRegistry.ByPlayerName("Cda"));
        Assert.Same(VidaraExtractor.Instance, HostRegistry.ByPlayerName("Vidara"));
        Assert.Same(YouruploadExtractor.Instance, HostRegistry.ByHost("www.yourupload.com"));
        Assert.Null(HostRegistry.ByPlayerName("Mega"));
        Assert.Null(HostRegistry.ByPlayerName("Voesx"));
    }

    [Fact]
    public async Task ExtractorFailuresAreProviderExceptions()
    {
        var handler = new HostsFakeHandler(_ => HostsFakeHandler.Ok("<html></html>"));
        var http = new HostHttp(new HttpClient(handler));
        await Assert.ThrowsAsync<ProviderException>(() => YouruploadExtractor.Instance.ExtractAsync("https://www.yourupload.com/embed/x", http));
    }
}
