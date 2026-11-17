using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tracking;

/// <summary>
/// AniList / MyAnimeList tracking. Mirrors <c>SyncRepositoryImpl</c> in core/sync/SyncRepositoryImpl.kt (see docs/tracking.md).
/// Tokens live in <see cref="ISecretStore"/>, updates queue in <c>sync_queue.json</c> (coalesced per MAL id, furthest episode
/// wins). Remote progress is never lowered; status becomes COMPLETED when the episode reaches the tracker's episode count.
/// </summary>
public sealed class SyncRepository : ISyncRepository
{
    public const string RedirectAniList = "animawindows://auth/anilist";
    public const string RedirectMal = "animawindows://auth/mal";
    public const string RedirectScheme = "animawindows";

    /// <summary>Fraction of an episode after which it counts as watched.</summary>
    public const double WatchedFraction = 0.85;

    private readonly SyncConfig _config;
    private readonly AniListClient _anilist;
    private readonly MalClient _mal;
    private readonly SecretTokenStore _tokens;
    private readonly SyncQueueStore _queue;
    private readonly IClock _clock;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly object _authLock = new();
    private readonly HashSet<string> _reported = [];
    private volatile SyncState _state;
    private string? _pendingMalVerifier;
    private string? _pendingMalState;

    public SyncRepository(
        SyncConfig config,
        AniListClient anilist,
        MalClient mal,
        ISecretStore secrets,
        SyncQueueStore queue,
        IClock clock)
    {
        _config = config;
        _anilist = anilist;
        _mal = mal;
        _tokens = new SecretTokenStore(secrets);
        _queue = queue;
        _clock = clock;
        _state = BuildState(null, false);
    }

    public SyncState State => _state;

    public event EventHandler<SyncState>? StateChanged;

    /// <summary>Raised when a delivery run hit a transient failure; <see cref="SyncScheduler"/> retries with backoff.</summary>
    public event EventHandler? RetryRequested;

    // ---- state ----

    private SyncState BuildState(string? error, bool syncing)
    {
        var services = ImmutableDictionary.CreateBuilder<SyncService, ServiceState>();
        foreach (var service in Enum.GetValues<SyncService>())
        {
            var t = _tokens.Get(service);
            services[service] = new ServiceState(_config.IsConfigured(service), t is not null, t?.Username);
        }

        return new SyncState(services.ToImmutable(), _queue.Load().Count, syncing, error);
    }

