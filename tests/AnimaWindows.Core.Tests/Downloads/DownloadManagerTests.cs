#pragma warning disable xUnit1051
using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tests.Tracking;

namespace AnimaWindows.Core.Tests.Downloads;

public sealed class DownloadManagerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeCdn _cdn = new();
    private readonly InMemoryDownloadRepository _repo = new();
    private readonly FakeSettings _settings = new();
    private readonly FakeChain _chain = new();
    private readonly FakeNetwork _network = new();
    private readonly FakeClock _clock = new(5_000);
    private readonly FakePaths _paths;
    private readonly List<TimeSpan> _delays = [];
    private readonly List<DownloadManager> _managers = [];

    public DownloadManagerTests()
    {
        _paths = new FakePaths(_dir.Path);
        _chain.Resolve = (_, episode, _) => Media.SetFor(_cdn, "ep" + episode.Number, 2);
    }

    public void Dispose()
    {
        foreach (var m in _managers)
        {
            m.Dispose();
        }

        _dir.Dispose();
    }

    private DownloadManager NewManager(Func<TimeSpan, CancellationToken, Task>? delay = null, int maxParallel = 2)
    {
        var http = new HttpClient(_cdn);
        var manager = new DownloadManager(
            _repo,
            new DownloadPlanner(_chain, http),
            new HlsDownloader(http, 1),
            new ProgressiveDownloader(http),
            _settings,
            _network,
            _paths,
            _clock,
            delay ?? ((d, _) =>
            {
                lock (_delays)
                {
                    _delays.Add(d);
                }

                return Task.CompletedTask;
            }),
            maxParallel);
        _managers.Add(manager);
        return manager;
    }

    private static Episode Ep(int n) => new("id" + n, n.ToString());

    private Task<DownloadItem?> Row(int n, string show = "show") => _repo.GetAsync("prov", show, n.ToString());

    private static string LocalPath(string fileUri) => new Uri(fileUri).LocalPath;

    [Fact]
    public async Task Enqueue_DownloadsEpisode_AndStoresLocalSnapshot()
    {
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Completed, row.State);
        Assert.Equal(1f, row.Percent);
        Assert.Equal(1080, row.Quality); // Quality.Best
        Assert.Equal(2 * 100, row.Bytes);
        Assert.Equal(5_000, row.CreatedAt);
        Assert.Null(row.FailureReason);
        Assert.True(row.IsPlayable);

        var variant = Assert.Single(row.StreamSet!.Variants);
        Assert.Equal(1080, variant.Quality);
        Assert.StartsWith("file:", variant.Url);
        Assert.Null(row.StreamSet.MasterUrl);
        Assert.Empty(row.StreamSet.Headers);

        var playlist = LocalPath(variant.Url);
        Assert.True(File.Exists(playlist));
        var expectedDir = DownloadIds.EpisodeDirectory(_paths.DownloadsDirectory, "prov", "show", "1");
        Assert.Equal(Path.Combine(expectedDir, "main.m3u8"), playlist);
        Assert.True(File.Exists(Path.Combine(expectedDir, "main", "seg00001.ts")));

        // headers of the stream were sent on every request; only the chosen rendition was fetched
        Assert.All(_cdn.Requests, r => Assert.Equal("https://site.test/", r.Headers["Referer"]));
        Assert.DoesNotContain(_cdn.Requests, r => r.Path.Contains("/720/") || r.Path.Contains("/360/"));
        Assert.Equal(1, _chain.Calls);
    }

    [Fact]
    public async Task Enqueue_UsesQualityPreference_NearestLower()
    {
        await _settings.UpdateAsync(s => s with { Quality = QualityPreference.P480 });
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        var row = (await Row(1))!;
        Assert.Equal(360, row.Quality);
        Assert.Equal(360, row.StreamSet!.Variants.Single().Quality);
    }

    [Fact]
    public async Task Enqueue_KeepsSidecars_AudioAndSubtitles()
    {
        _cdn.Put("/aux/audio.m3u8", Media.Playlist(1));
        _cdn.Files["/aux/seg0.ts"] = Media.Bytes(7, 50);
        _cdn.Files["/aux/en.vtt"] = System.Text.Encoding.UTF8.GetBytes("WEBVTT\n");
        _chain.Resolve = (_, _, _) => Media.SetFor(_cdn, "ep1", 1) with
        {
            ExternalAudioUrl = "https://cdn.test/aux/audio.m3u8",
            Subtitles = [new SubtitleTrack("https://cdn.test/aux/en.vtt", "en", "English", true)],
            MalId = 42,
        };
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Dub);
        await Media.Within(manager.WaitForIdleAsync());

        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Completed, row.State);
        Assert.Equal(TranslationMode.Dub, row.Mode);
        Assert.Equal(42, row.StreamSet!.MalId);
        Assert.True(File.Exists(LocalPath(row.StreamSet.ExternalAudioUrl!)));
        var sub = Assert.Single(row.StreamSet.Subtitles);
        Assert.Equal("English", sub.Label);
        Assert.True(sub.IsDefault);
        Assert.Equal("WEBVTT\n", await File.ReadAllTextAsync(LocalPath(sub.Url)));
        Assert.Equal(100 + 50 + 7, row.Bytes);
    }

    [Fact]
    public async Task Enqueue_ProgressiveMp4_Works()
    {
        _cdn.Files["/f/ep.mp4"] = Media.Bytes(1, 777);
        _chain.Resolve = (_, _, _) => new StreamSet { Variants = [new StreamVariant(720, "https://cdn.test/f/ep.mp4")] };
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Completed, row.State);
        Assert.Equal(777, row.Bytes);
        Assert.EndsWith("main.mp4", LocalPath(row.StreamSet!.Variants.Single().Url));
    }

    [Fact]
    public async Task Enqueue_UsesCustomDownloadDirectory_FromSettings()
    {
        var custom = Path.Combine(_dir.Path, "custom-root");
        await _settings.UpdateAsync(s => s with { DownloadDirectory = custom });
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        var row = (await Row(1))!;
        Assert.StartsWith(new Uri(custom).LocalPath, LocalPath(row.StreamSet!.Variants.Single().Url));
        Assert.Equal(custom, manager.RootDirectory);
    }

    [Fact]
    public async Task Enqueue_IsIdempotent_ForCompletedAndActive()
    {
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub); // already queued/active
        await Media.Within(manager.WaitForIdleAsync());
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub); // completed
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(1, _chain.Calls);
    }

    [Fact]
    public async Task Enqueue_StreamLookupFailure_BecomesFailedRow_WithoutRetry()
    {
        _chain.Resolve = (_, _, _) => throw new ProviderException("all providers down");
        var manager = NewManager();
        var messages = new List<string>();
        manager.Message += (_, m) => messages.Add(m);

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub); // must not throw

        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Failed, row.State);
        Assert.Contains("all providers down", row.FailureReason);
        Assert.Single(messages);
        Assert.Empty(_delays);
        await Media.Within(manager.WaitForIdleAsync());
    }

    [Fact]
    public async Task EnqueueAll_QueuesEveryEpisode()
    {
        var manager = NewManager();
        await manager.EnqueueAllAsync(Media.Show(), [Ep(1), Ep(2), Ep(3)], TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        foreach (var n in new[] { 1, 2, 3 })
        {
            Assert.Equal(DownloadState.Completed, (await Row(n))!.State);
        }
    }

    // ---- concurrency ----

    [Fact]
    public async Task RunsAtMostTwoDownloadsInParallel()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new List<string>();
        _cdn.Before = r =>
        {
            if (r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal))
            {
                lock (blocked)
                {
                    blocked.Add(r.Path);
                }

                return release.Task;
            }

            return Task.CompletedTask;
        };
        var manager = NewManager();
        await manager.EnqueueAllAsync(Media.Show(), [Ep(1), Ep(2), Ep(3)], TranslationMode.Sub);

        await Media.WaitUntilAsync(() => { lock (blocked) { return blocked.Count == 2; } }, "two running downloads");
        await Task.Delay(200); // a third must not start while two are running
        Assert.Equal(2, _cdn.Requests.Select(r => r.Path.Split('/')[1]).Distinct().Count());
        Assert.Equal(DownloadState.Queued, (await Row(3))!.State);

        release.SetResult();
        await Media.Within(manager.WaitForIdleAsync());
        foreach (var n in new[] { 1, 2, 3 })
        {
            Assert.Equal(DownloadState.Completed, (await Row(n))!.State);
        }
    }

    // ---- network gate ----

    [Fact]
    public async Task WifiOnly_OnMeteredNetwork_WaitsUntilUnmetered()
    {
        _network.Set(NetworkCost.Metered);
        var manager = NewManager();
        var waiting = new List<bool>();
        manager.WaitingForNetworkChanged += (_, w) => waiting.Add(w);

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Task.Delay(100);

        Assert.Equal(DownloadState.Queued, (await Row(1))!.State);
        Assert.DoesNotContain(_cdn.Requests, r => r.Path.EndsWith(".ts", StringComparison.Ordinal)); // only stream resolution happened
        Assert.True(manager.WaitingForNetwork);

        _network.Set(NetworkCost.Unmetered);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.False(manager.WaitingForNetwork);
        Assert.Equal(new[] { true, false }, waiting);
    }

    [Fact]
    public async Task MeteredAllowed_WhenWifiOnlyIsOff()
    {
        await _settings.UpdateAsync(s => s with { DownloadWifiOnly = false });
        _network.Set(NetworkCost.Metered);
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
    }

    [Fact]
    public async Task Offline_NeverDownloads_EvenWithoutWifiOnly()
    {
        await _settings.UpdateAsync(s => s with { DownloadWifiOnly = false });
        _network.Set(NetworkCost.Offline);
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Task.Delay(100);

        Assert.Equal(DownloadState.Queued, (await Row(1))!.State);
        Assert.True(manager.WaitingForNetwork);
    }

    [Fact]
    public async Task TurningWifiOnlyOff_ReleasesWaitingDownloads()
    {
        _network.Set(NetworkCost.Metered);
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Task.Delay(50);
        Assert.Equal(DownloadState.Queued, (await Row(1))!.State);

        await _settings.UpdateAsync(s => s with { DownloadWifiOnly = false });
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
    }

    [Fact]
    public async Task LosingUnmeteredNetworkMidDownload_RequeuesAndResumesLater()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdn.Before = r =>
        {
            if (r.Path.EndsWith("/seg1.ts", StringComparison.Ordinal) && !gate.Task.IsCompleted)
            {
                blocked.TrySetResult();
                return gate.Task;
            }

            return Task.CompletedTask;
        };
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(blocked.Task);

        _network.Set(NetworkCost.Metered);
        await Media.WaitUntilAsync(() => manager.WaitingForNetwork, "waiting flag");
        Assert.Equal(DownloadState.Queued, (await Row(1))!.State);

        gate.SetResult();
        _network.Set(NetworkCost.Unmetered);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.Equal(1, _cdn.Count(r => r.Path.EndsWith("/1080/seg0.ts", StringComparison.Ordinal))); // already stored: not fetched again
        Assert.Empty(_delays); // a network pause is not a failure
    }

    // ---- retry / backoff ----

    [Fact]
    public async Task Failure_RetriesThreeTimes_With30s_2min_8min_Backoff_ThenStaysFailed()
    {
        _cdn.Fault = r => r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : null;
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(8) }, _delays);
        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Failed, row.State);
        Assert.Equal("Server error (HTTP 500)", row.FailureReason);
        Assert.Equal(4, _chain.Calls); // initial + one re-resolve per retry (fresh links)
        Assert.Equal(4, _cdn.Count(r => r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal)));
        Assert.Contains(_repo.Log, e => e.State == DownloadState.Queued && e.Reason == "Retrying (3/3)");
    }

    [Fact]
    public async Task Failure_ThenSuccess_CompletesAfterOneBackoff()
    {
        var failures = 1;
        _cdn.Fault = r => r.Path.EndsWith("/seg1.ts", StringComparison.Ordinal) && Interlocked.Decrement(ref failures) >= 0
            ? HttpStatusCode.ServiceUnavailable
            : null;
        var manager = NewManager();

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, _delays);
        var row = (await Row(1))!;
        Assert.Equal(DownloadState.Completed, row.State);
        Assert.Null(row.FailureReason);
        Assert.Equal(1, _cdn.Count(r => r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal))); // kept from the first attempt
    }

    [Fact]
    public async Task ManualResume_OfFailedDownload_StartsOver_WithFreshRetries()
    {
        var broken = true;
        _cdn.Fault = r => broken && r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal) ? HttpStatusCode.Forbidden : null;
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());
        Assert.Equal(DownloadState.Failed, (await Row(1))!.State);
        Assert.Contains("link may have expired", (await Row(1))!.FailureReason);

        broken = false;
        await manager.ResumeAsync("prov", "show", "1");
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
    }

    [Fact]
    public async Task RetryWaiting_CanBePaused()
    {
        var manager = NewManager((d, ct) => Task.Delay(Timeout.Infinite, ct)); // backoff never elapses
        _cdn.Fault = r => r.Path.EndsWith("/seg0.ts", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError : null;
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.WaitUntilAsync(() => _repo.Log.Any(e => e.Reason == "Retrying (1/3)"), "retry wait");

        await manager.PauseAsync("prov", "show", "1");

        Assert.Equal(DownloadState.Paused, (await Row(1))!.State);
        await Media.Within(manager.WaitForIdleAsync());
    }

    // ---- pause / resume / remove ----

    [Fact]
    public async Task Pause_KeepsFiles_AndResumeContinuesWithoutRefetching()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdn.Before = r =>
        {
            if (r.Path.EndsWith("/seg1.ts", StringComparison.Ordinal) && !gate.Task.IsCompleted)
            {
                blocked.TrySetResult();
                return gate.Task;
            }

            return Task.CompletedTask;
        };
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(blocked.Task);

        await manager.PauseAsync("prov", "show", "1");

        var paused = (await Row(1))!;
        Assert.Equal(DownloadState.Paused, paused.State);
        Assert.Null(paused.FailureReason);
        Assert.NotNull(paused.StreamSet); // remote snapshot kept for resuming
        Assert.StartsWith("https:", paused.StreamSet.Variants.Single().Url);
        var episodeDir = DownloadIds.EpisodeDirectory(_paths.DownloadsDirectory, "prov", "show", "1");
        Assert.True(File.Exists(Path.Combine(episodeDir, "main", "seg00000.ts")));
        await Media.Within(manager.WaitForIdleAsync());

        gate.SetResult();
        await manager.ResumeAsync("prov", "show", "1");
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.Equal(1, _cdn.Count(r => r.Path.EndsWith("/1080/seg0.ts", StringComparison.Ordinal)));
        Assert.Equal(2, _cdn.Count(r => r.Path.EndsWith("/1080/seg1.ts", StringComparison.Ordinal))); // the interrupted one is retried
        Assert.Equal(1, _chain.Calls); // resume does not call the provider again
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Pause_OfQueuedDownload_KeepsItOutOfTheQueue()
    {
        _network.Set(NetworkCost.Metered);
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);

        await manager.PauseAsync("prov", "show", "1");
        Assert.Equal(DownloadState.Paused, (await Row(1))!.State);
        await Media.Within(manager.WaitForIdleAsync());

        _network.Set(NetworkCost.Unmetered);
        await Task.Delay(100);
        Assert.Equal(DownloadState.Paused, (await Row(1))!.State); // not started by the network change

        await manager.ResumeAsync("prov", "show", "1");
        await Media.Within(manager.WaitForIdleAsync());
        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
    }

    [Fact]
    public async Task Enqueue_OfPausedDownload_Resumes()
    {
        _network.Set(NetworkCost.Metered);
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await manager.PauseAsync("prov", "show", "1");
        _network.Set(NetworkCost.Unmetered);

        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.Equal(1, _chain.Calls);
    }

    [Fact]
    public async Task Remove_DeletesFilesAndRow_OfCompletedDownload()
    {
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(manager.WaitForIdleAsync());
        var episodeDir = DownloadIds.EpisodeDirectory(_paths.DownloadsDirectory, "prov", "show", "1");
        Assert.True(Directory.Exists(episodeDir));

        await manager.RemoveAsync("prov", "show", "1");

        Assert.Null(await Row(1));
        Assert.False(Directory.Exists(episodeDir));
        Assert.False(Directory.Exists(Path.GetDirectoryName(episodeDir))); // empty show folder is cleaned up too
    }

    [Fact]
    public async Task Remove_CancelsRunningDownload_AndDeletesItsFiles()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _cdn.Before = r =>
        {
            if (r.Path.EndsWith("/seg1.ts", StringComparison.Ordinal))
            {
                blocked.TrySetResult();
                return Task.Delay(Timeout.Infinite);
            }

            return Task.CompletedTask;
        };
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        await Media.Within(blocked.Task);

        await manager.RemoveAsync("prov", "show", "1");

        Assert.Null(await Row(1));
        Assert.False(Directory.Exists(DownloadIds.EpisodeDirectory(_paths.DownloadsDirectory, "prov", "show", "1")));
        await Media.Within(manager.WaitForIdleAsync());
        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Remove_OfFailedRow_OnlyDeletesRow()
    {
        _chain.Resolve = (_, _, _) => throw new ProviderException("nope");
        var manager = NewManager();
        await manager.EnqueueAsync(Media.Show(), Ep(1), TranslationMode.Sub);
        Assert.Equal(DownloadState.Failed, (await Row(1))!.State);

        await manager.RemoveAsync("prov", "show", "1");

        Assert.Null(await Row(1));
    }

    // ---- startup ----

    [Fact]
    public async Task Start_ResumesInterruptedDownloads_OldestFirst()
    {
        var set1 = DownloadPlanner.CreatePlan(Media.SetFor(_cdn, "ep1", 2), QualityPreference.Best)!;
        await _repo.UpsertAsync(new DownloadItem("prov", "show", "id1", "1", "Show", State: DownloadState.Downloading, Percent: 0.5f, Bytes: 100, CreatedAt: 10, StreamSet: set1.Snapshot, Quality: 1080));
        await _repo.UpsertAsync(new DownloadItem("prov", "show", "id2", "2", "Show", State: DownloadState.Queued, CreatedAt: 20)); // lookup was interrupted: resolved again
        await _repo.UpsertAsync(new DownloadItem("prov", "show", "id3", "3", "Show", State: DownloadState.Paused, CreatedAt: 30));
        await _repo.UpsertAsync(new DownloadItem("prov", "show", "id4", "4", "Show", State: DownloadState.Failed, CreatedAt: 40, FailureReason: "x"));
        var manager = NewManager(maxParallel: 1);

        await manager.StartAsync();
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.Equal(DownloadState.Completed, (await Row(2))!.State);
        Assert.Equal(DownloadState.Paused, (await Row(3))!.State);
        Assert.Equal(DownloadState.Failed, (await Row(4))!.State);
        Assert.Equal(1, _chain.Calls); // only the row without a snapshot needed the provider
        var firstSeg = _cdn.Requests.First(r => r.Path.EndsWith(".ts", StringComparison.Ordinal)).Path;
        Assert.StartsWith("/ep1/", firstSeg);
    }

    [Fact]
    public async Task Start_WithStaleSnapshot_RetriesWithFreshLinks()
    {
        var stale = new StreamSet { Variants = [new StreamVariant(1080, "https://cdn.test/old/1080/index.m3u8")] };
        await _repo.UpsertAsync(new DownloadItem("prov", "show", "id1", "1", "Show", State: DownloadState.Queued, CreatedAt: 10, StreamSet: stale, Quality: 1080));
        var manager = NewManager();

        await manager.StartAsync();
        await Media.Within(manager.WaitForIdleAsync());

        Assert.Equal(DownloadState.Completed, (await Row(1))!.State);
        Assert.Equal(new[] { TimeSpan.FromSeconds(30) }, _delays); // 404 on the old link, then re-resolved
        Assert.Equal(1, _chain.Calls);
    }

    [Fact]
    public void BuildLocalSnapshot_PointsEverythingAtLocalFiles()
    {
        var set = Media.SetFor(_cdn, "e", 1) with
        {
            ExternalAudioUrl = "https://cdn.test/a.m3u8",
            Subtitles = [new SubtitleTrack("https://cdn.test/a.vtt", "pl", "Polski", false)],
            SpriteVttUrl = "https://cdn.test/sprite.vtt",
            Intro = new SkipInterval(1, 2),
        };
        var plan = DownloadPlanner.CreatePlan(set, QualityPreference.P720)!;
        var parts = DownloadPlanner.Parts("k", plan.Snapshot);
        var local = new Dictionary<string, string>
        {
            ["k"] = Path.Combine(_dir.Path, "main.m3u8"),
            ["k#audio"] = Path.Combine(_dir.Path, "audio.m3u8"),
            ["k#sub0"] = Path.Combine(_dir.Path, "sub0.vtt"),
        };

        var snapshot = DownloadManager.BuildLocalSnapshot(plan.Snapshot, parts, local, plan.Quality);

        Assert.Equal(720, snapshot.Variants.Single().Quality);
        Assert.All(new[] { snapshot.Variants.Single().Url, snapshot.ExternalAudioUrl!, snapshot.Subtitles.Single().Url }, u => Assert.StartsWith("file:", u));
        Assert.Equal("Polski", snapshot.Subtitles.Single().Label);
        Assert.Null(snapshot.SpriteVttUrl);
        Assert.Empty(snapshot.Headers);
        Assert.Equal(new SkipInterval(1, 2), snapshot.Intro); // provider skip data survives
    }
}
