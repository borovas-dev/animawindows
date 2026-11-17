#pragma warning disable xUnit1051
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tracking;

namespace AnimaWindows.Core.Tests.Tracking;

public sealed class SyncSchedulerTests : IDisposable
{
    private readonly SyncEnv _env = new();

    public void Dispose() => _env.Dispose();

    [Fact]
    public void Backoff_DoublesFromThirtySecondsUpToFifteenMinutes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), SyncScheduler.BackoffFor(1));
        Assert.Equal(TimeSpan.FromMinutes(1), SyncScheduler.BackoffFor(2));
        Assert.Equal(TimeSpan.FromMinutes(2), SyncScheduler.BackoffFor(3));
        Assert.Equal(TimeSpan.FromMinutes(15), SyncScheduler.BackoffFor(20));
    }

    [Fact]
    public async Task RunOnce_SkipsWhileOffline_AndDrainsWhenOnline()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 2, null);
        var network = new FakeNetwork();
        network.Set(NetworkCost.Offline);
        using var scheduler = new SyncScheduler(_env.Repo, network, TimeSpan.FromHours(1));

        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Empty(_env.Http.Requests);

        network.Set(NetworkCost.Unmetered);
        _env.Http.Enqueue("""{"data":{"Media":{"id":1,"episodes":12,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");
        await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, _env.Http.Requests.Count);
        Assert.Equal(0, _env.Repo.State.Pending);
    }

    [Fact]
    public async Task RetryRequested_SchedulesDelayedRun_WithBackoff()
    {
        _env.Tokens.Put(SyncService.AniList, new Tokens("TOK"));
        await _env.Repo.RecordAsync(5, 2, null);
        var network = new FakeNetwork();
        var delays = new List<TimeSpan>();
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scheduler = new SyncScheduler(
            _env.Repo,
            network,
            TimeSpan.FromHours(1),
            (d, _) =>
            {
                lock (delays)
                {
                    delays.Add(d);
                }

                ran.TrySetResult();
                return Task.CompletedTask;
            });
        _env.Http.Enqueue("busy", System.Net.HttpStatusCode.TooManyRequests);
        _env.Http.Enqueue("""{"data":{"Media":{"id":1,"episodes":12,"mediaListEntry":null}}}""");
        _env.Http.Enqueue("""{"data":{"SaveMediaListEntry":{"id":1}}}""");

        // Start() drains the leftover queue immediately: the first answer (429) is transient and requests a retry.
        scheduler.Start();
        await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (delays)
        {
            Assert.Equal(TimeSpan.FromSeconds(30), delays[0]);
        }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_env.Repo.State.Pending != 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal(0, _env.Repo.State.Pending);
    }
}
