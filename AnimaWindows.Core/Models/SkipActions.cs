namespace AnimaWindows.Core.Models;

/// <summary>
/// What the player should do for the current position. Mirrors <c>SkipAction</c> in feature/aniskip/SkipController.kt.
/// </summary>
public abstract record SkipAction
{
    private SkipAction() { }

    public sealed record None : SkipAction
    {
        public static None Instance { get; } = new();
    }

    /// <summary>Seek to <paramref name="TargetMs"/> immediately (with an undo prompt).</summary>
    public sealed record AutoSeek(long TargetMs, SkipSegment Segment) : SkipAction;

    /// <summary>Show a skip button that seeks to <paramref name="TargetMs"/>.</summary>
    public sealed record ShowButton(SkipSegment Segment, long TargetMs) : SkipAction;
}
