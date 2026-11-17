using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.AnimeParadise;

/// <summary>
/// animeparadise.moe: JSON API (<c>api.animeparadise.moe</c>) + an HLS proxy on <c>stream.animeparadise.moe</c>. SUB only.
/// The base URL is read through the provider config per request so a remote-config refresh applies without a restart.
/// <see cref="Anime.Id"/> is the site <c>link</c> slug; <see cref="Episode.Id"/> is <c>&lt;uid&gt;|&lt;anime _id&gt;</c> (the stream
/// endpoint needs both). Mirrors <c>AnimeParadiseProvider</c> in AnimeParadiseProvider.kt.
/// </summary>
public sealed class AnimeParadiseProvider : IAnimeProvider
{
    public const string ProviderId = "animeparadise";
    public const string DefaultBaseUrl = "https://api.animeparadise.moe";
    public const string DefaultStreamBase = "https://stream.animeparadise.moe";
    public const string SiteOrigin = "https://www.animeparadise.moe";

    private static readonly IReadOnlyDictionary<string, string> Langs = new Dictionary<string, string>
    {
        ["english"] = "en",
        ["italian"] = "it",
        ["portuguese"] = "pt",
        ["spanish"] = "es",
    };

    private static readonly IReadOnlyDictionary<string, string> ApiHeaders = new Dictionary<string, string>
    {
        ["Origin"] = SiteOrigin,
        ["Accept"] = "application/json, */*",
    };

    private readonly HostHttp _http;
    private readonly IProviderConfig _config;
    private readonly bool _includeAssSubtitles;

    /// <param name="client">Injected HTTP client.</param>
    /// <param name="config">Remote provider config, read on every request.</param>
    /// <param name="includeAssSubtitles">ASS tracks come from <c>/stream/file/&lt;id&gt;</c> (no file extension), which the player would mis-detect as WebVTT.</param>
    public AnimeParadiseProvider(HttpClient client, IProviderConfig config, bool includeAssSubtitles = false)
    {
        _http = new HostHttp(client);
        _config = config;
        _includeAssSubtitles = includeAssSubtitles;
    }

    public string Id => ProviderId;
    public string DisplayName => "AnimeParadise";
    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string> { "en" };
    public bool GfwBlocked => false;
    public int Priority => 2;

    private string Base => _config.ForProvider(Id).BaseUrlOr(DefaultBaseUrl);
    private string StreamBase => _config.ForProvider(Id).Param("streamBase", DefaultStreamBase).TrimEnd('/');

