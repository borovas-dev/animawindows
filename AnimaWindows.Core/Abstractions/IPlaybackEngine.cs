using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>Playback state machine of an <see cref="IPlaybackEngine"/>.</summary>
public enum PlaybackState
{
    /// <summary>Nothing loaded (initial, after <see cref="IPlaybackEngine.Stop"/>).</summary>
    Idle,

    /// <summary>A <c>LoadAsync</c> is in progress (resolving/opening the source).</summary>
    Loading,

    /// <summary>Playing intent is set but data is not yet available (initial buffering or rebuffering).</summary>
    Buffering,

    Playing,
    Paused,

    /// <summary>Reached the end of the media. <see cref="IPlaybackEngine.Ended"/> was raised.</summary>
    Ended,

    /// <summary>Unrecoverable error. <see cref="IPlaybackEngine.Failed"/> was raised. A new <c>LoadAsync</c> recovers.</summary>
    Failed,
}

/// <summary>What to play. Passed to <see cref="IPlaybackEngine.LoadAsync"/>.</summary>
/// <param name="Streams">Source description: master/variants, headers, optional external audio.</param>
/// <param name="Variant">
/// Explicit rendition to play (one of <see cref="StreamSet.Variants"/>). Null = adaptive: play
/// <see cref="StreamSet.MasterUrl"/> when present, else the first (best) variant.
/// </param>
/// <param name="StartPosition">Initial seek position; <see cref="TimeSpan.Zero"/> = from the start.</param>
/// <param name="LocalFile">When non-null, play this local file (offline download) instead of the network URLs; headers are ignored.</param>
/// <param name="AutoPlay">Start playing as soon as ready (default true); otherwise end up Paused.</param>
public sealed record PlaybackRequest(
    StreamSet Streams,
    StreamVariant? Variant = null,
    TimeSpan StartPosition = default,
    string? LocalFile = null,
    bool AutoPlay = true);

/// <summary>Payload of <see cref="IPlaybackEngine.Failed"/>.</summary>
public sealed record PlaybackFailure(string Message, Exception? Exception = null);

/// <summary>
/// A video player backend (native Windows MediaPlayer or libmpv). Replaces ExoPlayer/Media3 on Android (feature/player).
/// <para>
/// Threading: methods may be called from the UI thread and return quickly; events are raised on arbitrary threads (the UI marshals).
/// Implementations are single-instance-per-playback-session; <see cref="IDisposable.Dispose"/> releases native resources and is idempotent.
/// </para>
/// <para>
/// Subtitles are rendered by the app (overlay), NOT by the engine: engines must never display embedded or external subtitle tracks
/// and must ignore <see cref="StreamSet.Subtitles"/>. (Burned-in hardsubs are of course visible.)
/// </para>
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    /// <summary>Short engine name for diagnostics ("native", "mpv").</summary>
    string Name { get; }

    /// <summary>
    /// Opaque, engine-specific visual element hosting the video (e.g. a WinUI FrameworkElement or swap-chain panel). The App
    /// project knows the concrete type per engine and places it in the visual tree; Core never inspects it. Stable for the
    /// lifetime of the engine instance.
    /// </summary>
    object VideoSurface { get; }

    PlaybackState State { get; }

    /// <summary>Current position; <see cref="TimeSpan.Zero"/> when nothing is loaded.</summary>
    TimeSpan Position { get; }

    /// <summary>Media duration; <see cref="TimeSpan.Zero"/> while unknown (live/unknown).</summary>
    TimeSpan Duration { get; }

    /// <summary>Playback speed multiplier, 1.0 normal. Setting applies immediately and persists across loads of this engine instance.</summary>
    double Rate { get; set; }

    /// <summary>Linear volume 0.0..1.0 (clamped).</summary>
    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>
    /// Opens the source. Sends <see cref="StreamSet.Headers"/> with every request (including playlist and segment requests) and
    /// plays <see cref="StreamSet.ExternalAudioUrl"/> as an additional audio track when present. State goes Loading, then
    /// Buffering/Playing (or Paused). Completes when the source is opened and seeked to the start position. Playback problems do
    /// not throw: State becomes Failed, <see cref="Failed"/> is raised and the returned task completes normally. Cancellation aborts
    /// the load (State returns to Idle) and throws <see cref="OperationCanceledException"/>.
    /// </summary>
    Task LoadAsync(PlaybackRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts or resumes. If Ended, restarts from the beginning. No-op when Idle/Loading/Failed.</summary>
    void Play();

    /// <summary>Pauses. No-op unless Playing/Buffering.</summary>
    void Pause();

    /// <summary>Seeks (clamped to 0..Duration). Raises <see cref="PositionChanged"/> promptly after the seek lands.</summary>
    void Seek(TimeSpan position);

    /// <summary>Stops and unloads; State becomes Idle. The engine stays reusable.</summary>
    void Stop();

    /// <summary>Raised about 4 times per second while Playing, and after seeks, with the new position.</summary>
    event EventHandler<TimeSpan>? PositionChanged;

    /// <summary>Raised when the duration becomes known or changes.</summary>
    event EventHandler<TimeSpan>? DurationChanged;

    /// <summary>Raised on every state transition with the new state.</summary>
    event EventHandler<PlaybackState>? StateChanged;

    /// <summary>Raised once when playback reaches the end (State = Ended).</summary>
    event EventHandler? Ended;

    /// <summary>Raised when playback fails (State = Failed).</summary>
    event EventHandler<PlaybackFailure>? Failed;
}
