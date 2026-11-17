using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.KickAssAnime;

/// <summary>
/// KickAssAnime (kaa.lt): JSON API on the site plus the <c>krussdomi.com</c> cat-player, whose page embeds the HLS manifest and
/// soft WebVTT subtitles. SUB = <c>ja-JP</c> episodes, DUB = <c>en-US</c> episodes (the episode slug differs per language, so
/// <see cref="Episode.Id"/> is the full <c>ep-&lt;number&gt;-&lt;slug&gt;</c> path segment). The base URL is read through the provider
/// config per request. Mirrors <c>KickAssAnimeProvider</c> in KickAssAnimeProvider.kt.
/// <para>
/// The master playlist has one audio rendition per language and video-only variants, and the segments (really MPEG-TS
/// disguised as <c>.jpg</c>) need an <c>Origin</c> header. So the stream is exposed as variants plus an explicit
/// <see cref="StreamSet.ExternalAudioUrl"/> (English for DUB, Japanese for SUB) rather than as the master.
/// </para>
/// </summary>
public sealed partial class KickAssAnimeProvider : IAnimeProvider
{
    public const string ProviderId = "kickassanime";
    public const string DefaultBaseUrl = "https://kaa.lt";
    private const string LangSub = "ja-JP";
    private const string LangDub = "en-US";

    [GeneratedRegex("\"manifest\":\\[0,\"([^\"]+)\"\\]")]
    private static partial Regex ManifestRegex();

    [GeneratedRegex("\"language\":\\[0,\"([^\"]*)\"\\],\"name\":\\[0,\"([^\"]*)\"\\],\"src\":\\[0,\"([^\"]+)\"\\]")]
    private static partial Regex SubtitleRegex();

    [GeneratedRegex("LANGUAGE=\"([^\"]*)\"")]
    private static partial Regex AttrLanguageRegex();

    [GeneratedRegex("URI=\"([^\"]*)\"")]
    private static partial Regex AttrUriRegex();

    private static readonly IReadOnlyDictionary<string, string> SiteHeaders =
        new Dictionary<string, string> { ["Accept"] = "application/json, */*" };

    private readonly HostHttp _http;
    private readonly IProviderConfig _config;

    public KickAssAnimeProvider(HttpClient client, IProviderConfig config)
    {
        _http = new HostHttp(client);
        _config = config;
    }

    public string Id => ProviderId;
    public string DisplayName => "KickAssAnime";
    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string> { "en" };
    public bool GfwBlocked => false;
    public int Priority => 1;

    private string Base => _config.ForProvider(Id).BaseUrlOr(DefaultBaseUrl);

