using System.Globalization;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Chain;

/// <summary>
/// Language-aware, failover-capable facade over all <see cref="IAnimeProvider"/>s. Port of <c>ProviderChain</c> in
/// provider/api/ProviderChain.kt. See <see cref="IProviderChain"/> for the rules.
/// </summary>
public sealed class ProviderChain : IProviderChain
{
    private readonly Dictionary<string, IAnimeProvider> _providers = new(StringComparer.Ordinal);
    private readonly List<string> _ids = [];
    private readonly ISettingsStore _settings;
    private readonly TimeSpan _timeout;
    private readonly IProviderAvailability _availability;
    private readonly Func<string> _deviceLanguage;

    /// <param name="providers">All registered providers (registration order = <see cref="Ids"/> order).</param>
    /// <param name="settings">Settings source for <see cref="ChainAsync"/>, <see cref="SearchAsync"/> and <see cref="StreamsAsync"/>.</param>
    /// <param name="options">Per-provider timeout; defaults to 20 s.</param>
    /// <param name="availability">Remote kill switch; defaults to "all enabled".</param>
    /// <param name="deviceLanguage">Device UI language code (injectable for tests); defaults to the current UI culture.</param>
    public ProviderChain(
        IEnumerable<IAnimeProvider> providers,
        ISettingsStore settings,
        ProviderChainOptions? options = null,
        IProviderAvailability? availability = null,
        Func<string>? deviceLanguage = null)
    {
        foreach (var p in providers)
        {
            if (!_providers.ContainsKey(p.Id)) _ids.Add(p.Id);
            _providers[p.Id] = p;
        }

        _settings = settings;
        _timeout = (options ?? ProviderChainOptions.Default).Timeout;
        _availability = availability ?? AllEnabled.Instance;
        _deviceLanguage = deviceLanguage ?? (() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);
    }

    private sealed class AllEnabled : IProviderAvailability
    {
        public static AllEnabled Instance { get; } = new();
        public bool IsEnabled(string id) => true;
    }

    public IReadOnlyList<string> Ids => _ids;

    public IAnimeProvider? Provider(string id) => _providers.GetValueOrDefault(id);

    public string EffectiveLanguage(AppSettings settings)
    {
        string? explicitLang = string.IsNullOrWhiteSpace(settings.ContentLanguage) || settings.ContentLanguage == "auto"
            ? null
            : settings.ContentLanguage;
        var chosen = explicitLang
            ?? (!string.IsNullOrWhiteSpace(settings.SubtitleLanguage) ? settings.SubtitleLanguage : DeviceContentLanguage());
        return NormalizeLang(chosen);
    }

    private string DeviceContentLanguage()
    {
        var dev = NormalizeLang(_deviceLanguage());
        return _providers.Values.Any(p => p.ContentLanguages.Contains(dev)) ? dev : "en";
    }

    public IReadOnlyList<IAnimeProvider> ChainFor(AppSettings settings)
    {
        var lang = EffectiveLanguage(settings);
        var eligible = _ids.Select(id => _providers[id])
            .Where(p => !settings.DisabledProviders.Contains(p.Id)
                        && _availability.IsEnabled(p.Id)
                        && !(lang == "zh" && p.GfwBlocked))
            .ToList();
        IReadOnlyList<string> manual = settings.ProviderOrder.TryGetValue(lang, out var m) ? m : [];

        int ManualIndex(IAnimeProvider p)
        {
            for (var i = 0; i < manual.Count; i++)
                if (manual[i] == p.Id) return i;
            return int.MaxValue;
        }

        IEnumerable<IAnimeProvider> Sort(IEnumerable<IAnimeProvider> src) =>
            src.OrderBy(ManualIndex).ThenBy(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal);

        var native = Sort(eligible.Where(p => p.ContentLanguages.Contains(lang))).ToList();
        var english = lang == "en"
            ? []
            : Sort(eligible.Where(p => p.ContentLanguages.Contains("en") && !native.Contains(p))).ToList();
        return [.. native, .. english];
    }

