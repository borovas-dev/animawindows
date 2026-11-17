#pragma warning disable xUnit1051
using System.Net;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tests.Tracking;

namespace AnimaWindows.Core.Tests.Downloads;

/// <summary>Port of DownloadPlanningTest.kt / DownloadPolicyTest.kt.</summary>
public sealed class DownloadPlanningTests
{
    private static readonly StreamVariant[] Variants =
    [
        new(1080, "https://cdn/1080.m3u8"),
        new(720, "https://cdn/720.m3u8"),
        new(360, "https://cdn/360.m3u8"),
    ];

    private static readonly StreamSet Set = new()
    {
        MasterUrl = "https://cdn/master.m3u8",
        Variants = Variants,
        Headers = new Dictionary<string, string> { ["Referer"] = "https://site/" },
        ExternalAudioUrl = "https://cdn/audio.m3u8",
        Subtitles =
        [
            new SubtitleTrack("https://cdn/en.vtt?x=1", "en", "English", true),
            new SubtitleTrack("https://cdn/en.vtt?x=1", "en", "dup", false),
            new SubtitleTrack("https://cdn/de.srt", "de", null, false),
        ],
    };

    [Theory]
    [InlineData(QualityPreference.Best, 1080)]
    [InlineData(QualityPreference.Worst, 360)]
    [InlineData(QualityPreference.P1080, 1080)]
    [InlineData(QualityPreference.P720, 720)]
    [InlineData(QualityPreference.P480, 360)] // nearest lower
    [InlineData(QualityPreference.P360, 360)]
    public void Plan_PicksVariantPerPreference(QualityPreference pref, int expected) =>
        Assert.Equal(expected, DownloadPlanner.CreatePlan(Set, pref)!.Quality);

    [Fact]
    public void Plan_WhenOnlyLowerThanRequestedExists_UsesIt() =>
        Assert.Equal(360, DownloadPlanner.CreatePlan(Set with { Variants = Variants[^1..] }, QualityPreference.P1080)!.Quality);

    [Fact]
    public void Plan_WhenTargetIsBelowAll_UsesBest()
    {
        var set = Set with { Variants = [new StreamVariant(720, "a"), new StreamVariant(480, "b")] };
        Assert.Equal(720, DownloadPlanner.CreatePlan(set, QualityPreference.P360)!.Quality);
    }

    [Fact]
    public void SelectVariant_HandlesUnknownQualities()
    {
        var unknown = new[] { new StreamVariant(null, "x"), new StreamVariant(null, "y") };
        Assert.Equal("x", DownloadPlanner.SelectVariant(unknown, QualityPreference.Best)!.Url);
        Assert.Equal("y", DownloadPlanner.SelectVariant(unknown, QualityPreference.Worst)!.Url);
        Assert.Equal("x", DownloadPlanner.SelectVariant(unknown, QualityPreference.P720)!.Url);
        Assert.Null(DownloadPlanner.SelectVariant([], QualityPreference.Best));
    }

    [Fact]
    public void Plan_SnapshotIsSingleRenditionWithoutMaster_AndKeepsSidecars()
    {
        var plan = DownloadPlanner.CreatePlan(Set, QualityPreference.P720)!;
        Assert.Null(plan.Snapshot.MasterUrl);
        Assert.Equal(new[] { "https://cdn/720.m3u8" }, plan.Snapshot.Variants.Select(v => v.Url));
        Assert.Equal(Set.Headers, plan.Snapshot.Headers);
        Assert.Equal(Set.ExternalAudioUrl, plan.Snapshot.ExternalAudioUrl);
        Assert.Equal(Set.Subtitles, plan.Snapshot.Subtitles);
    }