    public async Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub) return [];
        var url = $"{Base}/search?sort=POPULARITY&page=1&q={Uri.EscapeDataString(query.Trim())}&v=1";
        var response = Decode(await GetAsync(url, cancellationToken).ConfigureAwait(false), AnimeParadiseJsonContext.Instance.ApSearchResponse);
        return (response.Data ?? []).Select(ToAnime).ToList();
    }

    public async Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        ApAnimeDto d;
        try
        {
            d = await FetchDetailsAsync(anime.Id, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return anime;
        }
        return anime with
        {
            Synopsis = anime.Synopsis ?? (string.IsNullOrWhiteSpace(d.Synopsys) ? null : d.Synopsys),
            CoverUrl = anime.CoverUrl ?? d.PosterImage?.Best(),
            Year = anime.Year ?? d.AnimeSeason?.Year,
            SubEpisodes = anime.SubEpisodes ?? d.Episodes,
            MalId = anime.MalId ?? d.Mappings?.Myanimelist,
        };
    }

    public async Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub) throw new ProviderException("AnimeParadise has no dubbed episodes");
        var origin = (await FetchDetailsAsync(anime.Id, cancellationToken).ConfigureAwait(false)).Id
            ?? throw new ProviderException($"Anime '{anime.Id}' has no id");
        var list = Decode(
            await GetAsync($"{Base}/anime/{Enc(origin)}/episode", cancellationToken).ConfigureAwait(false),
            AnimeParadiseJsonContext.Instance.ApEpisodesResponse).Data ?? [];
        return list.Where(e => e.Uid is not null && e.Number is not null)
            .Select(e => new Episode($"{e.Uid}|{e.Origin ?? origin}", e.Number!, string.IsNullOrWhiteSpace(e.Title) ? null : e.Title))
            .OrderBy(e => HtmlJsoupCompat.ToDoubleOrNull(e.Number) ?? double.MaxValue)
            .ToList();
    }

    public async Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub) throw new ProviderException("AnimeParadise has no dubbed episodes");
        var parts = episode.Id.Split('|', 2);
        var uid = parts[0];
        var origin = parts.Length > 1 ? parts[1] : null;
        if (string.IsNullOrEmpty(origin)) throw new ProviderException($"Unsupported AnimeParadise episode id '{episode.Id}'");
        var url = $"{Base}/ep/{Enc(uid)}?origin={Uri.EscapeDataString(origin)}";
        var ep = Decode(await GetAsync(url, cancellationToken).ConfigureAwait(false), AnimeParadiseJsonContext.Instance.ApEpisodeResponse).Data?.Episode;
        var token = ep?.StreamLink;
        if (ep is null || string.IsNullOrWhiteSpace(token)) throw new ProviderException($"No stream for episode {episode.Number}");

        var master = $"{StreamBase}/m3u8?url={Uri.EscapeDataString(token)}";
        var parsed = M3u8.ParseMaster(await GetAsync(master, cancellationToken).ConfigureAwait(false), master);
        if (parsed.Variants.Count == 0) throw new ProviderException("Master playlist has no variants");

        var skip = ep.SkipData;
        return new StreamSet
        {
            MasterUrl = master,
            Variants = parsed.Variants,
            Headers = new Dictionary<string, string>
            {
                ["Origin"] = SiteOrigin,
                ["Referer"] = $"{SiteOrigin}/",
                ["User-Agent"] = NetworkConstants.AniCliUserAgent,
            },
            ExternalAudioUrl = parsed.ExternalAudioUrl,
            Subtitles = SubtitleTracks(ep.SubData ?? []),
            MalId = null,
            Intro = ToInterval(skip?.Intro),
            Outro = ToInterval(skip?.Outro),
        };
    }

    private List<SubtitleTrack> SubtitleTracks(List<ApSubDto> data)
    {
        var tracks = new List<(bool IsVtt, string Label, SubtitleTrack Track)>();
        foreach (var s in data)
        {
            if (string.IsNullOrWhiteSpace(s.Src)) continue;
            var isVtt = string.Equals(s.Type, "vtt", StringComparison.OrdinalIgnoreCase);
            if (!isVtt && !(_includeAssSubtitles && string.Equals(s.Type, "ass", StringComparison.OrdinalIgnoreCase))) continue;
            var url = s.Src.StartsWith("http", StringComparison.Ordinal) ? s.Src : $"{Base}/stream/file/{Enc(s.Src)}";
            var label = s.Label ?? "Unknown";
            Langs.TryGetValue(label.ToLowerInvariant(), out var lang);
            tracks.Add((isVtt, label, new SubtitleTrack(url, lang, label, false)));
        }
        // VTT first (native), English first within each format; the first English track is the default.
        var sorted = tracks
            .OrderBy(t => !t.IsVtt)
            .ThenBy(t => !string.Equals(t.Label, "English", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var defaultIdx = sorted.FindIndex(t => string.Equals(t.Label, "English", StringComparison.OrdinalIgnoreCase));
        return sorted.Select((t, i) => t.Track with { IsDefault = i == defaultIdx }).ToList();
    }

    private async Task<ApAnimeDto> FetchDetailsAsync(string link, CancellationToken cancellationToken) =>
        Decode(await GetAsync($"{Base}/anime/{Enc(link)}", cancellationToken).ConfigureAwait(false), AnimeParadiseJsonContext.Instance.ApDetailsResponse).Data
        ?? throw new ProviderException($"Anime '{link}' not found");

    private static string Enc(string segment) => Uri.EscapeDataString(segment);

    private static T Decode<T>(string text, JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(text, info) ?? throw new JsonException("null document");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException("Unexpected AnimeParadise response", e);
        }
    }

    private Task<string> GetAsync(string url, CancellationToken cancellationToken) =>
        _http.GetStringAsync(url, $"{SiteOrigin}/", headers: ApiHeaders, cancellationToken: cancellationToken);

    private static Anime ToAnime(ApAnimeDto d)
    {
        var id = d.Link ?? d.Id ?? throw new ProviderException("AnimeParadise result without id");
        return new Anime
        {
            Id = id,
            ProviderId = ProviderId,
            Title = d.Title ?? d.AlternativeTitle?.English ?? d.AlternativeTitle?.Romaji ?? id,
            CoverUrl = d.PosterImage?.Best(),
            SubEpisodes = d.Episodes,
            Year = d.AnimeSeason?.Year,
        };
    }

    private static SkipInterval? ToInterval(ApSkipDto? s)
    {
        if (s is null) return null;
        var start = s.Start ?? 0.0;
        var end = s.End ?? 0.0;
        return end > start && end > 0 ? new SkipInterval(start, end) : null;
    }
}
