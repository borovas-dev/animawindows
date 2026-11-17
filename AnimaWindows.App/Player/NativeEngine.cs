using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Downloads;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.Web.Http.Filters;
using WebHttp = Windows.Web.Http;

namespace AnimaWindows.App.Player;

/// <summary>
/// Adds the stream headers (Referer/Origin/User-Agent, ...) to every request <see cref="AdaptiveMediaSource"/> makes for the
/// playlist, keys and segments.
/// </summary>
internal sealed class HeaderInjectingFilter : IHttpFilter
{
    private readonly HttpBaseProtocolFilter _inner = new();
    private readonly IReadOnlyDictionary<string, string> _headers;

    public HeaderInjectingFilter(IReadOnlyDictionary<string, string> headers) => _headers = headers;

    public Windows.Foundation.IAsyncOperationWithProgress<WebHttp.HttpResponseMessage, WebHttp.HttpProgress> SendRequestAsync(
        WebHttp.HttpRequestMessage request)
    {
        foreach (var (name, value) in _headers)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            request.Headers.Remove(name);
            request.Headers.TryAppendWithoutValidation(name, value);
        }

        return _inner.SendRequestAsync(request);
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>
/// <see cref="IPlaybackEngine"/> over <c>Windows.Media.Playback.MediaPlayer</c>, rendered by a <see cref="MediaPlayerElement"/>
/// without transport controls. HLS goes through <see cref="AdaptiveMediaSource"/> with a header-injecting HTTP client;
/// progressive media and local downloads go through <see cref="LocalStreamProxy"/>. A separate external audio track is not
/// supported (MediaPlayer cannot merge it): such a request fails so the coordinator can fall back to mpv.
/// Must be constructed on the UI thread.
/// </summary>
public sealed class NativeEngine : IPlaybackEngine
{
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(40);

    private readonly LocalStreamProxy _proxy;
    private readonly MediaPlayer _player = new();
    private readonly MediaPlayerElement _element;
    private readonly Timer _timer;
    private readonly object _gate = new();

    private PlaybackState _state = PlaybackState.Idle;
    private volatile TaskCompletionSource<string?>? _openTcs;
    private volatile bool _loaded;
    private volatile bool _ended;
    private volatile bool _disposed;
    private double _rate = 1.0;
    private ProxyLease? _lease;
    private WebHttp.HttpClient? _http;
    private MediaSource? _mediaSource;

    public NativeEngine(LocalStreamProxy proxy)
    {
        _proxy = proxy;
        _player.CommandManager.IsEnabled = false; // SMTC is driven manually by PlayerMediaSession.
        _element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            Stretch = Stretch.Uniform,
            IsTabStop = false,
        };
        _element.SetMediaPlayer(_player);

        _player.MediaOpened += (_, _) =>
        {
            _loaded = true;
            _openTcs?.TrySetResult(null);
            DurationChanged?.Invoke(this, Duration);
        };
        _player.MediaFailed += (_, e) =>
        {
            var message = string.IsNullOrWhiteSpace(e.ErrorMessage) ? "Playback failed (" + e.Error + ")" : e.ErrorMessage;
            var tcs = _openTcs;
            if (tcs is not null && tcs.TrySetResult(message)) return;
            Fail(message);
        };
        _player.MediaEnded += (_, _) =>
        {
            _ended = true;
            SetState(PlaybackState.Ended);
            Ended?.Invoke(this, EventArgs.Empty);
        };
        _player.PlaybackSession.PlaybackStateChanged += (s, _) => OnSessionState(s.PlaybackState);
        _player.PlaybackSession.NaturalDurationChanged += (_, _) => DurationChanged?.Invoke(this, Duration);
        _player.PlaybackSession.SeekCompleted += (_, _) => PositionChanged?.Invoke(this, Position);
        _timer = new Timer(_ => Tick(), null, 250, 250);
    }

    public string Name => "native";

    public object VideoSurface => _element;

    /// <summary>The underlying player (for diagnostics; SMTC is not bound to it).</summary>
    public MediaPlayer Player => _player;

    public PlaybackState State
    {
        get
        {
            lock (_gate) return _state;
        }
    }

    public TimeSpan Position
    {
        get
        {
            if (_disposed || !_loaded) return TimeSpan.Zero;
            try
            {
                return _player.PlaybackSession.Position;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            if (_disposed || !_loaded) return TimeSpan.Zero;
            try
            {
                var d = _player.PlaybackSession.NaturalDuration;
                return d > TimeSpan.FromDays(7) ? TimeSpan.Zero : d;
            }
            catch (Exception)
            {
                return TimeSpan.Zero;
            }
        }
    }

    public double Rate
    {
        get => _rate;
        set
        {
            _rate = Math.Clamp(value, 0.25, 4.0);
            ApplyRate();
        }
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0.0, 1.0);
    }

    public bool IsMuted
    {
        get => _player.IsMuted;
        set => _player.IsMuted = value;
    }

    public event EventHandler<TimeSpan>? PositionChanged;

    public event EventHandler<TimeSpan>? DurationChanged;

    public event EventHandler<PlaybackState>? StateChanged;

    public event EventHandler? Ended;

    public event EventHandler<PlaybackFailure>? Failed;

