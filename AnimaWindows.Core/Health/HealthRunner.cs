using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Health;

public enum StepStatus
{
    Pending,
    Ok,
    Failed,
    Skipped,
}

/// <summary>Outcome of one live check step. <paramref name="Millis"/> is the latency, <paramref name="Message"/> the error (or a short success note).</summary>
public sealed record StepResult(StepStatus Status, long Millis = 0, string? Message = null);

/// <summary>Live check of one provider: search, episodes, streams; each step reported separately. Mirrors <c>ProviderHealth</c>.</summary>
public sealed record ProviderHealth(string Id, string Name)
{
    /// <summary>Disabled by the user (still checked, shown as such).</summary>
    public bool Disabled { get; init; }

    /// <summary>Part of the effective chain for the current content language.</summary>
    public bool InChain { get; init; } = true;

    public StepResult Search { get; init; } = new(StepStatus.Pending);
    public StepResult Episodes { get; init; } = new(StepStatus.Pending);
    public StepResult Streams { get; init; } = new(StepStatus.Pending);

    public IReadOnlyList<StepResult> Steps => [Search, Episodes, Streams];
    public bool Finished => Steps.All(s => s.Status != StepStatus.Pending);
    public bool AllOk => Steps.All(s => s.Status == StepStatus.Ok);
}

/// <summary>Pure runner so it can be unit tested with fake providers. Each step has its own timeout. Port of <c>HealthRunner</c>.</summary>
public sealed class HealthRunner
{
    private readonly TimeSpan _timeout;
    private readonly string _query;
    private readonly Func<long> _nowMs;

    public HealthRunner(TimeSpan? timeout = null, string query = "frieren", Func<long>? nowMs = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _query = query;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
    }

    /// <summary>Runs the three steps for <paramref name="provider"/>; <paramref name="onUpdate"/> receives every intermediate state (starting with <paramref name="initial"/>).</summary>
    public async Task<ProviderHealth> CheckAsync(
        IAnimeProvider provider,
        ProviderHealth initial,
        Action<ProviderHealth>? onUpdate = null,
        CancellationToken cancellationToken = default)
    {
        onUpdate ??= static _ => { };
        var h = initial;
        onUpdate(h);
        const TranslationMode mode = TranslationMode.Sub;

        var found = await StepAsync(async ct =>
        {
            var r = await provider.SearchAsync(_query, mode, ct).ConfigureAwait(false);
            if (r.Count == 0) throw new InvalidOperationException($"no results for \"{_query}\"");
            return r;
        }, cancellationToken).ConfigureAwait(false);
        h = h with { Search = found.Result($"{found.Value?.Count ?? 0}") };
        onUpdate(h);
        var anime = found.Value?[0];
        if (anime is null) return SkipRest(h, skipEpisodes: true, onUpdate);

        var eps = await StepAsync(async ct =>
        {
            var r = await provider.EpisodesAsync(anime, mode, ct).ConfigureAwait(false);
            if (r.Count == 0) throw new InvalidOperationException("no episodes");
            return r;
        }, cancellationToken).ConfigureAwait(false);
        h = h with { Episodes = eps.Result($"{eps.Value?.Count ?? 0}") };
        onUpdate(h);
        var first = eps.Value?[0];
        if (first is null) return SkipRest(h, skipEpisodes: false, onUpdate);

        var st = await StepAsync(async ct =>
        {
            var r = await provider.StreamsAsync(anime, first, mode, ct).ConfigureAwait(false);
            if (r.Variants.Count == 0 && r.MasterUrl is null) throw new InvalidOperationException("no playable streams");
            return r;
        }, cancellationToken).ConfigureAwait(false);
        h = h with { Streams = st.Result(st.Value is null ? null : $"{st.Value.Variants.Count}") };
        onUpdate(h);
        return h;
    }

    private static ProviderHealth SkipRest(ProviderHealth h, bool skipEpisodes, Action<ProviderHealth> onUpdate)
    {
        var skipped = new StepResult(StepStatus.Skipped);
        var r = skipEpisodes ? h with { Episodes = skipped, Streams = skipped } : h with { Streams = skipped };
        onUpdate(r);
        return r;
    }

    private sealed record Timed<T>(T? Value, long Millis, string? Error) where T : class
    {
        public StepResult Result(string? note) =>
            Error is null ? new StepResult(StepStatus.Ok, Millis, note) : new StepResult(StepStatus.Failed, Millis, Error);
    }

    private async Task<Timed<T>> StepAsync<T>(Func<CancellationToken, Task<T>> block, CancellationToken ct) where T : class
    {
        var t0 = _nowMs();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var task = Task.Run(() => block(work.Token), CancellationToken.None);
            var delay = Task.Delay(_timeout, delayCts.Token);
            var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
            if (done != task)
            {
                ct.ThrowIfCancellationRequested();
                work.Cancel();
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return new Timed<T>(null, _nowMs() - t0, $"timed out after {(int)_timeout.TotalSeconds}s");
            }

            delayCts.Cancel();
            var value = await task.ConfigureAwait(false);
            return new Timed<T>(value, _nowMs() - t0, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new Timed<T>(null, _nowMs() - t0, string.IsNullOrWhiteSpace(e.Message) ? e.GetType().Name : e.Message);
        }
    }
}