    [Fact]
    public void Plan_FallsBackToMaster_AndFailsWhenEmpty()
    {
        var master = DownloadPlanner.CreatePlan(Set with { Variants = [] }, QualityPreference.Best)!;
        Assert.Equal("https://cdn/master.m3u8", master.Snapshot.MasterUrl);
        Assert.Null(master.Quality);
        Assert.Null(DownloadPlanner.CreatePlan(Set with { Variants = [], MasterUrl = null }, QualityPreference.Best));
    }

    [Fact]
    public void Parts_AreMainThenAudioThenDedupedSubtitles()
    {
        var key = DownloadIds.EpisodeKey("hianime", "a", "1");
        var parts = DownloadPlanner.Parts(key, DownloadPlanner.CreatePlan(Set, QualityPreference.P720)!.Snapshot);
        Assert.Equal(new[] { key, key + "#audio", key + "#sub0", key + "#sub1" }, parts.Select(p => p.Id));
        Assert.Equal(new[] { true, true, false, false }, parts.Select(p => p.IsHls));
        Assert.True(DownloadIds.IsMain(parts[0].Id));
        Assert.False(DownloadIds.IsMain(parts[1].Id));
        Assert.All(parts, p => Assert.Equal(key, DownloadIds.KeyOf(p.Id)));
        Assert.Equal(new[] { "main", "audio", "sub0", "sub1" }, parts.Select(p => DownloadIds.PartName(p.Id)));
        Assert.Empty(DownloadPlanner.Parts(key, new StreamSet()));
    }

    [Theory]
    [InlineData("https://cdn/a.m3u8", true)]
    [InlineData("https://cdn/a.M3U8?token=1#x", true)]
    [InlineData("https://cdn/a.mp4", false)]
    [InlineData("https://cdn/a.mp4?x=.m3u8", false)]
    public void IsHls(string url, bool expected) => Assert.Equal(expected, DownloadPlanner.IsHls(url));

    [Fact]
    public void Ids_RoundTrip()
    {
        var key = DownloadIds.EpisodeKey("hianime", "slug-123", "12.5");
        Assert.Equal(("hianime", "slug-123", "12.5"), DownloadIds.Parse(DownloadIds.KeyOf(key)));
        Assert.Null(DownloadIds.Parse("nope"));
    }

    [Fact]
    public void SafeSegment_NeverCollidesAndStaysAFileName()
    {
        Assert.Equal("12.5", DownloadIds.SafeSegment("12.5"));
        var a = DownloadIds.SafeSegment("a/b");
        var b = DownloadIds.SafeSegment("a_b");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain(':', DownloadIds.SafeSegment("c:d"));
        Assert.True(DownloadIds.SafeSegment(new string('x', 300)).Length < 100);
        Assert.NotEmpty(DownloadIds.SafeSegment(""));
    }

    [Fact]
    public async Task PlanAsync_ResolvesThroughChain_AndPicksQuality()
    {
        var cdn = new FakeCdn();
        var chain = new FakeChain { Resolve = (_, _, _) => Media.SetFor(cdn, "e1", 2) };
        var planner = new DownloadPlanner(chain);
        var plan = await planner.PlanAsync(Media.Show(), new Episode("e1", "1"), TranslationMode.Sub, QualityPreference.P720);
        Assert.Equal(720, plan.Quality);
        Assert.Equal("https://cdn.test/e1/720/index.m3u8", plan.Snapshot.Variants.Single().Url);
        Assert.Equal(1, chain.Calls);
    }

