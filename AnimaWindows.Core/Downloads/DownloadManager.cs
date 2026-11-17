using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Downloads;

/// <summary>
/// Download queue: at most <c>maxParallel</c> (2) episodes at a time, Wi-Fi/unmetered-only gate
/// (<see cref="INetworkCostProvider"/> + <see cref="AppSettings.DownloadWifiOnly"/>), three automatic retries with 30 s / 2 min /
/// 8 min backoff (each retry re-resolves the stream through the provider chain, so expired links get fresh ones), state persisted
/// through <see cref="IDownloadRepository"/>. Mirrors <c>DownloadCoordinator</c> in feature/downloads (minus the Media3 /
/// foreground-service / watchdog parts). Call <see cref="StartAsync"/> once at startup to resume interrupted downloads.
/// </summary>
public sealed class DownloadManager : IDownloadController, IDisposable
{
    public const int DefaultMaxParallel = 2;

    private readonly IDownloadRepository _repo;
    private readonly DownloadPlanner _planner;
    private readonly HlsDownloader _hls;
    private readonly ProgressiveDownloader _progressive;
    private readonly ISettingsStore _settings;
    private readonly INetworkCostProvider _network;
    private readonly IAppPaths _paths;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxParallel;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new();
    private readonly LinkedList<Job> _queue = new();
    private int _running;
    private bool _disposed;
    private int _waiting;
    private TaskCompletionSource _idle = NewCompletedSource();

    public DownloadManager(
        IDownloadRepository repository,
        DownloadPlanner planner,
        HlsDownloader hls,
        ProgressiveDownloader progressive,
        ISettingsStore settings,
        INetworkCostProvider network,
        IAppPaths paths,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxParallel = DefaultMaxParallel)
    {
        _repo = repository;
        _planner = planner;
        _hls = hls;
        _progressive = progressive;
        _settings = settings;
        _network = network;
        _paths = paths;
        _clock = clock;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
        _maxParallel = Math.Max(1, maxParallel);
        _network.Changed += OnNetworkChanged;
        _settings.Changed += OnSettingsChanged;
    }

    /// <summary>True while queued downloads wait because the network is offline or metered (with Wi-Fi only on).</summary>
    public bool WaitingForNetwork => Volatile.Read(ref _waiting) != 0;

    public event EventHandler<bool>? WaitingForNetworkChanged;

    /// <summary>One-shot user-visible errors (e.g. the stream lookup failed).</summary>
    public event EventHandler<string>? Message;

    /// <summary>Root folder: <see cref="AppSettings.DownloadDirectory"/> when set, else <see cref="IAppPaths.DownloadsDirectory"/>.</summary>
    public string RootDirectory =>
        _settings.Current.DownloadDirectory is { Length: > 0 } custom && !string.IsNullOrWhiteSpace(custom) ? custom : _paths.DownloadsDirectory;

    private string DirectoryOf(string providerId, string animeId, string episodeNumber) =>
        DownloadIds.EpisodeDirectory(RootDirectory, providerId, animeId, episodeNumber);

    // ---- jobs ----

    private enum JobStatus
    {
        Queued,
        Running,
        RetryWait,
    }

    private enum CancelReason
    {
        None,
        Pause,
        Remove,
        Network,
    }

    private sealed class Job
    {
        public Job(string providerId, string animeId, string episodeNumber)
        {
            ProviderId = providerId;
            AnimeId = animeId;
            EpisodeNumber = episodeNumber;
            Key = DownloadIds.EpisodeKey(providerId, animeId, episodeNumber);
        }

        public string ProviderId { get; }

        public string AnimeId { get; }

        public string EpisodeNumber { get; }

        public string Key { get; }

        public JobStatus Status;
        public bool ForceResolve;
        public int Attempts;
        public CancelReason Reason;
        public CancellationTokenSource? Cts;
        public CancellationTokenSource? RetryCts;
        public TaskCompletionSource RunDone = NewCompletedSource();
        public readonly SemaphoreSlim Gate = new(1, 1);
        public bool Terminal;
        public bool HasProgress;
        public long Bytes;
        public float Percent;
        public long LastWriteTimestamp;
        public float LastWritePercent;
        public int WritePending;
    }

