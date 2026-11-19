using System.Collections.Concurrent;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Common;
using AnimaWindows.Providers.Hosts;

namespace AnimaWindows.Providers.Shinden;

/// <summary>
/// Polish provider for shinden.pl (port of alt-ani-cli-win-shinden, using the in-app Cloudflare solver instead of FlareSolverr).
/// Mirrors <c>ShindenProvider</c> in provider/shinden/ShindenProvider.kt.
/// <para>
/// Mode mapping: <see cref="TranslationMode.Dub"/> = Polish audio (lektor/dubbing); <see cref="TranslationMode.Sub"/> = original audio
/// with Polish (then English) subtitles. Base URL(s) come from <see cref="IProviderConfig"/> per request (mirrors are tried on
/// connection failure/5xx; <c>params.apiUrl</c> overrides the API host). Only the public guest token documented by the
/// reference tool is used; no account.
/// </para>
/// </summary>
public sealed class ShindenProvider : IAnimeProvider
{
    public const string ProviderId = "shinden";
    public const string DefaultBaseUrl = "https://shinden.pl";
    public const string DefaultApiUrl = "https://api4.shinden.pl";

    /// <summary>Public guest token (base64 of <c>_guest_:0,5,21000000,255,4174293644</c>, URL-encoded) documented by the reference tool.</summary>
    public const string GuestAuth = "X2d1ZXN0XzowLDUsMjEwMDAwMDAsMjU1LDQxNzQyOTM2NDQ%3D";

    public const string AcceptLanguage = "pl-PL,pl;q=0.9,en-US;q=0.8,en;q=0.7";

    private const int MaxPages = 10;

    private readonly HostHttp _http;
    private readonly IProviderConfig _config;
    private readonly TimeSpan _antibotDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly int _maxResolveAttempts;
    private readonly ConcurrentDictionary<string, string> _kinds = new();
    private readonly ConcurrentDictionary<string, int> _mal = new();

    /// <param name="http">HTTP layer; build it with <see cref="AcceptLanguage"/> so Polish pages are served.</param>
    /// <param name="config">Per-provider remote config, read on every request.</param>
    /// <param name="antibotDelay">Server-enforced pause between <c>player_load</c> and <c>player_show</c>; shorter values make <c>player_show</c> come back empty. Default 5000 ms.</param>
    /// <param name="delay">Delay function (default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>); tests inject a no-op.</param>
    /// <param name="maxResolveAttempts">Maximum number of players tried per episode.</param>
    public ShindenProvider(
        HostHttp http,
        IProviderConfig config,
        TimeSpan? antibotDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        int maxResolveAttempts = 3)
    {
        _http = http;
        _config = config;
        _antibotDelay = antibotDelay ?? TimeSpan.FromMilliseconds(5000);
        _delay = delay ?? ((t, ct) => Task.Delay(t, ct));
        _maxResolveAttempts = maxResolveAttempts;
    }

    /// <summary>Convenience factory for the composition root: wraps <paramref name="client"/> with the Polish Accept-Language.</summary>
    public static ShindenProvider Create(HttpClient client, IProviderConfig config, HttpClient? noRedirectClient = null) =>
        new(new HostHttp(client, AcceptLanguage, noRedirectClient), config);

    /// <summary>Host lookup; overridable in tests so a fake server can stand in for a real video host.</summary>
    internal Func<string, IHostExtractor?> HostResolver { get; set; } = HostRegistry.ByHost;

    public string Id => ProviderId;
    public string DisplayName => "Shinden (PL)";
    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string> { "pl" };
    public int Priority => 0;

    private IReadOnlyList<string> Bases() => _config.ForProvider(ProviderId).MirrorsOr([DefaultBaseUrl]);
    private string Api() => _config.ForProvider(ProviderId).Param("apiUrl", DefaultApiUrl).TrimEnd('/');

    public Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync(Bases(), b => SearchAtAsync(b, query, cancellationToken));

    private async Task<IReadOnlyList<Anime>> SearchAtAsync(string baseUrl, string query, CancellationToken ct)
    {
        var url = $"{baseUrl}/series?search={Uri.EscapeDataString(query.Trim())}";
        return ShindenParsers.ParseSearch(await _http.GetStringAsync(url, $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false), ProviderId, baseUrl);
    }

