using System.Diagnostics;
using System.Globalization;
using AnimaWindows.App.Services;
using AnimaWindows.Core.AniSkip;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Playback;
using AnimaWindows.Core.Subtitles;
using AnimaWindows.Core.Tracking;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Windows.Media;

namespace AnimaWindows.App.Player;

/// <summary>A quality entry of the quality menu (<see cref="Height"/> null = automatic).</summary>
public sealed record QualityOption(string Label, int? Height);

/// <summary>A subtitle entry of the subtitle menu (<see cref="Track"/> null = off).</summary>
public sealed record SubtitleOption(string Label, SubtitleTrack? Track);

/// <summary>
/// View model of <see cref="PlayerPage"/>: stream resolution, engine selection/failover, progress saving, skip segments,
/// subtitles, prefetch and Windows media integration. Port of <c>PlayerViewModel</c> in feature/player. Created on the UI
/// thread; all mutation happens on the UI thread (engine and SMTC callbacks are marshalled).
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly long NoticeMs = 4000;

    private readonly ISettingsStore _settings;
    private readonly ILibraryRepository _library;
    private readonly PlaybackResolver _resolver;
    private readonly ISkipTimesRepository _skipTimes;
    private readonly IMalIdResolver _malIds;
    private readonly SyncRepository _sync;
    private readonly IClock _clock;
    private readonly INetworkCostProvider _network;
    private readonly ICloudflareSolver _cloudflare;
    private readonly IPlayerEngineFactory _engines;
    private readonly PlayerSessionState _session;
    private readonly SubtitleLoader _subtitles;
    private readonly PlayerMediaSession _media;
    private readonly IDispatcher _dispatcher;
    private readonly IShellChrome _chrome;
    private readonly INavigationService _navigation;
    private readonly NextEpisodePrefetcher<ResolvedPlayback> _prefetcher;
    private readonly AutoSkipUndo _undo = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Stopwatch _slowClock = Stopwatch.StartNew();

    private PlayerNavigationArgs? _args;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _operation;
    private CancellationTokenSource? _subtitleCts;
    private IPlaybackEngine? _engine;
    private EngineKind _engineKind = EngineKind.Native;
    private AppSettings _appSettings = AppSettings.Fresh;
    private StreamSet? _set;
    private bool _isOffline;
    private int _index;
    private SkipController? _skip;
    private CueTimeline? _cues;
    private long _pendingSkipTargetMs;
    private long _lastSavedMs = -1;
    private long _lastKnownPositionMs;
    private int _sinceSave;
    private int _slowTicks;
    private long _noticeUntil;
    private bool _errorIsCloudflare;
    private bool _embeddedSkipHintShown;
    private bool _malAttempted;
    private int? _malId;
    private bool _disposed;

    // ---- observable state ----
    private string _title = string.Empty;
    private string _sourceLabel = string.Empty;
    private bool _isLoading = true;
    private bool _isBuffering;
    private bool _isPlaying;
    private bool _hasEnded;
    private bool _hasError;
    private string _errorMessage = string.Empty;
    private string _notice = string.Empty;
    private double _positionSeconds;
    private double _durationSeconds;
    private string _positionText = "0:00";
    private string _durationText = "0:00";
    private double _volume = 1.0;
    private bool _isMuted;
    private double _speed = 1.0;
    private bool _hasPrevious;
    private bool _hasNext;
    private SkipSegment? _skipButtonSegment;
    private SkipSegment? _undoSegment;
    private SkipTimes? _skipTimesValue;
    private string _subtitleText = string.Empty;
    private IReadOnlyList<SubtitleOption> _subtitleOptions = [];
    private int _selectedSubtitleIndex;
    private IReadOnlyList<QualityOption> _qualityOptions = [];
    private int? _selectedQualityHeight;
    private PlaybackEngineChoice _engineChoice = PlaybackEngineChoice.Auto;
    private bool _isFullScreen;
    private bool _isCompactOverlay;

    public PlayerViewModel(
        ISettingsStore settings,
        ILibraryRepository library,
        PlaybackResolver resolver,
        ISkipTimesRepository skipTimes,
        IMalIdResolver malIds,
        SyncRepository sync,
        IClock clock,
        INetworkCostProvider network,
        ICloudflareSolver cloudflare,
        IPlayerEngineFactory engines,
        PlayerSessionState session,
        SubtitleLoader subtitles,
        PlayerMediaSession media,
        IDispatcher dispatcher,
        IShellChrome chrome,
        INavigationService navigation)
    {
        _settings = settings;
        _library = library;
        _resolver = resolver;
        _skipTimes = skipTimes;
        _malIds = malIds;
        _sync = sync;
        _clock = clock;
        _network = network;
        _cloudflare = cloudflare;
        _engines = engines;
        _session = session;
        _subtitles = subtitles;
        _media = media;
        _dispatcher = dispatcher;
        _chrome = chrome;
        _navigation = navigation;
        _prefetcher = new NextEpisodePrefetcher<ResolvedPlayback>(clock);
    }

    // ---- bindable properties ----

    public string Title { get => _title; private set => SetProperty(ref _title, value); }

    /// <summary>"Source: X", "Source: X (fallback)" or "Offline"; empty while resolving.</summary>
    public string SourceLabel { get => _sourceLabel; private set => SetProperty(ref _sourceLabel, value); }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    public bool IsBuffering { get => _isBuffering; private set => SetProperty(ref _isBuffering, value); }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (SetProperty(ref _isPlaying, value)) OnPropertyChanged(nameof(PlayGlyph));
        }
    }

    public bool HasEnded { get => _hasEnded; private set => SetProperty(ref _hasEnded, value); }

    public bool HasError { get => _hasError; private set => SetProperty(ref _hasError, value); }

    public string ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }

    /// <summary>Short non-blocking message (auto-clears).</summary>
    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value)) OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public double PositionSeconds { get => _positionSeconds; private set => SetProperty(ref _positionSeconds, value); }

    public double DurationSeconds { get => _durationSeconds; private set => SetProperty(ref _durationSeconds, value); }

    public string PositionText { get => _positionText; private set => SetProperty(ref _positionText, value); }

    public string DurationText { get => _durationText; private set => SetProperty(ref _durationText, value); }

    /// <summary>Segoe Fluent glyph of the play/pause button.</summary>
    public string PlayGlyph => _isPlaying ? "" : "";

    /// <summary>Linear volume 0..1.</summary>
    public double Volume { get => _volume; private set => SetProperty(ref _volume, value); }

    public bool IsMuted
    {
        get => _isMuted;
        private set
        {
            if (SetProperty(ref _isMuted, value)) OnPropertyChanged(nameof(VolumeGlyph));
        }
    }

    public string VolumeGlyph => _isMuted || _volume <= 0 ? "" : _volume < 0.34 ? "" : _volume < 0.67 ? "" : "";

    public double Speed
    {
        get => _speed;
        private set
        {
            if (SetProperty(ref _speed, value)) OnPropertyChanged(nameof(SpeedText));
        }
    }

    public string SpeedText => PlayerFormat.Speed(_speed);

    public bool HasPrevious { get => _hasPrevious; private set => SetProperty(ref _hasPrevious, value); }

    public bool HasNext { get => _hasNext; private set => SetProperty(ref _hasNext, value); }

    public bool IsSkipButtonVisible => _skipButtonSegment is not null;

    public string SkipButtonText => _skipButtonSegment == SkipSegment.Ending
        ? PlayerStrings.Get("Player_SkipOutro", "Skip outro")
        : PlayerStrings.Get("Player_SkipIntro", "Skip intro");

    public bool IsUndoVisible => _undoSegment is not null;

    public string UndoText => _undoSegment == SkipSegment.Ending
        ? PlayerStrings.Get("Player_SkippedOutro", "Skipped outro")
        : PlayerStrings.Get("Player_SkippedIntro", "Skipped intro");

    /// <summary>Opening/ending of the current episode, for the seek-bar marks.</summary>
    public SkipTimes? SkipTimes { get => _skipTimesValue; private set => SetProperty(ref _skipTimesValue, value); }

    /// <summary>Active cue text as normalised markup (<see cref="StyledText"/>), empty when none.</summary>
    public string SubtitleText
    {
        get => _subtitleText;
        private set
        {
            if (SetProperty(ref _subtitleText, value)) OnPropertyChanged(nameof(HasSubtitle));
        }
    }

    public bool HasSubtitle => _subtitleText.Length > 0;

    /// <summary>"Off" followed by the provider's tracks.</summary>
    public IReadOnlyList<SubtitleOption> SubtitleOptions { get => _subtitleOptions; private set => SetProperty(ref _subtitleOptions, value); }

    public int SelectedSubtitleIndex { get => _selectedSubtitleIndex; private set => SetProperty(ref _selectedSubtitleIndex, value); }

    public IReadOnlyList<QualityOption> QualityOptions { get => _qualityOptions; private set => SetProperty(ref _qualityOptions, value); }

    public int? SelectedQualityHeight { get => _selectedQualityHeight; private set => SetProperty(ref _selectedQualityHeight, value); }

    /// <summary>The user's engine setting (Auto/Native/Mpv).</summary>
    public PlaybackEngineChoice EngineChoice { get => _engineChoice; private set => SetProperty(ref _engineChoice, value); }

    public EngineKind CurrentEngine => _engineKind;

    /// <summary>True while the engine draws its own controls and subtitles (mpv): the XAML transport bar and overlay are hidden.</summary>
    public bool IsEmbeddedUi => _engine is IEmbeddedUiEngine;

    public bool IsNativeUi => !IsEmbeddedUi;

    public bool IsMpvAvailable => _engines.IsMpvAvailable;

    public bool IsFullScreen
    {
        get => _isFullScreen;
        private set
        {
            if (SetProperty(ref _isFullScreen, value)) OnPropertyChanged(nameof(FullScreenGlyph));
        }
    }

    public string FullScreenGlyph => _isFullScreen ? "" : "";

    public bool IsCompactOverlay { get => _isCompactOverlay; private set => SetProperty(ref _isCompactOverlay, value); }

    /// <summary>Current engine instance, or null before the first load. Raised on the UI thread; the page swaps the video surface.</summary>
    public IPlaybackEngine? Engine => _engine;

    public event EventHandler<IPlaybackEngine?>? EngineChanged;

    // ---- lifecycle ----

    public void Initialize(PlayerNavigationArgs args)
    {
        _args = args;
        _index = Math.Clamp(args.StartIndex, 0, Math.Max(0, args.Episodes.Count - 1));
        _isOffline = args.Offline;
        var (volume, muted) = PlayerPreferences.Load();
        Volume = volume;
        IsMuted = muted;
        _appSettings = _settings.Current;
        EngineChoice = _appSettings.Engine;
        IsFullScreen = _chrome.IsFullScreen;
        _chrome.IsImmersive = true;
        _media.ButtonPressed += OnMediaButton;
        _cloudflare.Solved += OnCloudflareSolved;

        var queue = DispatcherQueue.GetForCurrentThread();
        _timer = queue.CreateTimer();
        _timer.Interval = TickInterval;
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        if (args.Episodes.Count == 0)
        {
            ShowError(PlayerStrings.Get("Player_ErrorNoEpisodes", "There is nothing to play."));
            return;
        }

        _ = LoadEpisodeAsync(_index, resume: true, startOverrideMs: null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        SaveProgress();
        _disposed = true;
        _timer?.Stop();
        _lifetime.Cancel();
        _operation?.Cancel();
        _subtitleCts?.Cancel();
        _media.ButtonPressed -= OnMediaButton;
        _cloudflare.Solved -= OnCloudflareSolved;
        PlayerPreferences.Save(_volume, _isMuted);
        DetachEngine();
        _media.Dispose();
        _prefetcher.Clear();
        try
        {
            if (_chrome.IsFullScreen) _chrome.IsFullScreen = false;
            if (_isCompactOverlay) _chrome.ExitCompactOverlay();
            _chrome.IsImmersive = false;
        }
        catch (Exception)
        {
            // Window already closing.
        }

        _operation?.Dispose();
        _subtitleCts?.Dispose();
        _lifetime.Dispose();
    }

    // ---- episode loading ----

    private CancellationToken BeginOperation()
    {
        _operation?.Cancel();
        _operation?.Dispose();
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _operation.Token;
    }

    private Episode CurrentEpisode => _args!.Episodes[_index];

    private async Task LoadEpisodeAsync(int index, bool resume, long? startOverrideMs)
    {
        if (_args is null || index < 0 || index >= _args.Episodes.Count || _disposed) return;
        SaveProgress();
        var ct = BeginOperation();
        _engine?.Stop();
        _index = index;
        _lastSavedMs = -1;
        _sinceSave = 0;
        _skip = null;
        _set = null;
        _cues = null;
        _lastKnownPositionMs = 0;
        _embeddedSkipHintShown = false;
        _undo.Dismiss();
        _errorIsCloudflare = false;
        var anime = _args.Anime;
        var episode = CurrentEpisode;
        Title = PlayerFormat.Title(anime.Title, episode.Number);
        HasPrevious = index > 0;
        HasNext = index < _args.Episodes.Count - 1;
        HasError = false;
        HasEnded = false;
        IsLoading = true;
        SourceLabel = string.Empty;
        SkipTimes = null;
        SetSkipButton(null);
        SetUndo(null);
        SubtitleText = string.Empty;
        SubtitleOptions = (SubtitleOption[])[new SubtitleOption(PlayerStrings.Get("Player_SubtitlesOff", "Off"), null)];
        SelectedSubtitleIndex = 0;
        QualityOptions = [];
        SelectedQualityHeight = null;
        UpdatePosition(TimeSpan.Zero, TimeSpan.Zero);
        _media.SetMedia(anime.Title, PlayerStrings.Get("Player_Episode", "Episode") + " " + episode.Number, anime.CoverUrl, HasPrevious, HasNext);

        try
        {
            _appSettings = _settings.Current;
            EngineChoice = _appSettings.Engine;
            var resolved = await _resolver.TryOfflineAsync(_args.ProviderId, anime, episode, ct);
            if (resolved is null && _args.Offline)
                throw new InvalidOperationException(PlayerStrings.Get("Player_ErrorNotDownloaded", "This episode is not downloaded."));
            if (resolved is null)
            {
                var key = PrefetchKey(index);
                resolved = key is null ? null : _prefetcher.Take(key);
                resolved ??= await _resolver.ResolveOnlineAsync(anime, episode, ct);
            }

            ct.ThrowIfCancellationRequested();
            _set = resolved.Set;
            _isOffline = resolved.IsOffline;
            SourceLabel = resolved.IsOffline
                ? PlayerStrings.Get("Player_Offline", "Offline")
                : PlayerStrings.Get("Player_Source", "Source:") + " " + resolved.SourceLabel +
                  (resolved.IsFallback ? " " + PlayerStrings.Get("Player_SourceFallback", "(fallback)") : string.Empty);
            QualityOptions = BuildQualityOptions(resolved.Set);
            SubtitleOptions = BuildSubtitleOptions(resolved.Set);

            var startMs = startOverrideMs
                          ?? (resume
                              ? ResumePolicy.StartPositionMs(await _library.GetProgressAsync(_args.ProviderId, anime.Id, episode.Number, ct))
                              : 0L);
            EnsureEngine();
            await StartPlaybackAsync(resolved.Set, startMs, null, ct);
            if (HasError) return;

            _ = LoadSkipTimesAsync(resolved.Set, episode, index, ct);
            var track = TrackPicker.Pick(resolved.Set.Subtitles, _appSettings.SubtitleLanguage, resolved.Set.HardsubLanguage);
            if (track is not null) await SelectSubtitleAsync(SubtitleOptions.ToList().FindIndex(o => o.Track == track));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded by another load or the page closed.
        }
        catch (CloudflareChallengeException e)
        {
            _errorIsCloudflare = true;
            ShowError(PlayerStrings.Get("Player_ErrorCloudflare", "The source needs a Cloudflare check.") + " " + e.Url);
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    private async Task StartPlaybackAsync(StreamSet set, long startMs, int? explicitHeight, CancellationToken ct)
    {
        var engine = _engine ?? throw new InvalidOperationException("No playback engine.");
        var pref = _appSettings.Quality;
        StreamVariant? variant = null;
        string? localFile = null;
        if (_isOffline)
        {
            variant = set.Variants.Count > 0 ? set.Variants[0] : null;
            if (variant is not null && Uri.TryCreate(variant.Url, UriKind.Absolute, out var uri) && uri.IsFile) localFile = uri.LocalPath;
        }
        else if (!QualitySelector.UseMaster(set, pref, explicitHeight))
        {
            variant = QualitySelector.SelectVariant(set.Variants, pref, explicitHeight);
        }

        if (variant is null && set.MasterUrl is null)
            throw new InvalidOperationException(PlayerStrings.Get("Player_ErrorNoStream", "No playable stream was found."));

        if (!_isOffline)
        {
            // Stream URLs come from scraped providers: engines must only ever be handed http(s) (no file:, smb:, edl:, ...).
            var target = variant?.Url ?? set.MasterUrl;
            if (!IsHttpUrl(target) || (!string.IsNullOrEmpty(set.ExternalAudioUrl) && !IsHttpUrl(set.ExternalAudioUrl)))
                throw new InvalidOperationException(PlayerStrings.Get("Player_ErrorNoStream", "No playable stream was found."));
        }

        SelectedQualityHeight = explicitHeight;
        _lastKnownPositionMs = startMs;
        if (engine is IEmbeddedUiEngine embedded) embedded.MediaTitle = Title;
        IsLoading = true;
        await engine.LoadAsync(new PlaybackRequest(set, variant, TimeSpan.FromMilliseconds(startMs), localFile), ct);
        if (!ReferenceEquals(engine, _engine)) return;
        if (engine.State != PlaybackState.Failed) IsLoading = false;
    }

    private static bool IsHttpUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static IReadOnlyList<QualityOption> BuildQualityOptions(StreamSet set)
    {
        var options = new List<QualityOption> { new(PlayerStrings.Get("Player_QualityAuto", "Auto"), null) };
        foreach (var height in set.Variants.Select(v => v.Quality).Where(q => q is not null).Select(q => q!.Value).Distinct().OrderByDescending(q => q))
            options.Add(new QualityOption(height.ToString(CultureInfo.InvariantCulture) + "p", height));
        return options;
    }

    private static IReadOnlyList<SubtitleOption> BuildSubtitleOptions(StreamSet set)
    {
        var options = new List<SubtitleOption> { new(PlayerStrings.Get("Player_SubtitlesOff", "Off"), null) };
        var n = 0;
        foreach (var track in set.Subtitles.DistinctBy(t => t.Url))
        {
            if (track.Label?.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) == true) continue;
            n++;
            var label = !string.IsNullOrWhiteSpace(track.Label)
                ? track.Label
                : !string.IsNullOrWhiteSpace(track.Language)
                    ? track.Language
                    : PlayerStrings.Get("Player_SubtitleTrack", "Track") + " " + n.ToString(CultureInfo.InvariantCulture);
            options.Add(new SubtitleOption(label, track));
        }

        return options;
    }

    private string? PrefetchKey(int index) =>
        _args is not null && index >= 0 && index < _args.Episodes.Count
            ? _args.ProviderId + "/" + _args.Anime.Id + "/" + _args.Episodes[index].Id
            : null;

    // ---- engines ----

    private void EnsureEngine()
    {
        if (_engine is not null) return;
        var kind = EngineSelection.Initial(_appSettings.Engine, _session.PreferredEngine, _engines.IsMpvAvailable);
        AttachEngine(kind);
    }

    private void AttachEngine(EngineKind kind)
    {
        var engine = _engines.Create(kind);
        _engine = engine;
        _engineKind = kind;
        engine.Volume = _volume;
        engine.IsMuted = _isMuted;
        engine.Rate = _speed;
        engine.StateChanged += OnEngineStateChanged;
        engine.Ended += OnEngineEnded;
        engine.Failed += OnEngineFailed;
        if (engine is IEmbeddedUiEngine embedded) embedded.ScriptMessage += OnScriptMessage;
        OnPropertyChanged(nameof(CurrentEngine));
        OnPropertyChanged(nameof(IsEmbeddedUi));
        OnPropertyChanged(nameof(IsNativeUi));
        EngineChanged?.Invoke(this, engine);
    }

    private void DetachEngine()
    {
        var engine = _engine;
        if (engine is null) return;
        _engine = null;
        engine.StateChanged -= OnEngineStateChanged;
        engine.Ended -= OnEngineEnded;
        engine.Failed -= OnEngineFailed;
        if (engine is IEmbeddedUiEngine embedded) embedded.ScriptMessage -= OnScriptMessage;
        EngineChanged?.Invoke(this, null);
        try
        {
            engine.Dispose();
        }
        catch (Exception)
        {
            // Native teardown problems must not block navigation.
        }
    }

    private async Task SwitchEngineAsync(EngineKind kind, long startMs)
    {
        if (_set is null || _disposed) return;
        var ct = BeginOperation();
        try
        {
            DetachEngine();
            AttachEngine(kind);
            HasError = false;
            ErrorMessage = string.Empty;
            await StartPlaybackAsync(_set, startMs, SelectedQualityHeight, ct);
            if (HasError) return;
            await ApplySubtitleAsync(SubtitleOptions.Count > SelectedSubtitleIndex ? SubtitleOptions[SelectedSubtitleIndex].Track : null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded.
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    /// <summary>Applies the engine choice from the engine menu (persisted) and switches at the current position when needed.</summary>
    public async Task SelectEngineAsync(PlaybackEngineChoice choice)
    {
        if (_disposed) return;
        if (choice == PlaybackEngineChoice.Mpv && !_engines.IsMpvAvailable)
        {
            ShowNotice(PlayerStrings.Get("Player_MpvMissing", "libmpv was not found, so mpv is unavailable."));
            return;
        }

        try
        {
            await _settings.UpdateAsync(s => s with { Engine = choice });
        }
        catch (Exception)
        {
            // Setting not persisted; the choice still applies to this session.
        }

        _appSettings = _appSettings with { Engine = choice };
        EngineChoice = choice;
        _session.PreferredEngine = choice == PlaybackEngineChoice.Auto ? null : EngineSelection.Resolve(choice, _engines.IsMpvAvailable);
        var kind = EngineSelection.Resolve(choice, _engines.IsMpvAvailable);
        if (kind == _engineKind || _engine is null) return;
        await SwitchEngineAsync(kind, CurrentPositionMs());
    }

    private void OnEngineStateChanged(object? sender, PlaybackState state) =>
        RunOnUi(() =>
        {
            if (!ReferenceEquals(sender, _engine)) return;
            ApplyState(state);
        });

    private void OnEngineEnded(object? sender, EventArgs e) =>
        RunOnUi(() =>
        {
            if (ReferenceEquals(sender, _engine)) OnEpisodeEnded();
        });

    private void OnEngineFailed(object? sender, PlaybackFailure failure) =>
        RunOnUi(() =>
        {
            if (ReferenceEquals(sender, _engine)) _ = HandleFailureAsync(failure.Message);
        });

    private async Task HandleFailureAsync(string message)
    {
        await Task.Yield(); // never tear the failing engine down from inside its own event.
        if (_disposed || _set is null) return;
        var startMs = Math.Max(_lastKnownPositionMs, CurrentPositionMs());
        if (EngineSelection.ShouldFallBackToMpv(_appSettings.Engine, _engineKind, _engines.IsMpvAvailable))
        {
            _session.PreferredEngine = EngineKind.Mpv;
            ShowNotice(PlayerStrings.Get("Player_SwitchingToMpv", "Native playback failed, switching to mpv."));
            await SwitchEngineAsync(EngineKind.Mpv, startMs);
            return;
        }

        SaveProgress();
        ShowError(message);
    }

    private void OnScriptMessage(object? sender, string message) =>
        RunOnUi(() =>
        {
            if (!ReferenceEquals(sender, _engine)) return;
            switch (message)
            {
                case "next":
                    Next();
                    break;
                case "prev":
                    Previous();
                    break;
                case "skip":
                    SkipCurrentSegment();
                    break;
                case "fullscreen":
                    ToggleFullscreen();
                    break;
                case "escape":
                    ExitFullscreenOrOverlay();
                    break;
            }
        });

    private void ApplyState(PlaybackState state)
    {
        IsPlaying = state == PlaybackState.Playing;
        IsBuffering = state is PlaybackState.Buffering or PlaybackState.Loading;
        if (state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering) IsLoading = false;
        HasEnded = state == PlaybackState.Ended;
        _media.SetPlayback(IsPlaying, IsBuffering, state is PlaybackState.Idle or PlaybackState.Failed);
        if (state == PlaybackState.Paused) SaveProgress();
    }

    // ---- playback commands ----

    [RelayCommand]
    public void PlayPause()
    {
        var engine = _engine;
        if (engine is null) return;
        if (engine.State is PlaybackState.Playing or PlaybackState.Buffering) engine.Pause();
        else engine.Play();
    }

    public void SeekBy(TimeSpan delta)
    {
        var engine = _engine;
        if (engine is null) return;
        var target = PlayerFormat.ClampSeek(engine.Position + delta, engine.Duration);
        engine.Seek(target);
        UpdatePosition(target, engine.Duration);
        _lastKnownPositionMs = (long)target.TotalMilliseconds;
    }

    public void SeekTo(double seconds)
    {
        var engine = _engine;
        if (engine is null) return;
        var target = PlayerFormat.ClampSeek(TimeSpan.FromSeconds(seconds), engine.Duration);
        engine.Seek(target);
        UpdatePosition(target, engine.Duration);
        _lastKnownPositionMs = (long)target.TotalMilliseconds;
    }

    [RelayCommand]
    public void Next()
    {
        if (HasNext) _ = LoadEpisodeAsync(_index + 1, resume: true, startOverrideMs: null);
    }

    [RelayCommand]
    public void Previous()
    {
        if (HasPrevious) _ = LoadEpisodeAsync(_index - 1, resume: true, startOverrideMs: null);
    }

    [RelayCommand]
    public void Retry()
    {
        var pos = Math.Max(_lastKnownPositionMs, CurrentPositionMs());
        _ = LoadEpisodeAsync(_index, resume: pos == 0, startOverrideMs: pos > 0 ? pos : null);
    }

    [RelayCommand]
    public void GoBack()
    {
        ExitFullscreenOrOverlay();
        if (_navigation.CanGoBack) _navigation.GoBack();
    }

    public void SetVolume(double level)
    {
        Volume = Math.Clamp(level, 0, 1);
        if (_volume > 0 && _isMuted) SetMuted(false);
        if (_engine is { } e) e.Volume = _volume;
        OnPropertyChanged(nameof(VolumeGlyph));
        PlayerPreferences.Save(_volume, _isMuted);
    }

    public void AdjustVolume(double delta) => SetVolume(_volume + delta);

    [RelayCommand]
    public void ToggleMute() => SetMuted(!_isMuted);

    private void SetMuted(bool muted)
    {
        IsMuted = muted;
        if (_engine is { } e) e.IsMuted = muted;
        PlayerPreferences.Save(_volume, _isMuted);
    }

    public void SetSpeed(double speed)
    {
        Speed = Math.Clamp(speed, 0.5, 2.0);
        if (_engine is { } e) e.Rate = _speed;
    }

    public async Task SelectQualityAsync(int? height)
    {
        if (_set is null || _isOffline || _disposed) return;
        var ct = BeginOperation();
        try
        {
            await StartPlaybackAsync(_set, CurrentPositionMs(), height, ct);
            if (!HasError) await ApplySubtitleAsync(CurrentSubtitleTrack());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Superseded.
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    [RelayCommand]
    public void ToggleFullscreen()
    {
        _chrome.IsFullScreen = !_chrome.IsFullScreen;
        IsFullScreen = _chrome.IsFullScreen;
    }

    [RelayCommand]
    public void ToggleCompactOverlay()
    {
        if (_isCompactOverlay) _chrome.ExitCompactOverlay();
        else _chrome.EnterCompactOverlay();
        IsCompactOverlay = !_isCompactOverlay;
    }

    /// <summary>Esc: leave compact overlay first, then fullscreen.</summary>
    public void ExitFullscreenOrOverlay()
    {
        if (_isCompactOverlay)
        {
            _chrome.ExitCompactOverlay();
            IsCompactOverlay = false;
        }
        else if (_chrome.IsFullScreen)
        {
            _chrome.IsFullScreen = false;
            IsFullScreen = false;
        }
    }

    /// <summary>True when Esc would do something (fullscreen or compact overlay is active).</summary>
    public bool CanExitFullscreenOrOverlay => _isCompactOverlay || _chrome.IsFullScreen;

    private long CurrentPositionMs() => _engine is { } e ? (long)e.Position.TotalMilliseconds : _lastKnownPositionMs;

    // ---- subtitles ----

    private SubtitleTrack? CurrentSubtitleTrack() =>
        _selectedSubtitleIndex >= 0 && _selectedSubtitleIndex < _subtitleOptions.Count ? _subtitleOptions[_selectedSubtitleIndex].Track : null;

    public async Task SelectSubtitleAsync(int index)
    {
        if (index < 0 || index >= _subtitleOptions.Count) return;
        SelectedSubtitleIndex = index;
        await ApplySubtitleAsync(_subtitleOptions[index].Track);
    }

    /// <summary>Cycles Off, track 1, track 2, ... and back to Off.</summary>
    public Task CycleSubtitlesAsync() =>
        _subtitleOptions.Count <= 1 ? Task.CompletedTask : SelectSubtitleAsync((_selectedSubtitleIndex + 1) % _subtitleOptions.Count);

    private async Task ApplySubtitleAsync(SubtitleTrack? track)
    {
        _subtitleCts?.Cancel();
        _subtitleCts?.Dispose();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _subtitleCts = cts;
        _cues = null;
        SubtitleText = string.Empty;
        if (_engine is IEmbeddedUiEngine embedded)
        {
            embedded.SetSubtitle(track?.Url);
            return;
        }

        if (track is null || _set is null) return;
        try
        {
            var cues = await _subtitles.LoadAsync(track, _set.Headers, cts.Token);
            if (!cts.IsCancellationRequested) _cues = cues;
        }
        catch (OperationCanceledException)
        {
            // Another selection replaced this one.
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested) ShowNotice(PlayerStrings.Get("Player_SubtitleFailed", "Could not load the subtitles."));
        }
    }

    // ---- skip segments ----

    private async Task LoadSkipTimesAsync(StreamSet set, Episode episode, int forIndex, CancellationToken ct)
    {
        try
        {
            if (!_appSettings.AniSkipEnabled || _args is null) return;
            var anime = _args.Anime;
            int? epNum = double.TryParse(episode.Number, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 0 && d < int.MaxValue
                ? (int)d
                : null;
            SkipTimes? aniSkip = null;
            if (epNum is { } n)
            {
                var malId = set.MalId ?? anime.MalId ?? await ResolveMalIdAsync(anime, ct);
                if (malId is { } id) aniSkip = await _skipTimes.SkipTimesAsync(id, n, ct);
            }

            var times = SkipController.Merge(aniSkip, set.Intro, set.Outro);
            if (times is null || forIndex != _index || ct.IsCancellationRequested) return;
            var s = _appSettings;
            _skip = new SkipController(times, s.SkipOffsetSec, s.SkipBehavior, s.AutoSkipIntro, s.AutoSkipOutro);
            SkipTimes = times;
        }
        catch (Exception)
        {
            // Best effort: no skip data, no skip button.
        }
    }

    private async Task<int?> ResolveMalIdAsync(Anime anime, CancellationToken ct)
    {
        if (_malId is not null || _malAttempted) return _malId;
        _malAttempted = true;
        _malId = await _malIds.ResolveAsync(anime, ct);
        return _malId;
    }

    private void HandleSkip(long posMs)
    {
        var controller = _skip;
        if (controller is null || _engine is null) return;
        switch (controller.OnPosition(posMs / 1000.0))
        {
            case SkipAction.AutoSeek auto:
                _undo.OnAutoSkip(auto.Segment, posMs, _clock.UtcNow);
                _engine.Seek(TimeSpan.FromMilliseconds(auto.TargetMs));
                SetSkipButton(null);
                SetUndo(auto.Segment);
                if (_engine is IEmbeddedUiEngine embeddedAuto)
                    embeddedAuto.ShowText(UndoText, TimeSpan.FromSeconds(3));
                break;
            case SkipAction.ShowButton show:
                _pendingSkipTargetMs = show.TargetMs;
                SetSkipButton(show.Segment);
                if (_engine is IEmbeddedUiEngine embedded && !_embeddedSkipHintShown)
                {
                    _embeddedSkipHintShown = true;
                    embedded.ShowText(SkipButtonText + " (S)", TimeSpan.FromSeconds(5));
                }

                break;
            default:
                SetSkipButton(null);
                break;
        }
    }

    /// <summary>The skip button / S key: seeks to the end of the offered segment.</summary>
    [RelayCommand]
    public void SkipCurrentSegment()
    {
        if (_skipButtonSegment is not { } segment || _engine is null) return;
        _engine.Seek(TimeSpan.FromMilliseconds(_pendingSkipTargetMs));
        _skip?.MarkSkipped(segment);
        SetSkipButton(null);
    }

    /// <summary>Undo of the last automatic skip: back to where it happened.</summary>
    [RelayCommand]
    public void UndoSkip()
    {
        if (_undo.Undo(_clock.UtcNow) is { } positionMs) _engine?.Seek(TimeSpan.FromMilliseconds(positionMs));
        SetUndo(null);
    }

    private void SetSkipButton(SkipSegment? segment)
    {
        if (_skipButtonSegment == segment) return;
        _skipButtonSegment = segment;
        OnPropertyChanged(nameof(IsSkipButtonVisible));
        OnPropertyChanged(nameof(SkipButtonText));
    }

    private void SetUndo(SkipSegment? segment)
    {
        if (_undoSegment == segment) return;
        _undoSegment = segment;
        OnPropertyChanged(nameof(IsUndoVisible));
        OnPropertyChanged(nameof(UndoText));
    }

    // ---- ticking, progress, prefetch ----

    private void Tick()
    {
        var engine = _engine;
        if (_disposed || engine is null) return;
        var state = engine.State;
        var pos = engine.Position;
        var dur = engine.Duration;
        if (state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering or PlaybackState.Ended)
        {
            UpdatePosition(pos, dur);
            _lastKnownPositionMs = (long)pos.TotalMilliseconds;
            UpdateSubtitle(_lastKnownPositionMs);
        }

        if (_noticeUntil != 0 && Environment.TickCount64 >= _noticeUntil)
        {
            _noticeUntil = 0;
            Notice = string.Empty;
        }

        if (_slowClock.ElapsedMilliseconds < ProgressCadence.TickMs) return;
        _slowClock.Restart();
        SlowTick(state, (long)pos.TotalMilliseconds, (long)dur.TotalMilliseconds);
    }

    private void SlowTick(PlaybackState state, long posMs, long durMs)
    {
        if (state is PlaybackState.Playing or PlaybackState.Paused) HandleSkip(posMs);
        if (_undoSegment is not null && _undo.Visible(_clock.UtcNow) is null) SetUndo(null);
        if (state != PlaybackState.Playing) return;
        MaybePrefetch(posMs, durMs);
        if (++_sinceSave >= ProgressCadence.SaveEveryTicks)
        {
            _sinceSave = 0;
            SaveProgress();
        }

        if (++_slowTicks % 4 == 0) _media.SetTimeline(TimeSpan.FromMilliseconds(posMs), TimeSpan.FromMilliseconds(durMs));
    }

    private void UpdatePosition(TimeSpan pos, TimeSpan dur)
    {
        PositionSeconds = pos.TotalSeconds;
        DurationSeconds = dur.TotalSeconds;
        PositionText = PlayerFormat.Time(pos);
        DurationText = PlayerFormat.Time(dur);
    }

    private void UpdateSubtitle(long posMs)
    {
        var timeline = _cues;
        if (timeline is null)
        {
            if (_subtitleText.Length > 0) SubtitleText = string.Empty;
            return;
        }

        var active = timeline.ActiveAt(posMs);
        SubtitleText = active.Count == 0 ? string.Empty : string.Join('\n', active.Select(c => c.Text));
    }

    private void MaybePrefetch(long posMs, long durMs)
    {
        if (_args is null || _isOffline) return;
        var nextIndex = _index + 1;
        var key = PrefetchKey(nextIndex);
        if (key is null) return;
        var network = _network.Current;
        if (!_prefetcher.ShouldStart(key, _appSettings.PrefetchNextEpisode, network == NetworkCost.Metered, _appSettings.PrefetchOnMetered, posMs, durMs))
            return;
        _prefetcher.MarkAttempted(key);
        _ = PrefetchAsync(key, _args.ProviderId, _args.Anime, _args.Episodes[nextIndex], _lifetime.Token);
    }

    private async Task PrefetchAsync(string key, string providerId, Anime anime, Episode episode, CancellationToken ct)
    {
        try
        {
            if (await _resolver.TryOfflineAsync(providerId, anime, episode, ct) is not null) return;
            var resolved = await _resolver.ResolveOnlineAsync(anime, episode, ct);
            RunOnUi(() => _prefetcher.Put(key, resolved));
        }
        catch (Exception)
        {
            // Silent: the episode is resolved normally when it starts.
        }
    }

    private void SaveProgress()
    {
        var engine = _engine;
        if (engine is null || _args is null) return;
        if (engine.State is PlaybackState.Idle or PlaybackState.Loading or PlaybackState.Failed) return;
        var durMs = (long)engine.Duration.TotalMilliseconds;
        if (durMs <= 0) return;
        var posMs = (long)engine.Position.TotalMilliseconds;
        if (posMs == _lastSavedMs) return;
        _lastSavedMs = posMs;
        Persist(posMs, durMs);
    }

    private void Persist(long posMs, long durMs)
    {
        if (_args is null) return;
        _ = PersistAsync(_args.Anime, CurrentEpisode.Number, posMs, durMs);
    }

    private async Task PersistAsync(Anime anime, string episodeNumber, long posMs, long durMs)
    {
        try
        {
            await _library.SaveProgressAsync(anime, episodeNumber, posMs, durMs);
        }
        catch (Exception)
        {
            // Progress is best effort.
        }

        try
        {
            if (!SyncRepository.IsWatched(posMs, durMs)) return;
            var withMal = anime;
            if (withMal.MalId is null)
            {
                var id = await ResolveMalIdAsync(anime, CancellationToken.None);
                if (id is not { } resolved) return;
                withMal = anime with { MalId = resolved };
            }

            _sync.ReportProgress(withMal, episodeNumber, posMs, durMs, null);
        }
        catch (Exception)
        {
            // Tracker sync is best effort and retried by the sync queue.
        }
    }

    private void OnEpisodeEnded()
    {
        var engine = _engine;
        var durMs = engine is null ? 0 : (long)engine.Duration.TotalMilliseconds;
        if (durMs > 0)
        {
            _lastSavedMs = durMs;
            Persist(durMs, durMs);
        }

        HasEnded = true;
        if (_appSettings.AutoPlayNext && HasNext)
            _ = LoadEpisodeAsync(_index + 1, resume: false, startOverrideMs: null);
    }

    // ---- errors and notices ----

    private void ShowError(string message)
    {
        ErrorMessage = string.IsNullOrWhiteSpace(message) ? PlayerStrings.Get("Player_ErrorPlayback", "Playback failed.") : message;
        HasError = true;
        IsLoading = false;
        IsBuffering = false;
    }

    private void ShowNotice(string message)
    {
        Notice = message;
        _noticeUntil = Environment.TickCount64 + NoticeMs;
    }

    private void OnCloudflareSolved(object? sender, EventArgs e) =>
        RunOnUi(() =>
        {
            if (HasError && _errorIsCloudflare) Retry();
        });

    private void OnMediaButton(object? sender, SystemMediaTransportControlsButton button) =>
        RunOnUi(() =>
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    _engine?.Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                    _engine?.Pause();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    Next();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    Previous();
                    break;
            }
        });

    private void RunOnUi(Action action)
    {
        if (_disposed) return;
        _ = _dispatcher.RunAsync(() =>
        {
            if (!_disposed) action();
        });
    }
}