    private void Publish(SyncState state)
    {
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void RefreshState(string? error, bool syncing = false) => Publish(BuildState(error, syncing));

    // ---- auth ----

    public string? AuthorizeUrl(SyncService service)
    {
        if (!_config.IsConfigured(service))
        {
            return null;
        }

        if (service == SyncService.AniList)
        {
            return AniListClient.AuthorizeUrl(_config.AnilistClientId);
        }

        var verifier = Pkce.Verifier();
        var state = Pkce.State();
        lock (_authLock)
        {
            _pendingMalVerifier = verifier;
            _pendingMalState = state;
        }

        return _mal.AuthorizeUrl(_config.MalClientId, Pkce.Challenge(verifier), state, RedirectMal);
    }

    public async Task<bool> HandleRedirectAsync(string uri, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
        {
            return false;
        }

        if (!string.Equals(u.Scheme, RedirectScheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(u.Host, "auth", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = u.AbsolutePath;
        var isAniList = string.Equals(path, "/anilist", StringComparison.OrdinalIgnoreCase);
        var isMal = string.Equals(path, "/mal", StringComparison.OrdinalIgnoreCase);
        if (!isAniList && !isMal)
        {
            return false;
        }

        var prm = ParseParams(u.Fragment);
        foreach (var kv in ParseParams(u.Query))
        {
            prm[kv.Key] = kv.Value;
        }

        prm.TryGetValue("error", out var error);
        try
        {
            if (isAniList)
            {
                if (!prm.TryGetValue("access_token", out var token))
                {
                    throw new InvalidOperationException(error ?? "no token");
                }

                long? ttl = prm.TryGetValue("expires_in", out var ttlText)
                    && long.TryParse(ttlText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ttlValue) ? ttlValue : null;
                var name = await TryAsync(() => _anilist.ViewerNameAsync(token, cancellationToken)).ConfigureAwait(false);
                _tokens.Put(SyncService.AniList, new Tokens(token, null, ttl is { } s ? _clock.UnixMilliseconds + s * 1000 : 0, name));
            }
            else
            {
                string? verifier;
                string? expectedState;
                lock (_authLock)
                {
                    verifier = _pendingMalVerifier;
                    expectedState = _pendingMalState;
                }

                prm.TryGetValue("state", out var gotState);
                if (error is not null || verifier is null || gotState != expectedState)
                {
                    throw new InvalidOperationException(error ?? "invalid state");
                }

                if (!prm.TryGetValue("code", out var code))
                {
                    throw new InvalidOperationException("no code");
                }

                var t = await _mal.ExchangeAsync(_config.MalClientId, code, verifier, RedirectMal, cancellationToken).ConfigureAwait(false);
                var name = await TryAsync(() => _mal.UserNameAsync(t.AccessToken, cancellationToken)).ConfigureAwait(false);
                _tokens.Put(SyncService.Mal, new Tokens(t.AccessToken, t.RefreshToken, _clock.UnixMilliseconds + t.ExpiresIn * 1000, name));
                lock (_authLock)
                {
                    _pendingMalVerifier = null;
                    _pendingMalState = null;
                }
            }

            RefreshState(null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            RefreshState(string.IsNullOrEmpty(e.Message) ? "sign-in failed" : e.Message);
        }

        return true;
    }

    private static async Task<string?> TryAsync(Func<Task<string?>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> ParseParams(string? s)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(s))
        {
            return result;
        }

        foreach (var pair in s.TrimStart('#', '?').Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i < 0)
            {
                continue;
            }

            result[Unescape(pair[..i])] = Unescape(pair[(i + 1)..]);
        }

        return result;
    }

    private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

    public async Task DisconnectAsync(SyncService service, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _tokens.Clear(service);
            var kept = new List<PendingUpdate>();
            foreach (var item in _queue.Load())
            {
                var services = item.Services.Remove(service);
                if (services.Count > 0)
                {
                    kept.Add(item with { Services = services });
                }
            }

            _queue.Save(kept);
        }
        finally
        {
            _mutex.Release();
        }

        RefreshState(_state.Error);
    }

    // ---- queueing ----

    /// <summary>True for whole episode numbers >= 1 ("12", "12.0"); false for "12.5", "SP", "0".</summary>
    public static bool TryParseEpisode(string episodeNumber, out int episode)
    {
        episode = 0;
        if (!double.TryParse(episodeNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return false;
        }

        if (d < 1 || d > int.MaxValue || d % 1.0 != 0.0)
        {
            return false;
        }

        episode = (int)d;
        return true;
    }

    /// <summary>True once <paramref name="positionMs"/> reached <see cref="WatchedFraction"/> (85%) of a known duration.</summary>
    public static bool IsWatched(long positionMs, long durationMs) =>
        durationMs > 0 && (double)positionMs / durationMs >= WatchedFraction;

    public void OnEpisodeWatched(Anime anime, string episodeNumber, int? totalEpisodes)
    {
        if (anime.MalId is not { } malId || !TryParseEpisode(episodeNumber, out var episode))
        {
            return;
        }

        _ = OnEpisodeWatchedAsync(malId, episode, totalEpisodes);
    }

    /// <summary>
    /// Call periodically from playback: reports the episode once position passes 85% of the duration. Ignored for shows without
    /// MAL id and for non-integer episodes; each episode is reported at most once per app session.
    /// </summary>
    public void ReportProgress(Anime anime, string episodeNumber, long positionMs, long durationMs, int? totalEpisodes)
    {
        if (anime.MalId is not { } malId || !IsWatched(positionMs, durationMs) || !TryParseEpisode(episodeNumber, out _))
        {
            return;
        }

        lock (_reported)
        {
            if (!_reported.Add($"{malId}:{episodeNumber}"))
            {
                return;
            }
        }

        OnEpisodeWatched(anime, episodeNumber, totalEpisodes);
    }

    internal async Task OnEpisodeWatchedAsync(int malId, int episode, int? total)
    {
        try
        {
            await Task.Yield();
            await RecordAsync(malId, episode, total).ConfigureAwait(false);
            await SyncNowAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Fire-and-forget: failures surface through SyncState.Error / the retry scheduler.
        }
    }

    /// <summary>Queues <paramref name="episode"/> of <paramref name="malId"/> for every connected service, coalescing with an existing entry for the show.</summary>
    public async Task RecordAsync(int malId, int episode, int? total, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var services = Enum.GetValues<SyncService>().Where(s => _tokens.Get(s) is not null).ToImmutableHashSet();
            if (services.Count == 0)
            {
                return;
            }

            var items = _queue.Load().ToList();
            var i = items.FindIndex(x => x.MalId == malId);
            if (i >= 0)
            {
                var old = items[i];
                items[i] = new PendingUpdate(malId, Math.Max(old.Episode, episode), total ?? old.Total, old.Services.Union(services));
            }
            else
            {
                items.Add(new PendingUpdate(malId, episode, total, services));
            }

            _queue.Save(items);
        }
        finally
        {
            _mutex.Release();
        }

        RefreshState(_state.Error);
    }

    // ---- delivery ----

    private enum OutcomeKind
    {
        Done,
        Retry,
        Drop,
    }

    private readonly record struct Outcome(OutcomeKind Kind, string? Reason = null)
    {
        public static Outcome Done => new(OutcomeKind.Done);

        public static Outcome Retry(string reason) => new(OutcomeKind.Retry, reason);

        public static Outcome Drop(string? reason) => new(OutcomeKind.Drop, reason);
    }

    public async Task SyncNowAsync(CancellationToken cancellationToken = default)
    {
        string? error = null;
        var retry = false;
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Publish(_state with { Syncing = true });
            var remainingItems = new List<PendingUpdate>();
            foreach (var item in _queue.Load())
            {
                var remaining = item.Services;
                foreach (var service in item.Services)
                {
                    var outcome = await DeliverAsync(service, item, cancellationToken).ConfigureAwait(false);
                    switch (outcome.Kind)
                    {
                        case OutcomeKind.Done:
                            remaining = remaining.Remove(service);
                            break;
                        case OutcomeKind.Drop:
                            remaining = remaining.Remove(service);
                            if (outcome.Reason is not null)
                            {
                                error = outcome.Reason;
                            }

                            break;
                        default:
                            retry = true;
                            error = outcome.Reason;
                            break;
                    }
                }

                if (remaining.Count > 0)
                {
                    remainingItems.Add(item with { Services = remaining });
                }
            }

            _queue.Save(remainingItems);
        }
        catch (OperationCanceledException)
        {
            Publish(BuildState(_state.Error, false));
            throw;
        }
        finally
        {
            _mutex.Release();
        }

        RefreshState(error);
        if (retry)
        {
            RetryRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private static string NameOf(SyncService service) => service == SyncService.AniList ? "ANILIST" : "MAL";

    private async Task<Outcome> DeliverAsync(SyncService service, PendingUpdate item, CancellationToken ct)
    {
        var tokens = _tokens.Get(service);
        if (tokens is null)
        {
            return Outcome.Drop(null);
        }

        try
        {
            if (service == SyncService.AniList)
            {
                var e = await _anilist.LookupAsync(tokens.Access, item.MalId, ct).ConfigureAwait(false);
                if (e is null)
                {
                    return Outcome.Drop(null);
                }

                if ((e.Progress ?? 0) < item.Episode)
                {
                    var total = item.Total ?? e.Episodes;
                    await _anilist.SaveAsync(tokens.Access, e.MediaId, item.Episode, total is { } t && item.Episode >= t, ct).ConfigureAwait(false);
                }

                return Outcome.Done;
            }

            return await MalAuthedAsync(
                async token =>
                {
                    var e = await _mal.EntryAsync(token, item.MalId, ct).ConfigureAwait(false);
                    if (e is null)
                    {
                        return Outcome.Drop(null);
                    }

                    if ((e.Watched ?? 0) < item.Episode)
                    {
                        var total = item.Total ?? e.Episodes;
                        await _mal.UpdateAsync(token, item.MalId, item.Episode, total is { } t && item.Episode >= t, ct).ConfigureAwait(false);
                    }

                    return Outcome.Done;
                },
                ct).ConfigureAwait(false);
        }
        catch (SyncHttpException e)
        {
            if (e.Unauthorized)
            {
                _tokens.Clear(service);
                return Outcome.Drop($"{NameOf(service)}: signed out (token rejected)");
            }

            return e.Transient ? Outcome.Retry(e.Message) : Outcome.Drop(e.Message);
        }
        catch (HttpRequestException e)
        {
            return Outcome.Retry(e.Message);
        }
        catch (IOException e)
        {
            return Outcome.Retry(e.Message);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return Outcome.Retry("request timed out");
        }
        catch (JsonException e)
        {
            return Outcome.Drop("unexpected response: " + e.Message);
        }
    }

    /// <summary>Runs <paramref name="block"/> with a valid MAL token: refreshes proactively when expired, and once more on a 401.</summary>
    private async Task<T> MalAuthedAsync<T>(Func<string, Task<T>> block, CancellationToken ct)
    {
        var token = await MalTokenAsync(force: false, ct).ConfigureAwait(false);
        try
        {
            return await block(token).ConfigureAwait(false);
        }
        catch (SyncHttpException e) when (e.Unauthorized)
        {
            token = await MalTokenAsync(force: true, ct).ConfigureAwait(false);
            return await block(token).ConfigureAwait(false);
        }
    }

    private async Task<string> MalTokenAsync(bool force, CancellationToken ct)
    {
        var cur = _tokens.Get(SyncService.Mal) ?? throw new SyncHttpException(401, "not signed in");
        if (!force && (cur.ExpiresAt == 0 || _clock.UnixMilliseconds < cur.ExpiresAt - 60_000))
        {
            return cur.Access;
        }

        var refresh = cur.Refresh ?? throw new SyncHttpException(401, "no refresh token");
        MalToken n;
        try
        {
            n = await _mal.RefreshAsync(_config.MalClientId, refresh, ct).ConfigureAwait(false);
        }
        catch (SyncHttpException e) when (e.Code is 400 or 401)
        {
            // MAL answers 400/401 for a revoked or expired refresh token.
            throw new SyncHttpException(401, "refresh rejected");
        }

        _tokens.Put(
            SyncService.Mal,
            cur with { Access = n.AccessToken, Refresh = n.RefreshToken ?? refresh, ExpiresAt = _clock.UnixMilliseconds + n.ExpiresIn * 1000 });
        return n.AccessToken;
    }
}