    public Task<IReadOnlyList<IAnimeProvider>> ChainAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ChainFor(_settings.Current));

    public async Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var chain = await ChainAsync(cancellationToken).ConfigureAwait(false);
        if (chain.Count == 0) return [];
        var tasks = chain
            .Select(p => AttemptAsync(
                ct => p.SearchAsync(query, mode, ct), _timeout + TimeSpan.FromSeconds(5), cancellationToken))
            .ToList();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);
        if (outcomes.All(o => o.Error is not null)) throw PickFailure(outcomes.Select(o => o.Error!).ToList());
        var perProvider = chain.Select((p, i) => (p.Id, (IReadOnlyList<Anime>)(outcomes[i].Value ?? []))).ToList();
        return Merge(perProvider);
    }

    public async Task<ResolvedStreams> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var s = _settings.Current;
        var candidates = Candidates(anime, s);
        if (candidates.Count == 0)
            throw new NoProviderAvailableException($"No provider available for {anime.ProviderId}");
        var failures = new List<Exception>();
        var next = 0;
        var attemptTimeout = _timeout + TimeSpan.FromSeconds(5);
        if (s.SourceStrategy == SourceStrategy.BestQuality && candidates.Count >= 2)
        {
            var pair = candidates.Take(2).ToList();
            next = 2;
            var results = await Task.WhenAll(pair.Select(c => AttemptAsync(
                ct => ResolveAsync(c, episode, mode, ct), attemptTimeout, cancellationToken))).ConfigureAwait(false);
            foreach (var r in results)
                if (r.Error is not null) failures.Add(r.Error);
            var ok = results.Where(r => r.Error is null).Select(r => r.Value!).ToList();
            if (ok.Count > 0)
            {
                var best = ok[0];
                for (var i = 1; i < ok.Count; i++)
                    if (Score(ok[i], s, mode) > Score(best, s, mode)) best = ok[i];
                return best;
            }
        }

        while (next < candidates.Count)
        {
            var r = await AttemptAsync(
                ct => ResolveAsync(candidates[next], episode, mode, ct), attemptTimeout, cancellationToken).ConfigureAwait(false);
            next++;
            if (r.Error is null) return r.Value!;
            failures.Add(r.Error);
            AttemptFailed?.Invoke(this, new ProviderAttemptFailedEventArgs(candidates[next - 1].Provider.Id, r.Error));
        }

        throw PickFailure(failures);
    }

    private sealed record Candidate(IAnimeProvider Provider, Anime Anime, bool IsFallback);

    /// <summary>Raised (any thread) when one provider's stream resolution fails, before the chain tries the next one.</summary>
    public event EventHandler<ProviderAttemptFailedEventArgs>? AttemptFailed;

    private List<Candidate> Candidates(Anime anime, AppSettings s)
    {
        var output = new List<Candidate>();
        if (_providers.TryGetValue(anime.ProviderId, out var own)) output.Add(new Candidate(own, anime, false));
        foreach (var p in ChainFor(s))
        {
            if (p.Id == anime.ProviderId) continue;
            if (!anime.Alternates.TryGetValue(p.Id, out var altId)) continue;
            output.Add(new Candidate(
                p,
                anime with { Id = altId, ProviderId = p.Id, Alternates = new Dictionary<string, string>() },
                true));
        }

        return output;
    }

    private async Task<ResolvedStreams> ResolveAsync(Candidate c, Episode episode, TranslationMode mode, CancellationToken ct)
    {
        var ep = episode;
        if (c.IsFallback)
        {
            var eps = await c.Provider.EpisodesAsync(c.Anime, mode, ct).ConfigureAwait(false);
            ep = eps.FirstOrDefault(e => SameEpisode(e.Number, episode.Number))
                 ?? throw new ProviderException($"{c.Provider.DisplayName}: episode {episode.Number} not found");
        }

        var set = await WithTimeoutAsync(
            innerCt => c.Provider.StreamsAsync(c.Anime, ep, mode, innerCt),
            _timeout,
            $"{c.Provider.DisplayName}: timed out after {(int)_timeout.TotalSeconds}s",
            ct).ConfigureAwait(false);
        if (set.Variants.Count == 0 && set.MasterUrl is null)
            throw new ProviderException($"{c.Provider.DisplayName}: no playable streams");
        return new ResolvedStreams(set, c.Provider.Id, c.IsFallback, c.Anime, ep);
    }

    /// <summary>Higher is better: subtitles satisfied (when wanted) dominate, then max height.</summary>
    private static long Score(ResolvedStreams r, AppSettings s, TranslationMode mode)
    {
        var lang = NormalizeLang(s.SubtitleLanguage);
        var wantsSubs = mode == TranslationMode.Sub && !string.IsNullOrWhiteSpace(s.SubtitleLanguage);
        long subsOk = 0;
        if (wantsSubs)
        {
            var hard = r.Set.HardsubLanguage;
            if ((hard is not null && NormalizeLang(hard) == lang) || r.Set.Subtitles.Count > 0) subsOk = 1;
        }

        var maxHeight = r.Set.Variants.Count == 0 ? 0 : r.Set.Variants.Max(v => v.Quality ?? 0);
        return subsOk * 100_000L + maxHeight;
    }

    private readonly record struct Outcome<T>(T? Value, Exception? Error);

    private static async Task<T> WithTimeoutAsync<T>(Func<CancellationToken, Task<T>> block, TimeSpan timeout, string message, CancellationToken ct)
    {
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(() => block(work.Token), CancellationToken.None);
        var delay = Task.Delay(timeout, delayCts.Token);
        var done = await Task.WhenAny(task, delay).ConfigureAwait(false);
        if (done == task)
        {
            delayCts.Cancel();
            return await task.ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();
        work.Cancel();
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        throw new ProviderTimeoutException(message);
    }

    private async Task<Outcome<T>> AttemptAsync<T>(Func<CancellationToken, Task<T>> block, TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            var v = await WithTimeoutAsync(block, timeout, "Timed out", ct).ConfigureAwait(false);
            return new Outcome<T>(v, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            return new Outcome<T>(default, e);
        }
    }

    private static Exception PickFailure(IReadOnlyList<Exception> all) =>
        all.FirstOrDefault(e => e is CloudflareChallengeException)
        ?? all.FirstOrDefault()
        ?? new ProviderException("No provider available");

    internal static string NormalizeLang(string l)
    {
        var s = l.Trim().ToLowerInvariant();
        var i = s.IndexOf('-');
        if (i >= 0) s = s[..i];
        i = s.IndexOf('_');
        if (i >= 0) s = s[..i];
        return s;
    }

    private static bool SameEpisode(string a, string b)
    {
        if (a == b) return true;
        if (!double.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) return false;
        return double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && x == y;
    }

    /// <summary>Lowercase letters/digits only, so "Re:Zero  - Season 2" and "re zero season 2" match.</summary>
    internal static string NormalizeTitle(string t) =>
        string.Concat(t.ToLowerInvariant().Where(char.IsLetterOrDigit));

    private sealed class Group(Anime head)
    {
        public Anime Head { get; set; } = head;
        public Dictionary<string, string> Alts { get; } = new(); // insertion order preserved for small dictionaries; ordered list below
        public List<string> AltOrder { get; } = [];
    }

    /// <summary>Merge step of <see cref="SearchAsync"/>; <paramref name="perProvider"/> is in chain order.</summary>
    internal static IReadOnlyList<Anime> Merge(IReadOnlyList<(string ProviderId, IReadOnlyList<Anime> Results)> perProvider)
    {
        var groups = new List<Group>();
        var byMal = new Dictionary<int, Group>();
        var byTitle = new Dictionary<string, Group>();
        foreach (var (providerId, results) in perProvider)
        {
            var joined = new HashSet<Group>();
            foreach (var a in results)
            {
                var key = NormalizeTitle(a.Title);
                bool Eligible(Group g) => !joined.Contains(g) && g.Head.ProviderId != providerId && !g.Alts.ContainsKey(providerId);

                Group? target = null;
                if (a.MalId is { } mal && byMal.TryGetValue(mal, out var gm) && Eligible(gm)) target = gm;
                else if (key.Length > 0 && byTitle.TryGetValue(key, out var gt) && Eligible(gt)) target = gt;

                if (target is not null)
                {
                    target.Alts[providerId] = a.Id;
                    target.AltOrder.Add(providerId);
                    joined.Add(target);
                    if (target.Head.MalId is null && a.MalId is { } newMal)
                    {
                        target.Head = target.Head with { MalId = newMal };
                        byMal[newMal] = target;
                    }

                    if (target.Head.CoverUrl is null) target.Head = target.Head with { CoverUrl = a.CoverUrl };
                }
                else
                {
                    var ng = new Group(a);
                    groups.Add(ng);
                    joined.Add(ng);
                    if (a.MalId is { } m2) byMal.TryAdd(m2, ng);
                    if (key.Length > 0) byTitle.TryAdd(key, ng);
                }
            }
        }

        return groups.Select(g =>
        {
            var alts = new Dictionary<string, string>();
            foreach (var kv in g.Head.Alternates) alts[kv.Key] = kv.Value;
            foreach (var id in g.AltOrder) alts[id] = g.Alts[id];
            return g.Head with { Alternates = alts };
        }).ToList();
    }
}

public sealed class ProviderAttemptFailedEventArgs(string providerId, Exception error) : EventArgs
{
    public string ProviderId { get; } = providerId;

    public Exception Error { get; } = error;
}
