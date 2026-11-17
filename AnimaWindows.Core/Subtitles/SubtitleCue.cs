namespace AnimaWindows.Core.Subtitles;

/// <summary>
/// One timed subtitle line. <see cref="Text"/> is normalised markup: plain text with <c>\n</c> line breaks and only the basic
/// <c>&lt;i&gt;</c>, <c>&lt;b&gt;</c> and <c>&lt;u&gt;</c> tags (see <see cref="CueMarkup"/>). Mirrors <c>SubtitleCue</c>.
/// </summary>
public sealed record SubtitleCue(long StartMs, long EndMs, string Text);

/// <summary>
/// Cue lookup by playback position over cues sorted by start time. Binary search plus a running maximum of end times, so
/// overlapping cues are found without scanning the whole list. Immutable and thread-safe.
/// </summary>
public sealed class CueTimeline
{
    private readonly SubtitleCue[] _cues;
    private readonly long[] _maxEndUpTo; // _maxEndUpTo[i] = max(EndMs of cues[0..i])

    /// <summary>Cues are sorted by start time (stable) on construction.</summary>
    public CueTimeline(IEnumerable<SubtitleCue> cues)
    {
        _cues = cues.OrderBy(c => c.StartMs).ToArray();
        _maxEndUpTo = new long[_cues.Length];
        long max = long.MinValue;
        for (var i = 0; i < _cues.Length; i++)
        {
            max = Math.Max(max, _cues[i].EndMs);
            _maxEndUpTo[i] = max;
        }
    }

    public int Count => _cues.Length;

    public IReadOnlyList<SubtitleCue> Cues => _cues;

    /// <summary>Index of the last cue whose start is at or before <paramref name="positionMs"/>, or -1.</summary>
    private int LastStartedAtOrBefore(long positionMs)
    {
        int lo = 0, hi = _cues.Length - 1, idx = -1;
        while (lo <= hi)
        {
            var mid = (int)(((uint)lo + (uint)hi) >> 1);
            if (_cues[mid].StartMs <= positionMs)
            {
                idx = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return idx;
    }

    /// <summary>All cues active at <paramref name="positionMs"/> (start inclusive, end exclusive), ordered by start time.</summary>
    public IReadOnlyList<SubtitleCue> ActiveAt(long positionMs)
    {
        var idx = LastStartedAtOrBefore(positionMs);
        if (idx < 0) return [];
        List<SubtitleCue>? result = null;
        for (var i = idx; i >= 0 && _maxEndUpTo[i] > positionMs; i--)
        {
            if (positionMs < _cues[i].EndMs) (result ??= []).Add(_cues[i]);
        }

        if (result is null) return [];
        result.Reverse();
        return result;
    }

    /// <summary>The single most recently started active cue (Kotlin <c>activeAt</c>), or null.</summary>
    public SubtitleCue? CurrentAt(long positionMs)
    {
        var idx = LastStartedAtOrBefore(positionMs);
        for (var i = idx; i >= 0 && _maxEndUpTo[i] > positionMs; i--)
            if (positionMs < _cues[i].EndMs) return _cues[i];
        return null;
    }
}
