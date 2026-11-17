using AnimaWindows.Core.Models;

namespace AnimaWindows.App.Player;

/// <summary>The concrete playback backend in use.</summary>
public enum EngineKind
{
    Native,
    Mpv,
}

/// <summary>Pure engine-selection rules for <see cref="PlaybackEngineChoice"/> (Auto = native, falling back to mpv on failure).</summary>
public static class EngineSelection
{
    /// <summary>Engine to start with. <paramref name="sessionPreference"/> is what an earlier failover in this session settled on.</summary>
    public static EngineKind Initial(PlaybackEngineChoice choice, EngineKind? sessionPreference, bool mpvAvailable) => choice switch
    {
        PlaybackEngineChoice.Mpv => mpvAvailable ? EngineKind.Mpv : EngineKind.Native,
        PlaybackEngineChoice.Native => EngineKind.Native,
        _ => sessionPreference == EngineKind.Mpv && mpvAvailable ? EngineKind.Mpv : EngineKind.Native,
    };

    /// <summary>True when a failure of <paramref name="current"/> should switch to mpv (Auto, native, mpv available).</summary>
    public static bool ShouldFallBackToMpv(PlaybackEngineChoice choice, EngineKind current, bool mpvAvailable) =>
        choice == PlaybackEngineChoice.Auto && current == EngineKind.Native && mpvAvailable;

    public static EngineKind Resolve(PlaybackEngineChoice choice, bool mpvAvailable) => Initial(choice, null, mpvAvailable);
}