    [Fact]
    public async Task PlanAsync_Throws_WhenNothingDownloadable()
    {
        var planner = new DownloadPlanner(new FakeChain { Resolve = (_, _, _) => new StreamSet() });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => planner.PlanAsync(Media.Show(), new Episode("e", "1"), TranslationMode.Sub, QualityPreference.Best));
    }

    [Fact]
    public async Task PlanAsync_PicksRenditionFromMasterPlaylist()
    {
        var cdn = new FakeCdn();
        cdn.Put(
            "/m/master.m3u8",
            "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=3000000,RESOLUTION=1920x1080\n1080/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=1500000,RESOLUTION=1280x720\n720/index.m3u8\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=500000,RESOLUTION=640x360\nhttps://other.test/360.m3u8\n");
        var chain = new FakeChain
        {
            Resolve = (_, _, _) => new StreamSet { MasterUrl = "https://cdn.test/m/master.m3u8", Headers = new Dictionary<string, string> { ["Referer"] = "r" } },
        };
        var planner = new DownloadPlanner(chain, new HttpClient(cdn));

        var p720 = await planner.PlanAsync(Media.Show(), new Episode("e", "1"), TranslationMode.Sub, QualityPreference.P720);
        Assert.Equal(720, p720.Quality);
        Assert.Equal("https://cdn.test/m/720/index.m3u8", p720.Snapshot.Variants.Single().Url);
        Assert.Null(p720.Snapshot.MasterUrl);
        Assert.Equal("r", cdn.Requests.First().Headers["Referer"]);

        var worst = await planner.PlanAsync(Media.Show(), new Episode("e", "1"), TranslationMode.Sub, QualityPreference.Worst);
        Assert.Equal("https://other.test/360.m3u8", worst.Snapshot.Variants.Single().Url);
    }

    // ---- policy ----

    [Fact]
    public void Backoff_Is30s_2min_8min_ThenCapped()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), DownloadPolicy.Backoff(1));
        Assert.Equal(TimeSpan.FromMinutes(2), DownloadPolicy.Backoff(2));
        Assert.Equal(TimeSpan.FromMinutes(8), DownloadPolicy.Backoff(3));
        Assert.Equal(TimeSpan.FromMinutes(10), DownloadPolicy.Backoff(4));
        Assert.Equal(TimeSpan.FromMinutes(10), DownloadPolicy.Backoff(50));
        Assert.Equal(TimeSpan.FromSeconds(30), DownloadPolicy.Backoff(0));
    }

    [Fact]
    public void ShouldRetry_StopsAfterThreeRetries()
    {
        Assert.True(DownloadPolicy.ShouldRetry(0));
        Assert.True(DownloadPolicy.ShouldRetry(2));
        Assert.False(DownloadPolicy.ShouldRetry(3));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.Gone, true)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void IsExpiredLink_OnlyForAuthAndMissingCodes(HttpStatusCode status, bool expected)
    {
        var wrapped = new IOException("outer", new HttpRequestException("x", null, status));
        Assert.Equal(expected, DownloadPolicy.IsExpiredLink(wrapped));
        Assert.False(DownloadPolicy.IsExpiredLink(null));
        Assert.False(DownloadPolicy.IsExpiredLink(new IOException("disk")));
    }

    [Fact]
    public void DescribeFailure_IsShortAndSpecific()
    {
        static HttpRequestException Http(HttpStatusCode c) => new("x", null, c);
        Assert.Contains("link may have expired", DownloadPolicy.DescribeFailure(Http(HttpStatusCode.Forbidden)));
        Assert.Equal("Video not found on server (HTTP 404)", DownloadPolicy.DescribeFailure(Http(HttpStatusCode.NotFound)));
        Assert.Equal("Server error (HTTP 503)", DownloadPolicy.DescribeFailure(Http(HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("Server returned HTTP 418", DownloadPolicy.DescribeFailure(Http((HttpStatusCode)418)));
        Assert.Equal("Could not reach the server", DownloadPolicy.DescribeFailure(new HttpRequestException("dns")));
        Assert.Equal("Connection timed out", DownloadPolicy.DescribeFailure(new TaskCanceledException("t")));
        Assert.Equal("Download failed: disk full", DownloadPolicy.DescribeFailure(new IOException("disk full")));
        Assert.Equal("Download failed", DownloadPolicy.DescribeFailure(null));
        Assert.Equal("Download failed", DownloadPolicy.DescribeFailure(new Exception("")));
    }
}
