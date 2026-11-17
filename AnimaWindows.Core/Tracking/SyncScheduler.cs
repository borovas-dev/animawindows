using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.Core.Tracking;

/// <summary>
/// Replaces Android WorkManager for tracker sync: a <see cref="PeriodicTimer"/> drains the queue regularly, a connectivity
/// change to online triggers an immediate run, and a transient failure (<see cref="SyncRepository.RetryRequested"/>) schedules
/// a retry with exponential backoff (30 s, 1 min, 2 min ... capped at 15 min). Runs are skipped while offline or idle.
/// </summary>
public sealed class SyncScheduler : IDisposable
{
    public static readonly TimeSpan DefaultPeriod = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);

    private readonly SyncRepository _sync;
    private readonly INetworkCostProvider _network;
    private readonly TimeSpan _period;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private int _retryAttempt;
    private int _retryScheduled;
    private int _started;
    private int _disposed;

    public SyncScheduler(
        SyncRepository sync,
        INetworkCostProvider network,
        TimeSpan? period = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _sync = sync;
        _network = network;
        _period = period ?? DefaultPeriod;
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct));
    }

    /// <summary>Delay before retry number <paramref name="attempt"/> (1-based).</summary>
    public static TimeSpan BackoffFor(int attempt)
    {
        var factor = Math.Pow(2, Math.Clamp(attempt - 1, 0, 10));
        var delay = TimeSpan.FromTicks((long)(BaseBackoff.Ticks * factor));
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    /// <summary>Begins periodic and event-driven syncing. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _network.Changed += OnNetworkChanged;
        _sync.RetryRequested += OnRetryRequested;
        _ = RunPeriodicAsync(_cts.Token);
        _ = RunOnceAsync(_cts.Token); // drain whatever was left from the previous session
    }

    private async Task RunPeriodicAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_period);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>One guarded run: only when something is queued and the network is up.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            if (_sync.State.Pending == 0 || _network.Current == NetworkCost.Offline)
            {
                return;
            }

            await _sync.SyncNowAsync(ct).ConfigureAwait(false);
            if (_sync.State.Error is null)
            {
                Interlocked.Exchange(ref _retryAttempt, 0);
            }
        }
        catch (Exception)
        {
            // Never let a background run crash the process; state carries the error.
        }
    }

    private void OnNetworkChanged(object? sender, NetworkCost cost)
    {
        if (cost != NetworkCost.Offline && Volatile.Read(ref _disposed) == 0)
        {
            _ = RunOnceAsync(_cts.Token);
        }
    }

    private void OnRetryRequested(object? sender, EventArgs e)
    {
        if (Volatile.Read(ref _disposed) != 0 || Interlocked.Exchange(ref _retryScheduled, 1) == 1)
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _retryAttempt);
        _ = RetryAfterAsync(BackoffFor(attempt), _cts.Token);
    }

    private async Task RetryAfterAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await _delay(delay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _retryScheduled, 0);
        }

        await RunOnceAsync(ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _network.Changed -= OnNetworkChanged;
        _sync.RetryRequested -= OnRetryRequested;
        _cts.Cancel();
        _cts.Dispose();
    }
}