    public async Task LoadAsync(PlaybackRequest request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ReleaseSource();
        SetState(PlaybackState.Loading);
        var opened = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _openTcs = opened;
        try
        {
            var set = request.Streams;
            if (!string.IsNullOrEmpty(set.ExternalAudioUrl))
            {
                Fail("The native engine cannot play a separate audio track.");
                return;
            }

            var source = await CreateSourceAsync(request, cancellationToken);
            if (source is null) return; // CreateSourceAsync already reported the failure.

            _mediaSource = source;
            _player.AutoPlay = request.AutoPlay;
            _player.Source = source;
            string? error;
            using (cancellationToken.Register(() => opened.TrySetCanceled(cancellationToken)))
            {
                try
                {
                    error = await opened.Task.WaitAsync(OpenTimeout, cancellationToken);
                }
                catch (TimeoutException)
                {
                    error = "Timed out opening the stream.";
                }
            }

            if (error is not null)
            {
                Fail(error);
                return;
            }

            ApplyRate();
            if (request.StartPosition > TimeSpan.Zero) _player.PlaybackSession.Position = request.StartPosition;
            SetState(request.AutoPlay ? PlaybackState.Buffering : PlaybackState.Paused);
            PositionChanged?.Invoke(this, Position);
        }
        catch (OperationCanceledException)
        {
            ReleaseSource();
            SetState(PlaybackState.Idle);
            throw;
        }
        finally
        {
            _openTcs = null;
        }
    }

    private async Task<MediaSource?> CreateSourceAsync(PlaybackRequest request, CancellationToken ct)
    {
        var set = request.Streams;
        string url;
        string? hlsProbe;
        if (request.LocalFile is { } local)
        {
            _lease = _proxy.RegisterLocal(local);
            url = _lease.Url;
            hlsProbe = local;
        }
        else
        {
            var chosen = request.Variant?.Url ?? set.MasterUrl ?? set.Variants.FirstOrDefault()?.Url;
            if (chosen is null)
            {
                Fail("No playable stream.");
                return null;
            }

            url = chosen;
            hlsProbe = chosen;
        }

        if (DownloadPlanner.IsHls(hlsProbe))
        {
            var headers = request.LocalFile is null ? set.Headers : new Dictionary<string, string>();
            _http = CreateHttpClient(headers);
            var result = await AdaptiveMediaSource.CreateFromUriAsync(new Uri(url), _http).AsTask(ct);
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success)
            {
                var http = result.HttpResponseMessage is { } r ? " (HTTP " + (int)r.StatusCode + ")" : string.Empty;
                Fail("Could not open the HLS stream: " + result.Status + http);
                return null;
            }

            return MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource);
        }

        if (request.LocalFile is null)
        {
            _lease = _proxy.RegisterRemote(url, set.Headers);
            url = _lease.Url;
        }

        return MediaSource.CreateFromUri(new Uri(url));
    }

    private static WebHttp.HttpClient CreateHttpClient(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0) return new WebHttp.HttpClient();
        try
        {
            return new WebHttp.HttpClient(new HeaderInjectingFilter(headers));
        }
        catch (Exception)
        {
            // Filter marshalling unavailable: fall back to default headers (still sent with every request).
            var client = new WebHttp.HttpClient();
            foreach (var (name, value) in headers)
            {
                if (!string.IsNullOrWhiteSpace(name)) client.DefaultRequestHeaders.TryAppendWithoutValidation(name, value);
            }

            return client;
        }
    }

    public void Play()
    {
        var state = State;
        if (_disposed || state is PlaybackState.Idle or PlaybackState.Loading or PlaybackState.Failed) return;
        if (state == PlaybackState.Ended || _ended)
        {
            _ended = false;
            _player.PlaybackSession.Position = TimeSpan.Zero;
        }

        _player.Play();
    }

    public void Pause()
    {
        if (_disposed) return;
        if (State is PlaybackState.Playing or PlaybackState.Buffering) _player.Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed || !_loaded) return;
        _ended = false;
        _player.PlaybackSession.Position = PlayerFormat.ClampSeek(position, Duration);
        PositionChanged?.Invoke(this, Position);
    }

    public void Stop()
    {
        if (_disposed) return;
        ReleaseSource();
        SetState(PlaybackState.Idle);
    }

    private void ReleaseSource()
    {
        _loaded = false;
        _ended = false;
        try
        {
            _player.Pause();
            _player.Source = null;
        }
        catch (Exception)
        {
            // Player already torn down.
        }

        _mediaSource?.Dispose();
        _mediaSource = null;
        _http?.Dispose();
        _http = null;
        _lease?.Dispose();
        _lease = null;
    }

    private void ApplyRate()
    {
        if (_disposed || !_loaded) return;
        try
        {
            _player.PlaybackSession.PlaybackRate = _rate;
        }
        catch (Exception)
        {
            // Rate not supported by this source.
        }
    }

    private void OnSessionState(MediaPlaybackState state)
    {
        if (!_loaded || _ended || _disposed) return;
        switch (state)
        {
            case MediaPlaybackState.Playing:
                SetState(PlaybackState.Playing);
                break;
            case MediaPlaybackState.Paused:
                SetState(PlaybackState.Paused);
                break;
            case MediaPlaybackState.Buffering:
            case MediaPlaybackState.Opening:
                SetState(PlaybackState.Buffering);
                break;
        }
    }

    private void Tick()
    {
        if (_disposed || State != PlaybackState.Playing) return;
        PositionChanged?.Invoke(this, Position);
    }

    private void Fail(string message)
    {
        _loaded = false;
        SetState(PlaybackState.Failed);
        Failed?.Invoke(this, new PlaybackFailure(message));
    }

    private void SetState(PlaybackState next)
    {
        lock (_gate)
        {
            if (_state == next) return;
            _state = next;
        }

        StateChanged?.Invoke(this, next);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        ReleaseSource();
        try
        {
            _element.SetMediaPlayer(null);
        }
        catch (Exception)
        {
            // Element already detached.
        }

        _player.Dispose();
    }
}
