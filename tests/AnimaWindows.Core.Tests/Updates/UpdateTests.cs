#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Net;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tests.Net;
using AnimaWindows.Core.Updates;

namespace AnimaWindows.Core.Tests.Updates;

public class UpdateTests
{
    [Fact]
    public void SemverCompare()
    {
        Assert.True(SemVer.IsNewer("v0.2.0", "0.1.0"));
        Assert.True(SemVer.IsNewer("1.0.0", "0.9.9"));
        Assert.True(SemVer.IsNewer("0.1.10", "0.1.9"));
        Assert.False(SemVer.IsNewer("0.1.0", "0.1.0"));
        Assert.False(SemVer.IsNewer("v0.1", "0.1.0"));
        Assert.False(SemVer.IsNewer("0.0.9", "0.1.0"));
        Assert.True(SemVer.IsNewer("1.0.0", "1.0.0-rc.1"));
        Assert.False(SemVer.IsNewer("1.0.0-rc.1", "1.0.0"));
        Assert.True(SemVer.IsNewer("1.0.0-rc.2", "1.0.0-rc.1"));
        Assert.True(SemVer.IsNewer("1.0.0-rc.10", "1.0.0-rc.9"));
        Assert.Equal(SemVer.Parse("1.2.3"), SemVer.Parse("v1.2.3+build5"));
    }

    [Fact]
    public void SemverGarbage()
    {
        Assert.Null(SemVer.Parse("latest"));
        Assert.Null(SemVer.Parse(""));
        Assert.False(SemVer.IsNewer("nightly", "0.1.0"));
        Assert.False(SemVer.IsNewer("1.0.0", "dev"));
    }

    private static string Release(string tag, bool pkg = true, bool pre = false)
    {
        var asset = pkg ? ",{\"name\":\"app.msix\",\"browser_download_url\":\"https://gh/app.msix\"}" : "";
        return "{\"tag_name\":\"" + tag + "\",\"body\":\"- fixes\",\"html_url\":\"https://gh/r\",\"draft\":false," +
               "\"prerelease\":" + (pre ? "true" : "false") + ",\"unknown\":1,\"assets\":[{\"name\":\"notes.txt\",\"browser_download_url\":\"https://gh/n\"}" + asset + "]}";
    }

    [Fact]
    public void ParseNewer()
    {
        var s = Assert.IsType<UpdateState.Available>(UpdateChecker.ParseRelease(Release("v0.2.0"), "0.1.0"));
        Assert.Equal("0.2.0", s.Version);
        Assert.Equal("- fixes", s.Changelog);
        Assert.Equal("https://gh/app.msix", s.PackageUrl);
        Assert.Equal("https://gh/r", s.PageUrl);
    }

    [Fact]
    public void ParseIgnores()
    {
        Assert.IsType<UpdateState.UpToDate>(UpdateChecker.ParseRelease(Release("v0.1.0"), "0.1.0"));
        Assert.IsType<UpdateState.UpToDate>(UpdateChecker.ParseRelease(Release("v0.2.0", pkg: false), "0.1.0"));
        Assert.IsType<UpdateState.UpToDate>(UpdateChecker.ParseRelease(Release("v0.2.0", pre: true), "0.1.0"));
    }

    [Fact]
    public void PrereleaseList()
    {
        var list = "[" + string.Join(",", Release("v0.3.0-alpha1", pre: true), Release("v0.2.0"), Release("v0.3.0-alpha2", pre: true)) + "]";
        static string V(UpdateState s) => Assert.IsType<UpdateState.Available>(s).Version;
        Assert.Equal("0.2.0", V(UpdateChecker.ParseReleaseList(list, "0.1.0", false)));
        Assert.Equal("0.3.0-alpha2", V(UpdateChecker.ParseReleaseList(list, "0.1.0", true)));
        Assert.Equal("0.3.0-alpha2", V(UpdateChecker.ParseReleaseList(list, "0.3.0-alpha1", true)));
        Assert.IsType<UpdateState.UpToDate>(UpdateChecker.ParseReleaseList(list, "0.3.0-alpha2", true));
        Assert.Equal("0.2.0", V(UpdateChecker.ParseReleaseList(list, "0.2.0-alpha1", false)));
    }

    [Fact]
    public async Task CheckLatest()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, Release("v0.2.0"));
        var c = new UpdateChecker(new HttpClient(h), "0.1.0", "o/r", "https://api.test");
        var seen = new List<UpdateState>();
        c.StateChanged += (_, s) => seen.Add(s);
        Assert.IsType<UpdateState.Idle>(c.State);
        await c.CheckAsync();
        Assert.IsType<UpdateState.Available>(c.State);
        Assert.IsType<UpdateState.Checking>(seen[0]);
        Assert.Equal("https://api.test/repos/o/r/releases/latest", h.Requests[0].Uri.ToString());
        c.Dismiss();
        Assert.IsType<UpdateState.Idle>(c.State);
    }

    [Fact]
    public async Task CheckWithPrereleasesUsesListEndpoint()
    {
        var h = new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "[" + Release("v0.3.0-alpha1", pre: true) + "]");
        var c = new UpdateChecker(new HttpClient(h), "0.1.0", "o/r", "https://api.test/");
        await c.CheckAsync(includePrereleases: true);
        Assert.Equal("https://api.test/repos/o/r/releases?per_page=30", h.Requests[0].Uri.ToString());
        Assert.IsType<UpdateState.Available>(c.State);
    }

    [Fact]
    public async Task NoReleasesIsUpToDateAndErrorsFail()
    {
        var c = new UpdateChecker(new HttpClient(new FakeHttpMessageHandler().Enqueue(HttpStatusCode.NotFound)), "0.1.0");
        await c.CheckAsync();
        Assert.IsType<UpdateState.UpToDate>(c.State);

        var c2 = new UpdateChecker(new HttpClient(new FakeHttpMessageHandler().Enqueue(HttpStatusCode.InternalServerError)), "0.1.0");
        await c2.CheckAsync();
        Assert.Equal(new UpdateState.Failed("HTTP 500"), c2.State);

        var c3 = new UpdateChecker(new HttpClient(new FakeHttpMessageHandler().Enqueue(HttpStatusCode.OK, "garbage")), "0.1.0");
        await c3.CheckAsync();
        Assert.IsType<UpdateState.Failed>(c3.State);
    }
}
