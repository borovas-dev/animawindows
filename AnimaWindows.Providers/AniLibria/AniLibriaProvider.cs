using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.AniLibria;

/// <summary>
/// Russian provider backed by the official AniLiberty JSON API (https://anilibria.top/api/docs/v1). Port of
/// <c>AnilibriaProvider</c> in provider/anilibria/AnilibriaProvider.kt. Releases are published in Russian, so only
/// <see cref="TranslationMode.Sub"/> is served; Dub yields nothing.
/// </summary>
public sealed class AniLibriaProvider(HttpClient client, IProviderConfig config) : IAnimeProvider
{
    /// <summary>Provider id; same string as the Kotlin app.</summary>
    internal const string ProviderId = "anilibria";
    internal const string DefaultBaseUrl = "https://anilibria.top";
    internal const string DefaultApiPath = "/api/v1";

    private readonly HostHttp _http = new(client, "ru,en;q=0.8");

    public string Id => ProviderId;

    public string DisplayName => "AniLibria";

    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string>(StringComparer.Ordinal) { "ru" };

    public bool GfwBlocked => false;

    public int Priority => 0;

    public async Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub || string.IsNullOrWhiteSpace(query)) return [];
        var search = query.Trim();
        return await Mirrors.WithMirrorsAsync(Bases(), async origin =>
        {
            var url = $"{origin}{ApiPath()}/app/search/releases?query={Uri.EscapeDataString(search)}";
            var json = await _http.GetStringAsync(url, cancellationToken: cancellationToken);
            return AniLibriaParsers.ParseSearch(json, origin);
        });
    }

    public async Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        (string Origin, AniLibriaRelease Release) fetched;
        try
        {
            fetched = await FetchReleaseAsync(anime.Id, cancellationToken);
        }
        catch (ProviderException)
        {
            // Best-effort enrichment: keep the search hit. Cloudflare challenges still propagate.
            return anime;
        }
        var full = AniLibriaParsers.ToAnime(fetched.Release, fetched.Origin);
        return anime with
        {
            CoverUrl = anime.CoverUrl ?? full.CoverUrl,
            Synopsis = anime.Synopsis ?? full.Synopsis,
            SubEpisodes = anime.SubEpisodes ?? full.SubEpisodes,
            Year = anime.Year ?? full.Year,
            MalId = anime.MalId ?? full.MalId,
        };
    }

    public async Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub) return [];
        var (_, release) = await FetchReleaseAsync(anime.Id, cancellationToken);
        return AniLibriaParsers.ToEpisodes(release);
    }

    public async Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        if (mode == TranslationMode.Dub)
        {
            throw new ProviderException("AniLibria has no separate dub (Russian voice-over only)");
        }
        var (origin, release) = await FetchReleaseAsync(anime.Id, cancellationToken);
        var episodeData = release.Episodes?.FirstOrDefault(e => e.Id == episode.Id)
            ?? throw new ProviderException($"Episode {episode.Number} not found in AniLibria release {anime.Id}");
        var variants = AniLibriaParsers.HlsVariants(episodeData);
        if (variants.Count == 0) throw new ProviderException($"No HLS source for episode {episode.Number}");
        return new StreamSet
        {
            MasterUrl = variants[0].Url,
            Variants = variants,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = $"{origin}/",
                ["User-Agent"] = NetworkConstants.AniCliUserAgent,
            },
            MalId = release.Mal?.Id,
            Intro = AniLibriaParsers.ToInterval(episodeData.Opening),
            Outro = AniLibriaParsers.ToInterval(episodeData.Ending),
        };
    }

    /// <summary>Fetches one release from the first mirror that answers; returns the origin that served it.</summary>
    private async Task<(string Origin, AniLibriaRelease Release)> FetchReleaseAsync(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ProviderException($"Bad AniLibria id '{id}'");
        }
        return await Mirrors.WithMirrorsAsync(Bases(), async origin =>
        {
            var json = await _http.GetStringAsync($"{origin}{ApiPath()}/anime/releases/{id}", cancellationToken: cancellationToken);
            return (origin, AniLibriaParsers.ParseRelease(json));
        });
    }

    /// <summary>Base URLs (primary first, then mirrors), read from the remote config on every call.</summary>
    private IReadOnlyList<string> Bases() => config.ForProvider(ProviderId).MirrorsOr([DefaultBaseUrl]);

    private string ApiPath() => "/" + config.ForProvider(ProviderId).Param("apiPath", DefaultApiPath).Trim('/');
}
