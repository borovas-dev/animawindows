using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.AniSkip;

/// <summary>
/// Port of ani-skip's mpv.lua skip logic (feature/aniskip SkipController). Pure and not thread-safe; drive it from one thread
/// (the player tick) with the current position in seconds.
/// </summary>
public sealed class SkipController
{
    private readonly SkipTimes? _times;
    private readonly int _offsetSec;
    private readonly SkipBehavior _behavior;
    private readonly bool _autoOpening;
    private readonly bool _autoEnding;

    private bool _autoDoneOp;
    private bool _autoDoneEd;
    private bool _skippedOp;
    private bool _skippedEd;
    private bool _enabled = true;
    private double _lastPos = double.NaN;

    /// <param name="offsetSec">0..5 seconds subtracted from the segment end (clamped).</param>
    /// <param name="autoOpening">Per-segment opt-in to auto-seek regardless of <paramref name="behavior"/>; an auto-skipped segment is never re-armed by seeking back.</param>
    public SkipController(SkipTimes? times, int offsetSec, SkipBehavior behavior, bool autoOpening = false, bool autoEnding = false)
    {
        _times = times;
        _offsetSec = Math.Clamp(offsetSec, 0, 5);
        _behavior = behavior;
        _autoOpening = autoOpening;
        _autoEnding = autoEnding;
    }

    public void Reset()
    {
        _skippedOp = false;
        _skippedEd = false;
        _autoDoneOp = false;
        _autoDoneEd = false;
        _lastPos = double.NaN;
    }

    public void SetEnabled(bool value)
    {
        if (value && !_enabled) Reset();
        _enabled = value;
    }

    /// <summary>Call when the user used the button so it is not shown again for the section.</summary>
    public void MarkSkipped(SkipSegment segment)
    {
        if (segment == SkipSegment.Opening) _skippedOp = true;
        else _skippedEd = true;
    }

    public SkipAction OnPosition(double posSec)
    {
        var prev = _lastPos;
        _lastPos = posSec;
        var t = _times;
        if (!_enabled || t is null) return SkipAction.None.Instance;
        // A backward seek before a section start re-arms it.
        if (!double.IsNaN(prev) && posSec < prev)
        {
            if (t.Opening is { } o && posSec < o.StartSec && !_autoDoneOp) _skippedOp = false;
            if (t.Ending is { } e && posSec < e.StartSec && !_autoDoneEd) _skippedEd = false;
        }

        return Evaluate(t.Opening, SkipSegment.Opening, posSec, _skippedOp)
               ?? Evaluate(t.Ending, SkipSegment.Ending, posSec, _skippedEd)
               ?? SkipAction.None.Instance;
    }

    private SkipAction? Evaluate(SkipInterval? i, SkipSegment seg, double t, bool skipped)
    {
        if (i is null || skipped) return null;
        var target = i.EndSec - _offsetSec;
        if (t < i.StartSec || t >= target) return null;
        var ms = (long)(target * 1000);
        var flagged = seg == SkipSegment.Opening ? _autoOpening : _autoEnding;
        var auto = _behavior == SkipBehavior.Auto || flagged;
        if (!auto) return new SkipAction.ShowButton(seg, ms);
        MarkSkipped(seg);
        if (flagged)
        {
            if (seg == SkipSegment.Opening) _autoDoneOp = true;
            else _autoDoneEd = true;
        }

        return new SkipAction.AutoSeek(ms, seg);
    }

    /// <summary>
    /// Combines AniSkip <paramref name="primary"/> data with provider-supplied intro/outro: AniSkip wins per segment, the provider
    /// values fill the gaps. Null when nothing is known. Port of <c>mergeSkipTimes</c>.
    /// </summary>
    public static SkipTimes? Merge(SkipTimes? primary, SkipInterval? providerIntro, SkipInterval? providerOutro)
    {
        var op = primary?.Opening ?? providerIntro;
        var ed = primary?.Ending ?? providerOutro;
        return op is null && ed is null ? null : new SkipTimes(op, ed);
    }
}

/// <summary>
/// "Skipped intro - Undo" prompt state after an automatic skip (Kotlin PlayerViewModel.autoSkipped/undoPositionMs plus the 5 s
/// auto-dismiss in AutoSkipUndo). Time-driven: pass the current time (e.g. from IClock) to every call.
/// </summary>
public sealed class AutoSkipUndo
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    private SkipSegment? _segment;
    private long _positionMs;
    private DateTimeOffset _at;

    /// <summary>Records an <see cref="SkipAction.AutoSeek"/> issued while playback was at <paramref name="fromPositionMs"/>.</summary>
    public void OnAutoSkip(SkipSegment segment, long fromPositionMs, DateTimeOffset now)
    {
        _segment = segment;
        _positionMs = fromPositionMs;
        _at = now;
    }

    /// <summary>Segment whose undo prompt should be visible now, or null (none or expired).</summary>
    public SkipSegment? Visible(DateTimeOffset now)
    {
        if (_segment is null) return null;
        if (now - _at >= Window)
        {
            _segment = null;
            return null;
        }

        return _segment;
    }

    /// <summary>Position to seek back to when the user taps Undo within the window, else null. Clears the prompt.</summary>
    public long? Undo(DateTimeOffset now)
    {
        if (Visible(now) is null) return null;
        _segment = null;
        return _positionMs;
    }

    public void Dismiss() => _segment = null;
}