    public async Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        string kind, html, baseUrl;
        try
        {
            (kind, html, baseUrl) = await Mirrors.WithMirrorsAsync(Bases(), async b =>
            {
                var (k, h) = await SeriesPageAsync(b, anime.Id, "", cancellationToken).ConfigureAwait(false);
                return (k, h, b);
            }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return anime;
        }
        _kinds[anime.Id] = kind;
        var d = ShindenParsers.ParseDetails(html);
        if (d.MalId is { } m) _mal[anime.Id] = m;
        return anime with
        {
            Synopsis = anime.Synopsis ?? d.Synopsis,
            CoverUrl = anime.CoverUrl ?? ShindenParsers.Absolute(baseUrl, d.Cover),
            MalId = anime.MalId ?? d.MalId,
        };
    }

    public Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync(Bases(), b => EpisodesAtAsync(b, anime, cancellationToken));

    private async Task<IReadOnlyList<Episode>> EpisodesAtAsync(string baseUrl, Anime anime, CancellationToken ct)
    {
        var (kind, first) = await SeriesPageAsync(baseUrl, anime.Id, "/episodes?na_stronie=100", ct).ConfigureAwait(false);
        _kinds[anime.Id] = kind;
        var rows = ShindenParsers.ParseEpisodeRows(first);
        var pages = Math.Min(ShindenParsers.PageCount(first), MaxPages);
        for (var page = 2; page <= pages; page++)
        {
            rows.AddRange(ShindenParsers.ParseEpisodeRows(
                await _http.GetStringAsync($"{baseUrl}/{kind}/{anime.Id}/episodes?na_stronie=100&strona={page}", $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false)));
        }
        // The site lists newest first; the app wants ascending.
        var episodes = rows.DistinctBy(e => e.Id).Reverse().ToList();
        if (episodes.Count == 0) throw new ProviderException($"Shinden lists no episodes with players for '{anime.Title}'");
        return episodes;
    }

    public Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync(Bases(), b => StreamsAtAsync(b, anime, episode, mode, cancellationToken));

    private async Task<StreamSet> StreamsAtAsync(string baseUrl, Anime anime, Episode episode, TranslationMode mode, CancellationToken ct)
    {
        var page = await _http.GetStringAsync($"{baseUrl}/episode/{anime.Id}/view/{episode.Id}", $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false);
        var players = ShindenParsers.ParsePlayers(page);
        var auth = ShindenParsers.ParseAuth(page) ?? GuestAuth;
        var candidates = ShindenSelection.Rank(players, mode);
        if (candidates.Count == 0)
        {
            var have = string.Join(", ", players.Select(ShindenSelection.Label).Distinct());
            if (have.Length == 0) have = "none";
            throw new ProviderException(
                (mode == TranslationMode.Dub ? "No Polish dub/lektor players" : "No subtitled players") +
                $" for episode {episode.Number} (available: {have})");
        }
        var skipped = new List<string>();
        var attempts = 0;
        foreach (var p in candidates)
        {
            if (HostRegistry.ExternalReason(p.Player) is { } reason)
            {
                skipped.Add($"{p.Player}: {reason}");
                continue;
            }
            if (attempts >= _maxResolveAttempts)
            {
                skipped.Add($"{p.Player}: not tried (attempt limit)");
                continue;
            }
            attempts++;
            PlayerShow show;
            try
            {
                show = await ResolveShowAsync(baseUrl, p.OnlineId, auth, ct).ConfigureAwait(false);
            }
            catch (ProviderException e)
            {
                skipped.Add($"{p.Player}: {e.Message}");
                continue;
            }
            switch (show)
            {
                case PlayerShow.External ext:
                    skipped.Add($"{p.Player}: external link to {ext.Label ?? ext.Url}");
                    break;
                case PlayerShow.Failure fail:
                    skipped.Add($"{p.Player}: {fail.Message}");
                    break;
                case PlayerShow.Embed embed:
                    var host = HostUtil.HostOf(embed.Url);
                    // Some hosts rotate domains (Vidara), so fall back to shinden's own player label.
                    var extractor = HostResolver(host) ?? HostRegistry.ByPlayerName(p.Player);
                    if (extractor is null)
                    {
                        skipped.Add($"{p.Player}: unsupported host '{host}'");
                        break;
                    }
                    try
                    {
                        var r = await extractor.ExtractAsync(embed.Url, _http, "https://shinden.pl/", ct).ConfigureAwait(false);
                        IReadOnlyList<StreamVariant> variants = r.Hls
                            ? []
                            : r.Variants.Count > 0 ? r.Variants : [new StreamVariant(p.Res, r.Url)];
                        return new StreamSet
                        {
                            MasterUrl = r.Hls ? r.Url : null,
                            Variants = variants,
                            Headers = r.Headers,
                            MalId = anime.MalId ?? (_mal.TryGetValue(anime.Id, out var mal) ? mal : null),
                            LanguageLabel = ShindenSelection.Label(p),
                        };
                    }
                    catch (ProviderException e)
                    {
                        skipped.Add($"{p.Player}: {e.Message}");
                    }
                    break;
            }
        }
        var tokenSource = ReferenceEquals(auth, GuestAuth) ? "built-in guest token" : "page token";
        throw new ProviderException($"No playable Shinden source for episode {episode.Number} ({tokenSource}): {string.Join("; ", skipped)}");
    }