    public async Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        var baseUrl = Base;
        var body = JsonSerializer.Serialize(new KaaSearchRequest { Query = query.Trim() }, KickAssJsonContext.Instance.KaaSearchRequest);
        var text = await _http.PostJsonAsync($"{baseUrl}/api/fsearch", body, headers: SiteHeaders, cancellationToken: cancellationToken).ConfigureAwait(false);
        var result = Decode(text, KickAssJsonContext.Instance.KaaSearchResponse).Result ?? [];
        var wanted = Lang(mode);
        return result
            .Where(h => !string.IsNullOrWhiteSpace(h.Slug)
                && ((h.Locales ?? []).Contains(wanted) || (mode == TranslationMode.Sub && (h.Locales ?? []).Count == 0)))
            .Select(h => ToAnime(h, baseUrl))
            .ToList();
    }

    public async Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var lang = Lang(mode);
        var first = await EpisodePageAsync(anime.Id, 1, lang, cancellationToken).ConfigureAwait(false);
        var pages = Math.Max(first.Pages?.Count ?? 0, 1);
        var all = new List<KaaEp>(first.Result ?? []);
        for (var p = 2; p <= pages; p++)
        {
            all.AddRange((await EpisodePageAsync(anime.Id, p, lang, cancellationToken).ConfigureAwait(false)).Result ?? []);
        }
        return all.Where(e => !string.IsNullOrWhiteSpace(e.Slug))
            .DistinctBy(e => e.Slug)
            .OrderBy(e => e.EpisodeNumber ?? 0.0)
            .Select(e =>
            {
                var number = e.EpisodeString ?? NumberOf(e.EpisodeNumber ?? 0.0);
                return new Episode(
                    $"ep-{number}-{e.Slug}",
                    number,
                    string.IsNullOrWhiteSpace(e.Title) ? null : e.Title);
            })
            .ToList();
    }

    public async Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var baseUrl = Base;
        var showSlug = Segment(anime.Id);
        var epSegment = Segment(episode.Id);
        var ep = Decode(
            await _http.GetStringAsync($"{baseUrl}/api/show/{showSlug}/episode/{epSegment}", headers: SiteHeaders, cancellationToken: cancellationToken).ConfigureAwait(false),
            KickAssJsonContext.Instance.KaaEpisodeResponse);
        if (mode == TranslationMode.Dub && ep.Language is not null && ep.Language != LangDub)
        {
            throw new ProviderException($"KickAssAnime has no English dub for episode {episode.Number}");
        }
        var server = (ep.Servers ?? []).FirstOrDefault(s =>
            (s.Src ?? "").Contains("/cat-player/player", StringComparison.Ordinal) && !(s.Src ?? "").Contains("type=dash", StringComparison.Ordinal))
            ?? throw new ProviderException($"No supported KickAssAnime server for episode {episode.Number}");
        var serverSrc = server.Src!;
        if (!Uri.TryCreate(serverSrc, UriKind.Absolute, out var playerUrl))
        {
            throw new ProviderException($"Bad KickAssAnime player URL '{serverSrc}'");
        }
        var origin = playerUrl.GetLeftPart(UriPartial.Authority);
        var headers = new Dictionary<string, string>
        {
            ["Origin"] = origin,
            ["Referer"] = $"{origin}/",
            ["User-Agent"] = NetworkConstants.AniCliUserAgent,
        };

        var page = (await _http.GetStringAsync(serverSrc, headers: headers, cancellationToken: cancellationToken).ConfigureAwait(false)).Replace("&quot;", "\"");
        var manifestMatch = ManifestRegex().Match(page);
        if (!manifestMatch.Success) throw new ProviderException("No stream found on the KickAssAnime player page");
        var manifest = manifestMatch.Groups[1].Value.Replace("\\/", "/");
        var master = await _http.GetStringAsync(manifest, headers: headers, cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = M3u8.ParseMaster(master, manifest);
        if (parsed.Variants.Count == 0) throw new ProviderException("KickAssAnime master playlist has no variants");

        string? audio;
        if (mode == TranslationMode.Dub)
        {
            audio = AudioRendition(master, manifest, ["eng", "en"])
                ?? throw new ProviderException($"KickAssAnime has no English dub for episode {episode.Number}");
        }
        else
        {
            audio = AudioRendition(master, manifest, ["jpn", "ja"]) ?? parsed.ExternalAudioUrl;
        }
        return new StreamSet
        {
            MasterUrl = audio is null ? manifest : null,
            Variants = parsed.Variants,
            Headers = headers,
            ExternalAudioUrl = audio,
            Subtitles = SubtitleTracks(page),
            LanguageLabel = mode == TranslationMode.Dub ? "English dub" : "Japanese audio + subs",
        };
    }

    private static List<SubtitleTrack> SubtitleTracks(string page)
    {
        var tracks = SubtitleRegex().Matches(page)
            .Select(m => new SubtitleTrack(m.Groups[3].Value.Replace("\\/", "/"), m.Groups[1].Value, m.Groups[2].Value, false))
            .ToList();
        var sorted = tracks.OrderBy(t => t.Language != "en").ToList();
        var def = sorted.FindIndex(t => t.Language == "en");
        return sorted.Select((t, i) => i == def ? t with { IsDefault = true } : t).ToList();
    }

    /// <summary>First <c>#EXT-X-MEDIA:TYPE=AUDIO</c> rendition whose LANGUAGE is in <paramref name="languages"/>, resolved against <paramref name="masterUrl"/>.</summary>
    private static string? AudioRendition(string master, string masterUrl, IReadOnlyCollection<string> languages)
    {
        foreach (var raw in master.Split(["\r\n", "\n", "\r"], StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (!line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) || !line.Contains("TYPE=AUDIO", StringComparison.Ordinal)) continue;
            var langMatch = AttrLanguageRegex().Match(line);
            var uriMatch = AttrUriRegex().Match(line);
            var lang = langMatch.Success ? langMatch.Groups[1].Value.ToLowerInvariant() : null;
            var uri = uriMatch.Success ? uriMatch.Groups[1].Value : null;
            if (lang is not null && languages.Contains(lang) && !string.IsNullOrWhiteSpace(uri)) return M3u8.Resolve(masterUrl, uri);
        }
        return null;
    }

    private async Task<KaaEpisodesResponse> EpisodePageAsync(string showSlug, int page, string lang, CancellationToken cancellationToken)
    {
        var url = $"{Base}/api/show/{Segment(showSlug)}/episodes?page={page}&lang={Uri.EscapeDataString(lang)}";
        var text = await _http.GetStringAsync(url, headers: SiteHeaders, cancellationToken: cancellationToken).ConfigureAwait(false);
        return Decode(text, KickAssJsonContext.Instance.KaaEpisodesResponse);
    }

    private static Anime ToAnime(KaaHit h, string baseUrl)
    {
        var slug = h.Slug!;
        var hq = h.Poster?.Hq;
        var synopsis = h.Synopsis?.Trim();
        return new Anime
        {
            Id = slug,
            ProviderId = ProviderId,
            Title = !string.IsNullOrWhiteSpace(h.TitleEn) ? h.TitleEn : !string.IsNullOrWhiteSpace(h.Title) ? h.Title : slug,
            CoverUrl = string.IsNullOrWhiteSpace(hq) ? null : $"{baseUrl}/image/poster/{hq}.webp",
            Synopsis = string.IsNullOrEmpty(synopsis) ? null : synopsis,
            SubEpisodes = h.EpisodeCount is > 0 ? h.EpisodeCount : null,
            Year = h.Year,
        };
    }

    private static string Lang(TranslationMode mode) => mode == TranslationMode.Dub ? LangDub : LangSub;

    private static string Segment(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || !s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ProviderException($"Bad KickAssAnime id '{s}'");
        }
        return s;
    }

    private static string NumberOf(double d) =>
        d == Math.Truncate(d) ? ((long)d).ToString(CultureInfo.InvariantCulture) : d.ToString(CultureInfo.InvariantCulture);

    private static T Decode<T>(string text, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(text, info) ?? throw new JsonException("null document");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new ProviderException("Unexpected KickAssAnime response", e);
        }
    }
}