    private static TaskCompletionSource NewCompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private static TaskCompletionSource NewPendingSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when no download is queued, running or waiting for a retry (not while waiting for the network).</summary>
    public Task WaitForIdleAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            task = _idle.Task;
        }

        return task.WaitAsync(cancellationToken);
    }

    // ---- startup ----

    /// <summary>Re-queues rows that were queued or downloading when the app last stopped (oldest first).</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var items = await _repo.GetAllAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in items.OrderBy(i => i.CreatedAt))
        {
            if (item.State == DownloadState.Downloading)
            {
                await _repo.UpdateProgressAsync(
                    item.ProviderId, item.AnimeId, item.EpisodeNumber, DownloadState.Queued, item.Bytes, item.Percent, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (item.State.IsActive())
            {
                QueueJob(item.ProviderId, item.AnimeId, item.EpisodeNumber, forceResolve: false);
            }
        }
    }

    // ---- IDownloadController ----

    public async Task EnqueueAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        // Runs on the manager's own lifetime so leaving the calling screen cannot strand a half-queued row.
        var task = Task.Run(() => EnqueueNowAsync(anime, episode, mode, _lifetime.Token), CancellationToken.None);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueAllAsync(Anime anime, IReadOnlyList<Episode> episodes, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var task = Task.Run(
            async () =>
            {
                foreach (var episode in episodes)
                {
                    await EnqueueNowAsync(anime, episode, mode, _lifetime.Token).ConfigureAwait(false);
                }
            },
            CancellationToken.None);
        await task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueNowAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken ct)
    {
        try
        {
            var key = DownloadIds.EpisodeKey(anime.ProviderId, anime.Id, episode.Number);
            bool hasJob;
            lock (_gate)
            {
                hasJob = _jobs.ContainsKey(key);
            }

            var existing = await _repo.GetAsync(anime.ProviderId, anime.Id, episode.Number, ct).ConfigureAwait(false);
            switch (existing?.State)
            {
                case DownloadState.Completed:
                    return;
                case DownloadState.Queued or DownloadState.Downloading when hasJob:
                    return;
                case DownloadState.Paused when existing?.StreamSet is not null && !hasJob:
                    await ResumeAsync(anime.ProviderId, anime.Id, episode.Number, ct).ConfigureAwait(false);
                    return;
            }

            if (hasJob)
            {
                return;
            }

            var baseItem = new DownloadItem(
                anime.ProviderId, anime.Id, episode.Id, episode.Number, anime.Title, anime.CoverUrl, mode,
                State: DownloadState.Queued,
                CreatedAt: existing is { CreatedAt: > 0 } ? existing.CreatedAt : _clock.UnixMilliseconds);
            await _repo.UpsertAsync(baseItem, ct).ConfigureAwait(false);
            try
            {
                var plan = await _planner.PlanAsync(anime, episode, mode, _settings.Current.Quality, ct).ConfigureAwait(false);
                await _repo.UpsertAsync(baseItem with { Quality = plan.Quality, StreamSet = plan.Snapshot }, ct).ConfigureAwait(false);
                await DeleteDirectoryAsync(DirectoryOf(anime.ProviderId, anime.Id, episode.Number), ct).ConfigureAwait(false); // fresh start
                QueueJob(anime.ProviderId, anime.Id, episode.Number, forceResolve: false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                var reason = $"Could not start download: {e.Message}";
                await _repo.UpdateProgressAsync(
                    anime.ProviderId, anime.Id, episode.Number, DownloadState.Failed, 0, 0f, reason, CancellationToken.None)
                    .ConfigureAwait(false);
                Message?.Invoke(this, reason);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Best effort: repository trouble must not escape (contract: never throws except cancellation).
            Message?.Invoke(this, $"Could not queue download: {e.Message}");
        }
    }

    public async Task PauseAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        var key = DownloadIds.EpisodeKey(providerId, animeId, episodeNumber);
        Job? job;
        lock (_gate)
        {
            _jobs.TryGetValue(key, out job);
        }

        if (job is null)
        {
            var item = await _repo.GetAsync(providerId, animeId, episodeNumber, cancellationToken).ConfigureAwait(false);
            if (item is not null && item.State.IsActive())
            {
                await _repo.UpdateProgressAsync(
                    providerId, animeId, episodeNumber, DownloadState.Paused, item.Bytes, item.Percent, null, cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        Task? wait = null;
        var writeNow = false;
        lock (_gate)
        {
            job.Reason = CancelReason.Pause;
            job.RetryCts?.Cancel();
            if (job.Status == JobStatus.Running)
            {
                job.Cts?.Cancel();
                wait = job.RunDone.Task;
            }
            else
            {
                _queue.Remove(job);
                RemoveJobLocked(job);
                writeNow = true;
            }
        }

        if (writeNow)
        {
            await WriteStateAsync(job, DownloadState.Paused, null, terminal: true).ConfigureAwait(false);
        }

        if (wait is not null)
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Pump();
    }

    public async Task ResumeAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        var key = DownloadIds.EpisodeKey(providerId, animeId, episodeNumber);
        lock (_gate)
        {
            if (_jobs.ContainsKey(key))
            {
                return;
            }
        }

        var item = await _repo.GetAsync(providerId, animeId, episodeNumber, cancellationToken).ConfigureAwait(false);
        if (item is null || item.State == DownloadState.Completed)
        {
            return;
        }

        var forceResolve = item.State == DownloadState.Failed || item.StreamSet is null;
        await _repo.UpdateProgressAsync(
            providerId, animeId, episodeNumber, DownloadState.Queued, item.Bytes, item.Percent, null, cancellationToken)
            .ConfigureAwait(false);
        QueueJob(providerId, animeId, episodeNumber, forceResolve);
    }

    public async Task RemoveAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        var key = DownloadIds.EpisodeKey(providerId, animeId, episodeNumber);
        Task? wait = null;
        lock (_gate)
        {
            if (_jobs.TryGetValue(key, out var job))
            {
                job.Reason = CancelReason.Remove;
                job.RetryCts?.Cancel();
                if (job.Status == JobStatus.Running)
                {
                    job.Cts?.Cancel();
                    wait = job.RunDone.Task;
                }
                else
                {
                    _queue.Remove(job);
                    RemoveJobLocked(job);
                }
            }
        }

        if (wait is not null)
        {
            await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        await _repo.DeleteAsync(providerId, animeId, episodeNumber, cancellationToken).ConfigureAwait(false);
        await DeleteDirectoryAsync(DirectoryOf(providerId, animeId, episodeNumber), cancellationToken).ConfigureAwait(false);
        Pump();
    }

    // ---- scheduling ----

    private bool NetworkAllowed() => _network.Current switch
    {
        NetworkCost.Unmetered => true,
        NetworkCost.Metered => !_settings.Current.DownloadWifiOnly,
        _ => false,
    };

    private void OnNetworkChanged(object? sender, NetworkCost cost) => OnEnvironmentChanged();

    private void OnSettingsChanged(object? sender, AppSettings settings) => OnEnvironmentChanged();

    private void OnEnvironmentChanged()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (!NetworkAllowed())
            {
                foreach (var job in _jobs.Values)
                {
                    if (job.Status == JobStatus.Running && job.Reason == CancelReason.None)
                    {
                        job.Reason = CancelReason.Network;
                        job.Cts?.Cancel();
                    }
                }
            }
        }

        Pump();
    }

    private bool QueueJob(string providerId, string animeId, string episodeNumber, bool forceResolve)
    {
        var job = new Job(providerId, animeId, episodeNumber) { ForceResolve = forceResolve };
        lock (_gate)
        {
            if (_disposed || _jobs.ContainsKey(job.Key))
            {
                return false;
            }

            _jobs[job.Key] = job;
            if (_idle.Task.IsCompleted)
            {
                _idle = NewPendingSource();
            }

            job.Status = JobStatus.Queued;
            _queue.AddLast(job);
        }

        Pump();
        return true;
    }

    private void RemoveJobLocked(Job job)
    {
        if (_jobs.TryGetValue(job.Key, out var current) && ReferenceEquals(current, job))
        {
            _jobs.Remove(job.Key);
        }

        if (_jobs.Count == 0)
        {
            _idle.TrySetResult();
        }
    }

    private void RemoveJob(Job job)
    {
        lock (_gate)
        {
            _queue.Remove(job);
            RemoveJobLocked(job);
        }
    }

    private void Pump()
    {
        List<Job>? started = null;
        bool waiting;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var allowed = NetworkAllowed();
            while (allowed && _running < _maxParallel && _queue.First is { } node)
            {
                _queue.RemoveFirst();
                var job = node.Value;
                job.Status = JobStatus.Running;
                job.Reason = CancelReason.None;
                job.Cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                job.RunDone = NewPendingSource();
                _running++;
                (started ??= []).Add(job);
            }

            waiting = !allowed && _queue.Count > 0;
        }

        if (started is not null)
        {
            foreach (var job in started)
            {
                var captured = job;
                _ = Task.Run(() => RunJobAsync(captured), CancellationToken.None);
            }
        }

        SetWaiting(waiting);
    }

    private void SetWaiting(bool value)
    {
        var next = value ? 1 : 0;
        if (Interlocked.Exchange(ref _waiting, next) != next)
        {
            WaitingForNetworkChanged?.Invoke(this, value);
        }
    }

    private async Task RunJobAsync(Job job)
    {
        var ct = job.Cts!.Token;
        try
        {
            try
            {
                await ExecuteAsync(job, ct).ConfigureAwait(false);
                RemoveJob(job);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await HandleCancelAsync(job).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                if (ReasonOf(job) != CancelReason.None)
                {
                    await HandleCancelAsync(job).ConfigureAwait(false);
                }
                else
                {
                    await HandleFailureAsync(job, e).ConfigureAwait(false);
                }
            }
        }
        catch (Exception)
        {
            // Bookkeeping failed (repository down); the row is repaired by the next StartAsync.
            RemoveJob(job);
        }
        finally
        {
            lock (_gate)
            {
                _running--;
            }

            job.RunDone.TrySetResult();
            Pump();
        }
    }

    private CancelReason ReasonOf(Job job)
    {
        lock (_gate)
        {
            return job.Reason;
        }
    }

    private async Task HandleCancelAsync(Job job)
    {
        switch (ReasonOf(job))
        {
            case CancelReason.Pause:
                await WriteStateAsync(job, DownloadState.Paused, null, terminal: true).ConfigureAwait(false);
                RemoveJob(job);
                break;
            case CancelReason.Remove:
                RemoveJob(job);
                break;
            case CancelReason.Network:
                await WriteStateAsync(job, DownloadState.Queued, null, terminal: true).ConfigureAwait(false);
                lock (_gate)
                {
                    job.Reason = CancelReason.None;
                    job.Status = JobStatus.Queued;
                    _queue.AddFirst(job);
                }

                break;
            default:
                break; // app shutting down: the row stays Queued/Downloading and StartAsync resumes it
        }
    }

    private async Task HandleFailureAsync(Job job, Exception error)
    {
        var reason = DownloadPolicy.DescribeFailure(error);
        if (!DownloadPolicy.ShouldRetry(job.Attempts))
        {
            await WriteStateAsync(job, DownloadState.Failed, reason, terminal: true).ConfigureAwait(false);
            RemoveJob(job);
            return;
        }

        job.Attempts++;
        job.ForceResolve = true;
        var attempt = job.Attempts;
        await WriteStateAsync(job, DownloadState.Queued, DownloadPolicy.RetryingText(attempt), terminal: true).ConfigureAwait(false);

        CancellationTokenSource? retryCts = null;
        lock (_gate)
        {
            if (job.Reason == CancelReason.None)
            {
                job.Status = JobStatus.RetryWait;
                retryCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                job.RetryCts = retryCts;
            }
        }

        if (retryCts is null)
        {
            await HandleCancelAsync(job).ConfigureAwait(false); // paused/removed/network-gated while the failure was being recorded
            return;
        }

        _ = RetryAfterDelayAsync(job, DownloadPolicy.Backoff(attempt), retryCts.Token);
    }

    private async Task RetryAfterDelayAsync(Job job, TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_gate)
        {
            if (ct.IsCancellationRequested || job.Status != JobStatus.RetryWait
                || !_jobs.TryGetValue(job.Key, out var current) || !ReferenceEquals(current, job))
            {
                return;
            }

            job.Status = JobStatus.Queued;
            job.RetryCts = null;
            _queue.AddLast(job);
        }

        Pump();
    }

    // ---- one run ----

    private async Task ExecuteAsync(Job job, CancellationToken ct)
    {
        var item = await _repo.GetAsync(job.ProviderId, job.AnimeId, job.EpisodeNumber, ct).ConfigureAwait(false);
        if (item is null)
        {
            return; // removed meanwhile
        }

        var dir = DirectoryOf(item.ProviderId, item.AnimeId, item.EpisodeNumber);
        if (item.StreamSet is null || job.ForceResolve)
        {
            var anime = new Anime
            {
                Id = item.AnimeId,
                ProviderId = item.ProviderId,
                Title = item.Title,
                CoverUrl = item.CoverUrl,
                MalId = item.StreamSet?.MalId,
            };
            var plan = await _planner
                .PlanAsync(anime, new Episode(item.EpisodeId, item.EpisodeNumber), item.Mode, _settings.Current.Quality, ct)
                .ConfigureAwait(false);
            if (item.StreamSet is not null && plan.Quality != item.Quality)
            {
                await DeleteDirectoryAsync(dir, ct).ConfigureAwait(false); // another rendition: stored segments do not fit
            }

            item = item with { Quality = plan.Quality, StreamSet = plan.Snapshot };
            await _repo.UpsertAsync(item, ct).ConfigureAwait(false);
            job.ForceResolve = false;
        }

        await WriteStateAsync(job, DownloadState.Downloading, null, terminal: false).ConfigureAwait(false);

        var set = item.StreamSet!;
        var parts = DownloadPlanner.Parts(job.Key, set);
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("No downloadable stream found");
        }

        Directory.CreateDirectory(dir);
        long doneBytes = 0;
        var mainPercent = 0f;
        var localPaths = new Dictionary<string, string>();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            var name = DownloadIds.PartName(part.Id);
            var isMain = DownloadIds.IsMain(part.Id);
            var baseBytes = doneBytes;
            Action<DownloadProgress> report = p =>
            {
                if (isMain)
                {
                    mainPercent = p.Percent;
                }

                OnProgress(job, baseBytes + p.Bytes, Math.Min(mainPercent, 0.99f));
            };

            var url = new Uri(part.Url);
            string path;
            long bytes;
            if (part.IsHls)
            {
                var result = await _hls.DownloadAsync(url, dir, name, set.Headers, report, ct).ConfigureAwait(false);
                path = result.PlaylistPath;
                bytes = result.Bytes;
            }
            else
            {
                var fallback = isMain ? ".mp4" : name.StartsWith("sub", StringComparison.Ordinal) ? ".vtt" : ".m4a";
                path = Path.Combine(dir, name + ExtensionOf(url, fallback));
                bytes = await _progressive.DownloadAsync(url, path, set.Headers, report, ct).ConfigureAwait(false);
            }

            localPaths[part.Id] = path;
            doneBytes += bytes;
        }

        var snapshot = BuildLocalSnapshot(set, parts, localPaths, item.Quality);
        await job.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            job.Terminal = true;
            var current = await _repo.GetAsync(job.ProviderId, job.AnimeId, job.EpisodeNumber, CancellationToken.None).ConfigureAwait(false);
            if (current is not null)
            {
                await _repo.UpsertAsync(
                    current with
                    {
                        State = DownloadState.Completed,
                        Bytes = doneBytes,
                        Percent = 1f,
                        Quality = item.Quality,
                        StreamSet = snapshot,
                        FailureReason = null,
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally
        {
            job.Gate.Release();
        }
    }

    /// <summary>
    /// Snapshot of what is on disk: single local rendition (file URI of the rewritten playlist or the file), local external audio
    /// and subtitles, no headers, no master. Playing it needs no provider call and no network.
    /// </summary>
    internal static StreamSet BuildLocalSnapshot(
        StreamSet set, IReadOnlyList<PartRequest> parts, IReadOnlyDictionary<string, string> localPaths, int? quality)
    {
        var key = parts[0].Id;
        var audioId = DownloadIds.AudioId(key);
        var subtitles = new List<SubtitleTrack>();
        var index = 0;
        foreach (var track in set.Subtitles.DistinctBy(t => t.Url))
        {
            if (localPaths.TryGetValue(DownloadIds.SubtitleId(key, index++), out var local))
            {
                subtitles.Add(track with { Url = FileUri(local) });
            }
        }

        return set with
        {
            MasterUrl = null,
            Variants = [new StreamVariant(quality, FileUri(localPaths[key]))],
            Headers = new Dictionary<string, string>(),
            ExternalAudioUrl = localPaths.TryGetValue(audioId, out var audio) ? FileUri(audio) : null,
            Subtitles = subtitles,
            SpriteVttUrl = null,
        };
    }

    private static string FileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

    private static string ExtensionOf(Uri url, string fallback)
    {
        var ext = Path.GetExtension(url.AbsolutePath);
        return ext.Length is >= 2 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) ? ext.ToLowerInvariant() : fallback;
    }

    // ---- persistence ----

    private void OnProgress(Job job, long bytes, float percent)
    {
        bool due;
        lock (job)
        {
            job.Bytes = bytes;
            job.Percent = percent;
            job.HasProgress = true;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            due = now - job.LastWriteTimestamp >= System.Diagnostics.Stopwatch.Frequency
                || percent - job.LastWritePercent >= 0.01f;
            if (due)
            {
                job.LastWriteTimestamp = now;
                job.LastWritePercent = percent;
            }
        }

        if (due && Interlocked.Exchange(ref job.WritePending, 1) == 0)
        {
            _ = FlushProgressAsync(job);
        }
    }

    private async Task FlushProgressAsync(Job job)
    {
        await job.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref job.WritePending, 0);
            if (job.Terminal)
            {
                return;
            }

            long bytes;
            float percent;
            lock (job)
            {
                bytes = job.Bytes;
                percent = job.Percent;
            }

            await TryUpdateAsync(job, DownloadState.Downloading, bytes, percent, null).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private async Task WriteStateAsync(Job job, DownloadState state, string? reason, bool terminal)
    {
        await job.Gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            job.Terminal = terminal;
            long bytes;
            float percent;
            bool known;
            lock (job)
            {
                bytes = job.Bytes;
                percent = job.Percent;
                known = job.HasProgress;
            }

            if (!known)
            {
                try
                {
                    var current = await _repo.GetAsync(job.ProviderId, job.AnimeId, job.EpisodeNumber, CancellationToken.None).ConfigureAwait(false);
                    if (current is not null)
                    {
                        bytes = current.Bytes;
                        percent = current.Percent;
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // keep zeros
                }
            }

            await TryUpdateAsync(job, state, bytes, percent, reason).ConfigureAwait(false);
        }
        finally
        {
            job.Gate.Release();
        }
    }

    private async Task TryUpdateAsync(Job job, DownloadState state, long bytes, float percent, string? reason)
    {
        try
        {
            await _repo.UpdateProgressAsync(
                job.ProviderId, job.AnimeId, job.EpisodeNumber, state, bytes, percent, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Progress is best effort; the next write (or StartAsync) repairs the row.
        }
    }

    private static async Task DeleteDirectoryAsync(string directory, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                var parent = Path.GetDirectoryName(directory);
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }

                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 2)
                {
                    return; // locked by a player; leftover files are harmless
                }

                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _network.Changed -= OnNetworkChanged;
        _settings.Changed -= OnSettingsChanged;
        _lifetime.Cancel();
    }
}