    /// <summary><c>player_load</c> then, after the antibot pause, <c>player_show</c>; mirrors the reference tool's two-step protocol.</summary>
    private async Task<PlayerShow> ResolveShowAsync(string baseUrl, string onlineId, string authToken, CancellationToken ct)
    {
        var api = Api();
        var auth = $"auth={authToken}";
        await _http.GetStringAsync($"{api}/xhr/{onlineId}/player_load?{auth}", $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false);
        await _delay(_antibotDelay, ct).ConfigureAwait(false);
        return ShindenParsers.ParsePlayerShow(
            await _http.GetStringAsync($"{api}/xhr/{onlineId}/player_show?{auth}&width=0&height=-1", $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false));
    }

    /// <summary>Fetches <c>/&lt;kind&gt;/&lt;id&gt;&lt;suffix&gt;</c>, trying <c>series</c> then <c>titles</c> (the site uses both prefixes for anime).</summary>
    private async Task<(string Kind, string Html)> SeriesPageAsync(string baseUrl, string idSlug, string suffix, CancellationToken ct)
    {
        if (!ShindenParsers.IsValidId(idSlug)) throw new ProviderException($"Unsupported Shinden id '{idSlug}'");
        var order = new List<string>();
        if (_kinds.TryGetValue(idSlug, out var known)) order.Add(known);
        order.Add("series");
        order.Add("titles");
        HttpStatusException? last = null;
        foreach (var kind in order.Distinct())
        {
            try
            {
                return (kind, await _http.GetCheckedAsync($"{baseUrl}/{kind}/{idSlug}{suffix}", $"{baseUrl}/", cancellationToken: ct).ConfigureAwait(false));
            }
            catch (HttpStatusException e)
            {
                if (e.Code != 404) throw new ProviderException(e.Message, e);
                last = e;
            }
        }
        throw new ProviderException($"Shinden page not found for '{idSlug}' (HTTP {last?.Code ?? 404})", last);
    }
}

/// <summary>Pure ranking and labelling of shinden player entries.</summary>
internal static class ShindenSelection
{
    /// <summary>Entries usable for <paramref name="mode"/>, best first. DUB = Polish audio; SUB = other audio, Polish subs preferred.</summary>
    public static List<PlayerEntry> Rank(IEnumerable<PlayerEntry> players, TranslationMode mode) =>
        players
            .Where(p => (p.Audio == "pl") == (mode == TranslationMode.Dub))
            .OrderByDescending(NativeHost)
            .ThenByDescending(p => SubRank(p.Sub))
            .ThenByDescending(p => p.Machine ? 0 : 1)
            .ThenByDescending(p => p.Res ?? 0)
            .ToList();

    /// <summary>Players with a known extractor are tried before link-out services and unknown hosts.</summary>
    private static int NativeHost(PlayerEntry p) =>
        HostRegistry.ByPlayerName(p.Player) is not null ? 2
        : HostRegistry.ExternalReason(p.Player) is null ? 1
        : 0;

    private static int SubRank(string sub) => sub switch { "pl" => 3, "en" => 2, "" => 0, _ => 1 };

    public static string Label(PlayerEntry p)
    {
        var audio = p.Audio switch
        {
            "pl" => "Polish dub/lektor",
            "jp" => "JP audio",
            "en" => "EN audio",
            "" => "audio ?",
            _ => $"{p.Audio.ToUpperInvariant()} audio",
        };
        var subs = p.Sub switch
        {
            "" => null,
            "pl" => "PL subs",
            "en" => "EN subs",
            _ => $"{p.Sub.ToUpperInvariant()} subs",
        };
        return subs is null ? audio : $"{audio} + {subs}";
    }
}
