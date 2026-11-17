using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.Core.Playback;

/// <summary>
/// Pure bookkeeping for next-episode prefetch: when to start, and a tiny in-memory cache whose entries expire after the TTL
/// (provider stream links go stale). Not thread-safe; use from one thread. Port of <c>NextEpisodePrefetcher</c>.
/// </summary>
public sealed class NextEpisodePrefetcher<T> where T : class
{
    public const long DefaultTtlMs = 10 * 60 * 1000L;
    public const double DefaultTrigger = 0.8;

    private sealed record Entry(T Value, long AtMs);

    private readonly Func<long> _clockMs;
    private readonly long _ttlMs;
    private readonly double _triggerFraction;
    private readonly Dictionary<string, Entry> _cache = new();
    private readonly HashSet<string> _attempted = new();

    /// <param name="clockMs">Monotonic-ish millisecond clock (injectable for tests).</param>
    public NextEpisodePrefetcher(Func<long> clockMs, long ttlMs = DefaultTtlMs, double triggerFraction = DefaultTrigger)
    {
        _clockMs = clockMs;
        _ttlMs = ttlMs;
        _triggerFraction = triggerFraction;
    }

    public NextEpisodePrefetcher(IClock clock) : this(() => clock.UnixMilliseconds)
    {
    }

    /// <summary>
    /// True once per <paramref name="key"/>: enabled, network allowed (<paramref name="metered"/> needs <paramref name="allowMetered"/>),
    /// a next episode exists and playback passed the trigger point.
    /// </summary>
    public bool ShouldStart(string? key, bool enabled, bool metered, bool allowMetered, long positionMs, long durationMs)
    {
        if (!enabled || key is null || durationMs <= 0) return false;
        if (metered && !allowMetered) return false;
        if (positionMs < durationMs * _triggerFraction) return false;
        return !_attempted.Contains(key) && Peek(key) is null;
    }

    /// <summary>Marks <paramref name="key"/> as tried so a failed resolve is not retried on every tick.</summary>
    public void MarkAttempted(string key) => _attempted.Add(key);

    public void Put(string key, T value) => _cache[key] = new Entry(value, _clockMs());

    /// <summary>Returns and removes a fresh entry; expired entries are dropped and yield null.</summary>
    public T? Take(string key)
    {
        if (!_cache.Remove(key, out var e)) return null;
        return _clockMs() - e.AtMs < _ttlMs ? e.Value : null;
    }

    private T? Peek(string key)
    {
        if (!_cache.TryGetValue(key, out var e)) return null;
        if (_clockMs() - e.AtMs >= _ttlMs)
        {
            _cache.Remove(key);
            return null;
        }

        return e.Value;
    }

    public void Clear()
    {
        _cache.Clear();
        _attempted.Clear();
    }
}
