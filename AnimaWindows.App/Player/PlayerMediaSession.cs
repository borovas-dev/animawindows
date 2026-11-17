using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using Windows.System.Display;

namespace AnimaWindows.App.Player;

/// <summary>
/// Windows integration of a playback session, identical for both engines: System Media Transport Controls (title, episode,
/// cover, play/pause/next/previous) driven manually through a never-playing <see cref="MediaPlayer"/> that only hosts the SMTC
/// (the video player's own <c>CommandManager</c> is disabled), and a <see cref="DisplayRequest"/> that keeps the screen awake
/// while playing.
/// </summary>
public sealed class PlayerMediaSession : IDisposable
{
    private readonly MediaPlayer _host = new();
    private readonly SystemMediaTransportControls _smtc;
    private readonly DisplayRequest _display = new();
    private bool _awake;
    private bool _disposed;

    public PlayerMediaSession()
    {
        _host.CommandManager.IsEnabled = false;
        _smtc = _host.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = false;
        _smtc.ButtonPressed += OnButtonPressed;
    }

    /// <summary>Raised on an arbitrary thread when the user presses a media key or SMTC flyout button.</summary>
    public event EventHandler<SystemMediaTransportControlsButton>? ButtonPressed;

    /// <summary>Publishes what is playing.</summary>
    public void SetMedia(string title, string episodeLabel, string? coverUrl, bool hasPrevious, bool hasNext)
    {
        if (_disposed) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.ClearAll();
            updater.Type = MediaPlaybackType.Video;
            updater.VideoProperties.Title = title;
            updater.VideoProperties.Subtitle = episodeLabel;
            if (Uri.TryCreate(coverUrl, UriKind.Absolute, out var cover) && (cover.Scheme == Uri.UriSchemeHttp || cover.Scheme == Uri.UriSchemeHttps))
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(cover);
            updater.Update();
            _smtc.IsPreviousEnabled = hasPrevious;
            _smtc.IsNextEnabled = hasNext;
        }
        catch (Exception)
        {
            // SMTC is optional; never let it break playback.
        }
    }

    /// <summary>Updates the transport state and the keep-awake request (awake only while actually playing).</summary>
    public void SetPlayback(bool isPlaying, bool isBuffering, bool isClosed)
    {
        if (_disposed) return;
        try
        {
            _smtc.PlaybackStatus = isClosed
                ? MediaPlaybackStatus.Closed
                : isBuffering ? MediaPlaybackStatus.Changing : isPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception)
        {
            // Ignore.
        }

        SetAwake(isPlaying && !isClosed);
    }

    public void SetTimeline(TimeSpan position, TimeSpan duration)
    {
        if (_disposed || duration <= TimeSpan.Zero) return;
        try
        {
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                StartTime = TimeSpan.Zero,
                MinSeekTime = TimeSpan.Zero,
                Position = position > duration ? duration : position,
                MaxSeekTime = duration,
                EndTime = duration,
            });
        }
        catch (Exception)
        {
            // Ignore.
        }
    }

    private void SetAwake(bool awake)
    {
        if (awake == _awake) return;
        try
        {
            if (awake) _display.RequestActive();
            else _display.RequestRelease();
            _awake = awake;
        }
        catch (Exception)
        {
            // Keep-awake is best effort.
        }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args) =>
        ButtonPressed?.Invoke(this, args.Button);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SetAwake(false);
        try
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            _smtc.IsEnabled = false;
        }
        catch (Exception)
        {
            // Already torn down.
        }

        _host.Dispose();
    }
}
