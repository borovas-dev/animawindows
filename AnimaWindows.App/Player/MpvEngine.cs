using System.Diagnostics;
using System.Globalization;
using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.App.Player;

/// <summary>
/// <see cref="IPlaybackEngine"/> over libmpv. Video is rendered by mpv into a child window of the main window placed over
/// <see cref="VideoSurface"/> (<c>wid</c> option); XAML cannot draw over a native child window, so mpv's own on-screen controller
/// (<c>osc=yes</c>) is the control surface and subtitles are rendered by mpv (<see cref="IEmbeddedUiEngine"/>).
/// Everything degrades gracefully when <c>libmpv-2.dll</c> is missing: <see cref="IsAvailable"/> is false and a load fails.
/// Must be constructed and disposed on the UI thread.
/// </summary>
public sealed class MpvEngine : IPlaybackEngine, IEmbeddedUiEngine
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(45);
    private static readonly long PositionIntervalTicks = Stopwatch.Frequency / 4;

    private readonly Func<nint> _windowHandle;
    private readonly MpvVideoSurface _surface = new();
    private readonly object _gate = new();
    private readonly Stopwatch _positionClock = Stopwatch.StartNew();

    private MpvApi? _api;
    private nint _ctx;
    private nint _child;
    private Thread? _thread;
    private volatile bool _disposed;
    private volatile TaskCompletionSource<string?>? _loadTcs;
    private long _lastPositionTick;
    private double _volume = 1.0;
    private bool _muted;
    private double _rate = 1.0;

    // Observed mpv state; written by the event thread, read anywhere.
    private PlaybackState _state = PlaybackState.Idle;
    private double _positionSeconds;
    private double _durationSeconds;
    private bool _pause;
    private bool _coreIdle = true;
    private bool _cacheBuffering;
    private bool _eof;
    private bool _fileLoaded;
    private bool _loading;
    private bool _failed;
    private bool _endedRaised;

    public MpvEngine(Func<nint> windowHandle)
    {
        _windowHandle = windowHandle;
        _surface.BoundsChanged += (_, bounds) => OnBoundsChanged(bounds);
    }

    /// <summary>True when libmpv-2.dll was found and exports the client API.</summary>
    public static bool IsAvailable => MpvApi.TryLoad() is not null;

    public string Name => "mpv";

    public object VideoSurface => _surface;

    public string? MediaTitle { get; set; }

    public PlaybackState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public TimeSpan Position => TimeSpan.FromSeconds(Math.Max(0, _positionSeconds));

    public TimeSpan Duration => TimeSpan.FromSeconds(Math.Max(0, _durationSeconds));

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, 0.25, 4.0);
            SetProperty("speed", _rate.ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0.0, 1.0);
            SetProperty("volume", (_volume * 100).ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            SetProperty("mute", value ? "yes" : "no");
        }
    }

    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler<TimeSpan>? DurationChanged;

    public event EventHandler<PlaybackState>? StateChanged;

    public event EventHandler? Ended;

    public event EventHandler<PlaybackFailure>? Failed;

    public event EventHandler<string>? ScriptMessage;

    public async Task LoadAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        _loading = true;
        _failed = false;
        _fileLoaded = false;
        _eof = false;
        _endedRaised = false;
        _positionSeconds = 0;
        _durationSeconds = 0;
        RecomputeState();

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _loadTcs = tcs;
        try
        {
            var initError = await EnsureInitializedAsync(cancellationToken);
            if (initError is not null)
            {
                Fail(initError);
                return;
            }

            var set = request.Streams;
            var target = request.LocalFile ?? request.Variant?.Url ?? set.MasterUrl ?? set.Variants.FirstOrDefault()?.Url;
            if (target is null)
            {
                Fail("No playable stream.");
                return;
            }

            ApplyHeaders(request);
            SetProperty("pause", request.AutoPlay ? "no" : "yes");
            SetProperty("start", request.StartPosition > TimeSpan.Zero
                ? request.StartPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
                : "0");
            SetProperty("force-media-title", MediaTitle ?? string.Empty);
            SetProperty("sid", "no");
            var rc = Command("loadfile", target, "replace");
            if (rc < 0)
            {
                Fail(_api?.ErrorString(rc) ?? "mpv is not initialised");
                return;
            }

            string? error;
            using (cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    error = await tcs.Task.WaitAsync(LoadTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    error = "Timed out opening the stream.";
                }
            }

            if (error is not null) Fail(error);
        }
        catch (OperationCanceledException)
        {
            Command("stop");
            _loading = false;
            RecomputeState();
            throw;
        }
        finally
        {
            _loadTcs = null;
        }
    }

    private void ApplyHeaders(PlaybackRequest request)
    {
        var set = request.Streams;
        var headers = request.LocalFile is null ? MpvHeaderSet.From(set.Headers) : new MpvHeaderSet(null, null, []);
        Command("change-list", "http-header-fields", "clr", string.Empty);
        foreach (var field in headers.Fields) Command("change-list", "http-header-fields", "append", field);
        SetProperty("referrer", headers.Referrer ?? string.Empty);
        SetProperty("user-agent", headers.UserAgent ?? DefaultUserAgent);
        Command("change-list", "audio-files", "clr", string.Empty);
        if (!string.IsNullOrEmpty(set.ExternalAudioUrl)) Command("change-list", "audio-files", "append", ToMpvSource(set.ExternalAudioUrl));
    }

    /// <summary>File URIs become plain paths (mpv understands both, but paths avoid escaping pitfalls).</summary>
    private static string ToMpvSource(string source) =>
        Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : source;

    private async Task<string?> EnsureInitializedAsync(CancellationToken ct)
    {
        if (_ctx != nint.Zero) return null;
        var api = MpvApi.TryLoad();
        if (api is null) return "libmpv-2.dll was not found next to the application.";

        if (!_surface.IsLoaded)
        {
            try
            {
                await _surface.WhenLoaded.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
            catch (TimeoutException)
            {
                return "The video surface is not in the visual tree.";
            }
        }

        var parent = _windowHandle();
        if (parent == nint.Zero) return "No window handle for the mpv video area.";
        var bounds = _surface.CurrentBounds() ?? (0, 0, 640, 360);
        _child = Win32Interop.CreateWindowEx(
            0, "Static", string.Empty,
            Win32Interop.WsChild | Win32Interop.WsVisible | Win32Interop.WsClipSiblings | Win32Interop.WsClipChildren,
            bounds.Item1, bounds.Item2, bounds.Item3, bounds.Item4, parent, nint.Zero, nint.Zero, nint.Zero);
        if (_child == nint.Zero) return "Could not create the mpv video window.";

        var ctx = api.Create();
        if (ctx == nint.Zero) return "mpv_create failed.";
        var options = new (string Name, string Value)[]
        {
            ("wid", ((long)_child).ToString(CultureInfo.InvariantCulture)),
            ("config", "no"),
            ("terminal", "no"),
            ("idle", "yes"),
            ("keep-open", "yes"),
            ("osc", "yes"),
            ("input-default-bindings", "yes"),
            ("input-vo-keyboard", "yes"),
            ("input-media-keys", "no"),
            ("cursor-autohide", "3000"),
            ("hwdec", "auto-safe"),
            ("ytdl", "no"),
            ("sub-auto", "no"),
            ("sid", "no"),
            ("demuxer-lavf-o", "allowed_extensions=ALL"),
            ("volume", (_volume * 100).ToString("0.#", CultureInfo.InvariantCulture)),
            ("mute", _muted ? "yes" : "no"),
            ("speed", _rate.ToString("0.###", CultureInfo.InvariantCulture)),
        };
        foreach (var (name, value) in options) api.SetOption(ctx, name, value);

        var rc = api.Initialize(ctx);
        if (rc < 0)
        {
            api.TerminateDestroy(ctx);
            return "mpv_initialize failed: " + api.ErrorString(rc);
        }

        lock (_gate)
        {
            _api = api;
            _ctx = ctx;
        }

        foreach (var (property, format) in new[]
                 {
                     ("time-pos", MpvApi.FormatDouble), ("duration", MpvApi.FormatDouble), ("pause", MpvApi.FormatFlag),
                     ("paused-for-cache", MpvApi.FormatFlag), ("core-idle", MpvApi.FormatFlag), ("eof-reached", MpvApi.FormatFlag),
                 })
        {
            api.Observe(ctx, property, format);
        }

        // mpv's own keys drive the app where XAML has no say (the child window owns keyboard focus).
        foreach (var (key, message) in new[]
                 {
                     ("f", "fullscreen"), ("F11", "fullscreen"), ("MBTN_LEFT_DBL", "fullscreen"), ("ESC", "escape"),
                     ("n", "next"), ("p", "prev"), ("s", "skip"),
                 })
        {
            Command("keybind", key, "script-message anima-" + message);
        }

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _thread.Start();
        OnBoundsChanged(_surface.CurrentBounds());
        return null;
    }

    private void OnBoundsChanged((int X, int Y, int Width, int Height)? bounds)
    {
        var child = _child;
        if (child == nint.Zero || _disposed) return;
        if (bounds is not { } b)
        {
            Win32Interop.ShowWindow(child, Win32Interop.SwHide);
            return;
        }

        Win32Interop.SetWindowPos(child, nint.Zero, b.X, b.Y, b.Width, b.Height, Win32Interop.SwpNoActivate | Win32Interop.SwpShowWindow);
    }

    // ---- commands ----

    private int Command(params string[] args)
    {
        lock (_gate)
        {
            return _ctx == nint.Zero || _api is null ? -1 : _api.Command(_ctx, args);
        }
    }

    private void SetProperty(string name, string value)
    {
        lock (_gate)
        {
            if (_ctx != nint.Zero && _api is not null) _api.SetProperty(_ctx, name, value);
        }
    }

    public void Play()
    {
        var state = State;
        if (_disposed || state is PlaybackState.Idle or PlaybackState.Loading or PlaybackState.Failed) return;
        if (state == PlaybackState.Ended) Command("seek", "0", "absolute");
        SetProperty("pause", "no");
    }

    public void Pause()
    {
        if (_disposed) return;
        if (State is PlaybackState.Playing or PlaybackState.Buffering) SetProperty("pause", "yes");
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || !_fileLoaded) return;
        var target = PlayerFormat.ClampSeek(position, Duration);
        _eof = false;
        Command("seek", target.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
        _positionSeconds = target.TotalSeconds;
        PositionChanged?.Invoke(this, target);
    }

    public void Stop()
    {
        if (_disposed) return;
        Command("stop");
        _fileLoaded = false;
        _loading = false;
        _failed = false;
        _eof = false;
        _positionSeconds = 0;
        _durationSeconds = 0;
        RecomputeState();
    }

    public void ShowText(string text, TimeSpan duration) =>
        Command("show-text", text, ((int)duration.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));

    public void SetSubtitle(string? source)
    {
        Command("sub-remove");
        if (string.IsNullOrEmpty(source))
        {
            SetProperty("sid", "no");
            return;
        }

        Command("sub-add", ToMpvSource(source), "select");
    }

    // ---- event loop (background thread) ----

    private void EventLoop()
    {
        var api = _api;
        var ctx = _ctx;
        if (api is null) return;
        while (!_disposed)
        {
            nint ev;
            try
            {
                ev = api.WaitEvent(ctx, 0.5);
            }
            catch (Exception)
            {
                return;
            }

            if (ev == nint.Zero) continue;
            var id = MpvApi.EventId(ev);
            if (id == MpvApi.EventShutdown) return;
            if (id == MpvApi.EventNone) continue;
            try
            {
                HandleEvent(id, ev);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Debug.WriteLine("mpv event handler failed: " + e.Message);
            }
        }
    }

    private void HandleEvent(int id, nint ev)
    {
        var data = MpvApi.EventData(ev);
        switch (id)
        {
            case MpvApi.EventFileLoaded:
                _fileLoaded = true;
                _loading = false;
                _loadTcs?.TrySetResult(null);
                RecomputeState();
                break;
            case MpvApi.EventEndFile:
                if (data != nint.Zero)
                {
                    var (reason, error) = MpvApi.ReadEndFile(data);
                    if (reason == MpvApi.EndFileError)
                    {
                        var message = _api?.ErrorString(error) ?? "mpv error " + error;
                        if (_loadTcs is { } tcs && tcs.TrySetResult(message)) break;
                        Fail(message);
                    }
                }

                break;
            case MpvApi.EventClientMessage:
                if (data != nint.Zero && MpvApi.ReadFirstClientArg(data) is { } arg && arg.StartsWith("anima-", StringComparison.Ordinal))
                    ScriptMessage?.Invoke(this, arg["anima-".Length..]);
                break;
            case MpvApi.EventSeek:
            case MpvApi.EventPlaybackRestart:
                PositionChanged?.Invoke(this, Position);
                break;
            case MpvApi.EventPropertyChange:
                if (data != nint.Zero) HandleProperty(data);
                break;
        }
    }

    private void HandleProperty(nint data)
    {
        var (name, format, value) = MpvApi.ReadProperty(data);
        if (value == nint.Zero) return;
        if (format == MpvApi.FormatDouble)
        {
            var number = BitConverter.Int64BitsToDouble(System.Runtime.InteropServices.Marshal.ReadInt64(value));
            if (name == "time-pos")
            {
                _positionSeconds = number;
                var now = _positionClock.ElapsedTicks;
                if (now - _lastPositionTick >= PositionIntervalTicks)
                {
                    _lastPositionTick = now;
                    PositionChanged?.Invoke(this, Position);
                }
            }
            else if (name == "duration")
            {
                _durationSeconds = number;
                DurationChanged?.Invoke(this, Duration);
            }

            return;
        }

        if (format != MpvApi.FormatFlag) return;
        var flag = System.Runtime.InteropServices.Marshal.ReadInt32(value) != 0;
        switch (name)
        {
            case "pause":
                _pause = flag;
                break;
            case "paused-for-cache":
                _cacheBuffering = flag;
                break;
            case "core-idle":
                _coreIdle = flag;
                break;
            case "eof-reached":
                _eof = flag;
                break;
        }

        RecomputeState();
    }

    private void RecomputeState()
    {
        PlaybackState next;
        if (_failed) next = PlaybackState.Failed;
        else if (!_fileLoaded) next = _loading ? PlaybackState.Loading : PlaybackState.Idle;
        else if (_eof) next = PlaybackState.Ended;
        else if (_pause) next = PlaybackState.Paused;
        else if (_cacheBuffering || _coreIdle) next = PlaybackState.Buffering;
        else next = PlaybackState.Playing;

        lock (_gate)
        {
            if (_state == next) return;
            _state = next;
        }

        StateChanged?.Invoke(this, next);
        if (next == PlaybackState.Ended && !_endedRaised)
        {
            _endedRaised = true;
            Ended?.Invoke(this, EventArgs.Empty);
        }
        else if (next != PlaybackState.Ended)
        {
            _endedRaised = false;
        }
    }

    private void Fail(string message)
    {
        _failed = true;
        _loading = false;
        _fileLoaded = false;
        RecomputeState();
        Failed?.Invoke(this, new PlaybackFailure(message));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_child != nint.Zero) Win32Interop.ShowWindow(_child, Win32Interop.SwHide);

        nint ctx;
        MpvApi? api;
        lock (_gate)
        {
            ctx = _ctx;
            api = _api;
        }

        if (ctx != nint.Zero && api is not null)
        {
            try
            {
                api.Wakeup(ctx);
                _thread?.Join(TimeSpan.FromSeconds(2));
            }
            catch (Exception)
            {
                // Best effort: the library is being torn down anyway.
            }

            lock (_gate)
            {
                _ctx = nint.Zero;
            }

            api.TerminateDestroy(ctx);
        }

        if (_child != nint.Zero)
        {
            Win32Interop.DestroyWindow(_child);
            _child = nint.Zero;
        }
    }
}
