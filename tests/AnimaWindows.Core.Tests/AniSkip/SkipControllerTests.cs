#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using AnimaWindows.Core.AniSkip;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tests.AniSkip;

public class SkipControllerTests
{
    private static readonly SkipTimes Times = new(new SkipInterval(10.0, 90.0), new SkipInterval(1300.0, 1390.0));
    private static readonly SkipAction None = SkipAction.None.Instance;

    [Fact]
    public void AutoSeeksOnceWithOffset()
    {
        var c = new SkipController(Times, 2, SkipBehavior.Auto);
        Assert.Equal(None, c.OnPosition(5.0));
        Assert.Equal(new SkipAction.AutoSeek(88_000, SkipSegment.Opening), c.OnPosition(10.0));
        Assert.Equal(None, c.OnPosition(20.0));
    }

    [Fact]
    public void MergePrefersAniSkipPerSegmentAndFallsBack()
    {
        var intro = new SkipInterval(31.0, 111.0);
        var outro = new SkipInterval(1376.0, 1447.0);
        Assert.Equal(new SkipTimes(intro, outro), SkipController.Merge(null, intro, outro));
        Assert.Equal(new SkipTimes(Times.Opening, outro), SkipController.Merge(new SkipTimes(Times.Opening, null), intro, outro));
        Assert.Equal(Times, SkipController.Merge(Times, intro, outro));
        Assert.Null(SkipController.Merge(null, null, null));
    }

    [Fact]
    public void ButtonKeepsShowingUntilMarked()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Button);
        Assert.Equal(new SkipAction.ShowButton(SkipSegment.Opening, 90_000), c.OnPosition(11.0));
        Assert.Equal(new SkipAction.ShowButton(SkipSegment.Opening, 90_000), c.OnPosition(12.0));
        c.MarkSkipped(SkipSegment.Opening);
        Assert.Equal(None, c.OnPosition(13.0));
        Assert.Equal(None, c.OnPosition(95.0));
    }

    [Fact]
    public void EndingAndReset()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Auto);
        Assert.Equal(new SkipAction.AutoSeek(1_390_000, SkipSegment.Ending), c.OnPosition(1301.0));
        c.Reset();
        Assert.Equal(new SkipAction.AutoSeek(1_390_000, SkipSegment.Ending), c.OnPosition(1301.0));
    }

    [Fact]
    public void BackwardSeekRearms()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Auto);
        c.OnPosition(11.0);
        Assert.Equal(None, c.OnPosition(100.0));
        Assert.Equal(None, c.OnPosition(3.0));
        Assert.Equal(new SkipAction.AutoSeek(90_000, SkipSegment.Opening), c.OnPosition(10.5));
    }

    [Fact]
    public void DisabledAndNullTimes()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Auto);
        c.SetEnabled(false);
        Assert.Equal(None, c.OnPosition(20.0));
        c.SetEnabled(true);
        Assert.Equal(new SkipAction.AutoSeek(90_000, SkipSegment.Opening), c.OnPosition(20.0));
        Assert.Equal(None, new SkipController(null, 0, SkipBehavior.Auto).OnPosition(20.0));
    }

    [Fact]
    public void PerSegmentAutoSkipWithButtonBehavior()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Button, autoOpening: true);
        Assert.Equal(new SkipAction.AutoSeek(90_000, SkipSegment.Opening), c.OnPosition(11.0));
        Assert.Equal(new SkipAction.ShowButton(SkipSegment.Ending, 1_390_000), c.OnPosition(1301.0));
    }

    [Fact]
    public void AutoSkipNotRepeatedAfterSeekingBack()
    {
        var c = new SkipController(Times, 0, SkipBehavior.Button, autoOpening: true);
        Assert.Equal(new SkipAction.AutoSeek(90_000, SkipSegment.Opening), c.OnPosition(11.0));
        Assert.Equal(None, c.OnPosition(91.0));
        Assert.Equal(None, c.OnPosition(5.0));
        Assert.Equal(None, c.OnPosition(12.0));
        c.Reset();
        Assert.Equal(new SkipAction.AutoSeek(90_000, SkipSegment.Opening), c.OnPosition(12.0));
    }

    [Fact]
    public void MissingSegmentDoesNothing()
    {
        var c = new SkipController(new SkipTimes(null, Times.Ending), 0, SkipBehavior.Button, autoOpening: true, autoEnding: true);
        Assert.Equal(None, c.OnPosition(11.0));
        Assert.Equal(new SkipAction.AutoSeek(1_390_000, SkipSegment.Ending), c.OnPosition(1301.0));
    }

    [Fact]
    public void OffsetIsClampedToFiveSeconds()
    {
        var c = new SkipController(Times, 99, SkipBehavior.Auto);
        Assert.Equal(new SkipAction.AutoSeek(85_000, SkipSegment.Opening), c.OnPosition(11.0));
    }

    [Fact]
    public void UndoWindowLastsFiveSeconds()
    {
        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var u = new AutoSkipUndo();
        Assert.Null(u.Visible(t0));
        u.OnAutoSkip(SkipSegment.Opening, 11_000, t0);
        Assert.Equal(SkipSegment.Opening, u.Visible(t0.AddSeconds(4.9)));
        Assert.Equal(11_000L, u.Undo(t0.AddSeconds(4.9)));
        Assert.Null(u.Visible(t0.AddSeconds(4.9)));

        u.OnAutoSkip(SkipSegment.Ending, 1_301_000, t0);
        Assert.Null(u.Undo(t0.AddSeconds(5)));
        Assert.Null(u.Visible(t0.AddSeconds(5)));
    }
}
