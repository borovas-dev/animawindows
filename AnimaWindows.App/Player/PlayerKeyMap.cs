using Windows.System;

namespace AnimaWindows.App.Player;

/// <summary>What a key press means for the player (port of <c>PlayerKeyAction</c> in PlayerKeys.kt plus Windows norms).</summary>
public enum PlayerKeyAction
{
    PlayPause,
    SeekBack,
    SeekForward,
    VolumeUp,
    VolumeDown,
    ToggleFullscreen,
    ToggleMute,
    ExitFullscreen,
    Next,
    Previous,
    SkipSegment,
    CycleSubtitles,
}

/// <summary>Pure key mapping, free of XAML state.</summary>
public static class PlayerKeyMap
{
    public static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(10);

    /// <summary>Volume change per key press / wheel notch.</summary>
    public const double VolumeStep = 0.05;

    /// <summary>
    /// Space/K play-pause, J/Left back 10 s, L/Right forward 10 s, Up/Down volume, M mute, F/F11 fullscreen, Esc leaves fullscreen
    /// or compact overlay, N next, P previous, S skip segment, C cycle subtitles. Returns null when the key is not ours
    /// (or a Ctrl/Alt/Win modifier is held).
    /// </summary>
    public static PlayerKeyAction? Map(VirtualKey key, bool ctrlAltOrWin, bool escapeIsUseful)
    {
        if (ctrlAltOrWin) return null;
        return key switch
        {
            VirtualKey.Space or VirtualKey.K or VirtualKey.GamepadA => PlayerKeyAction.PlayPause,
            VirtualKey.Left or VirtualKey.J => PlayerKeyAction.SeekBack,
            VirtualKey.Right or VirtualKey.L => PlayerKeyAction.SeekForward,
            VirtualKey.Up => PlayerKeyAction.VolumeUp,
            VirtualKey.Down => PlayerKeyAction.VolumeDown,
            VirtualKey.F or VirtualKey.F11 => PlayerKeyAction.ToggleFullscreen,
            VirtualKey.M => PlayerKeyAction.ToggleMute,
            VirtualKey.Escape => escapeIsUseful ? PlayerKeyAction.ExitFullscreen : null,
            VirtualKey.N => PlayerKeyAction.Next,
            VirtualKey.P => PlayerKeyAction.Previous,
            VirtualKey.S => PlayerKeyAction.SkipSegment,
            VirtualKey.C => PlayerKeyAction.CycleSubtitles,
            _ => null,
        };
    }
}
