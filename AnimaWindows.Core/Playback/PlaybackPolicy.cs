using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Playback;

/// <summary>Cadence constants of the Kotlin PlayerViewModel (TICK_MS, SAVE_EVERY_TICKS).</summary>
public static class ProgressCadence
{
    /// <summary>Position poll / skip evaluation interval.</summary>
    public const long TickMs = 250;

    /// <summary>Ticks between progress saves (20 ticks = 5 s).</summary>
    public const int SaveEveryTicks = 20;

    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(TickMs);
    public static readonly TimeSpan SaveInterval = TimeSpan.FromMilliseconds(TickMs * SaveEveryTicks);
}

/// <summary>Resume rule of <c>PlayerViewModel.resumePosition</c>.</summary>
public static class ResumePolicy
{
    /// <summary>Saved position to start from: 0 when there is no progress or it is finished (&gt;= 90%).</summary>
    public static long StartPositionMs(EpisodeProgress? progress) => progress is not null && !progress.IsFinished ? progress.PositionMs : 0L;
}

public enum PlayTargetKind
{
    Play,
    Continue,
    Replay,
}

/// <summary>Which episode the details screen's main button starts. Mirrors <c>PlayTarget</c> in feature/details.</summary>
public sealed record PlayTarget(int Index, PlayTargetKind Kind, string Episode)
{
    /// <summary>
    /// Resume like ani-cli: latest watched episode, or the next one if it was finished; Play (first episode) when nothing was
    /// watched; Replay when the last episode of the list was finished. Null when <paramref name="episodes"/> is empty.
    /// </summary>
    /// <param name="progress">Progress rows for this show (any order).</param>
    public static PlayTarget? Compute(IReadOnlyList<Episode> episodes, IEnumerable<EpisodeProgress> progress)
    {
        if (episodes.Count == 0) return null;
        EpisodeProgress? latest = null;
        foreach (var p in progress)
            if (latest is null || p.UpdatedAt > latest.UpdatedAt) latest = p;
        var idx = -1;
        if (latest is not null)
        {
            for (var i = 0; i < episodes.Count; i++)
            {
                if (episodes[i].Number == latest.EpisodeNumber)
                {
                    idx = i;
                    break;
                }
            }
        }

        if (latest is null || idx < 0) return new PlayTarget(0, PlayTargetKind.Play, episodes[0].Number);
        if (!latest.IsFinished) return new PlayTarget(idx, PlayTargetKind.Continue, episodes[idx].Number);
        if (idx + 1 < episodes.Count) return new PlayTarget(idx + 1, PlayTargetKind.Continue, episodes[idx + 1].Number);
        return new PlayTarget(idx, PlayTargetKind.Replay, episodes[idx].Number);
    }
}
