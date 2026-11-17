namespace AnimaWindows.App.Player;

/// <summary>
/// Implemented by engines whose video lives in a native child window that XAML cannot draw over (mpv). Such engines show their
/// own controls and subtitles, so the app hides its transport bar and subtitle overlay and talks to them through this contract.
/// </summary>
public interface IEmbeddedUiEngine
{
    /// <summary>Window/OSD title (<c>force-media-title</c>); set before <c>LoadAsync</c>.</summary>
    string? MediaTitle { get; set; }

    /// <summary>Messages sent by the engine's own key bindings: <c>next</c>, <c>prev</c>, <c>skip</c>, <c>fullscreen</c>, <c>escape</c>.</summary>
    event EventHandler<string>? ScriptMessage;

    /// <summary>Shows a transient on-screen message.</summary>
    void ShowText(string text, TimeSpan duration);

    /// <summary>
    /// Shows the subtitle file at <paramref name="source"/> (URL or local path) rendered by the engine, or none when null.
    /// Network sources use the headers of the current load.
    /// </summary>
    void SetSubtitle(string? source);
}
