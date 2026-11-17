#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Playback;

namespace AnimaWindows.Core.Tests.Playback;

public class QualitySelectorTests
{
    private static readonly StreamVariant[] V =
    [
        new(1080, "u1080"), new(720, "u720"), new(360, "u360"), new(null, "unknown"),
    ];

    private static StreamSet Set(string? master = "master", IReadOnlyList<StreamVariant>? variants = null) =>
        new() { MasterUrl = master, Variants = variants ?? V };

    [Fact]
    public void BestUsesMaster()
    {
        Assert.Equal("master", QualitySelector.ResolveUrl(Set(), QualityPreference.Best));
        Assert.Equal("u1080", QualitySelector.ResolveUrl(Set(master: null), QualityPreference.Best));
    }

    [Fact]
    public void WorstPicksLowestKnown() => Assert.Equal("u360", QualitySelector.ResolveUrl(Set(), QualityPreference.Worst));

    [Fact]
    public void ExactMatch() => Assert.Equal("u720", QualitySelector.ResolveUrl(Set(), QualityPreference.P720));

    [Fact]
    public void NearestLowerThenBest()
    {
        Assert.Equal("u360", QualitySelector.ResolveUrl(Set(), QualityPreference.P480));
        Assert.Equal("u1080", QualitySelector.ResolveUrl(Set(variants: [new StreamVariant(1080, "u1080")]), QualityPreference.P360));
    }

    [Fact]
    public void ExplicitHeightOverridesBest()
    {
        Assert.False(QualitySelector.UseMaster(Set(), QualityPreference.Best, 720));
        Assert.Equal("u720", QualitySelector.ResolveUrl(Set(), QualityPreference.Best, 720));
    }

    [Fact]
    public void OnlyMasterAndEmpty()
    {
        Assert.True(QualitySelector.UseMaster(Set(variants: []), QualityPreference.P720));
        Assert.Equal("master", QualitySelector.ResolveUrl(Set(variants: []), QualityPreference.P720));
        Assert.Null(QualitySelector.ResolveUrl(Set(master: null, variants: []), QualityPreference.Best));
    }

    [Fact]
    public void SubtitleMime()
    {
        Assert.Equal("text/vtt", QualitySelector.SubtitleMimeType("http://x/a.vtt?x=1"));
        Assert.Equal("application/x-subrip", QualitySelector.SubtitleMimeType("http://x/a.SRT"));
        Assert.Equal("text/x-ssa", QualitySelector.SubtitleMimeType("http://x/a.ass"));
    }
}

public class PlaybackPolicyTests
{
    private static EpisodeProgress P(string ep, long pos, long dur, long at) => new("p", "a", ep, pos, dur, at);

    [Fact]
    public void ResumeRule()
    {
        Assert.Equal(0L, ResumePolicy.StartPositionMs(null));
        Assert.Equal(500_000L, ResumePolicy.StartPositionMs(P("1", 500_000, 1_000_000, 1)));
        Assert.Equal(0L, ResumePolicy.StartPositionMs(P("1", 900_000, 1_000_000, 1))); // finished at 90%
        Assert.Equal(899_999L, ResumePolicy.StartPositionMs(P("1", 899_999, 1_000_000, 1)));
    }

    [Fact]
    public void CadenceConstants()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), ProgressCadence.SaveInterval);
        Assert.Equal(250, ProgressCadence.TickMs);
    }

    private static readonly Episode[] Eps = [new("e1", "1"), new("e2", "2"), new("e3", "3")];

    [Fact]
    public void PlayTargetRule()
    {
        Assert.Null(PlayTarget.Compute([], []));
        Assert.Equal(new PlayTarget(0, PlayTargetKind.Play, "1"), PlayTarget.Compute(Eps, []));
        Assert.Equal(new PlayTarget(0, PlayTargetKind.Play, "1"), PlayTarget.Compute(Eps, [P("99", 1, 10, 5)])); // unknown episode
        Assert.Equal(new PlayTarget(1, PlayTargetKind.Continue, "2"), PlayTarget.Compute(Eps, [P("1", 1000, 1000, 1), P("2", 10, 1000, 2)]));
        Assert.Equal(new PlayTarget(2, PlayTargetKind.Continue, "3"), PlayTarget.Compute(Eps, [P("2", 950, 1000, 2)]));
        Assert.Equal(new PlayTarget(2, PlayTargetKind.Replay, "3"), PlayTarget.Compute(Eps, [P("3", 1000, 1000, 9)]));
    }
}

public class NextEpisodePrefetcherTests
{
    private long _now;
    private readonly NextEpisodePrefetcher<string> _p;

    public NextEpisodePrefetcherTests() => _p = new NextEpisodePrefetcher<string>(() => _now);

    private bool Start(long pos, bool enabled = true, bool metered = false, bool allow = false, string? key = "e2") =>
        _p.ShouldStart(key, enabled, metered, allow, pos, 1000);

    [Fact]
    public void StartsOnlyPastEightyPercent()
    {
        Assert.False(Start(799));
        Assert.True(Start(800));
    }

    [Fact]
    public void DisabledOrNoNextNeverStarts()
    {
        Assert.False(Start(900, enabled: false));
        Assert.False(Start(900, key: null));
        Assert.False(_p.ShouldStart("e2", true, false, false, 900, 0));
    }

    [Fact]
    public void MeteredNeedsOptIn()
    {
        Assert.False(Start(900, metered: true));
        Assert.True(Start(900, metered: true, allow: true));
    }

    [Fact]
    public void AttemptedAndCachedAreNotRestarted()
    {
        _p.MarkAttempted("e2");
        Assert.False(Start(900));
        _p.Clear();
        _p.Put("e2", "set");
        Assert.False(Start(900));
    }

    [Fact]
    public void TakeReturnsFreshOnceThenNull()
    {
        _p.Put("e2", "set");
        _now = 9 * 60_000L;
        Assert.Equal("set", _p.Take("e2"));
        Assert.Null(_p.Take("e2"));
    }

    [Fact]
    public void ExpiresAfterTenMinutes()
    {
        _p.Put("e2", "set");
        _now = 10 * 60_000L;
        Assert.Null(_p.Take("e2"));
    }

    [Fact]
    public void ExpiredEntryAllowsNewStart()
    {
        _p.Put("e2", "set");
        _now = 11 * 60_000L;
        Assert.True(Start(900));
    }
}
