using System.Text;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.AnimeFire;

/// <summary>
/// Portuguese (pt-BR) provider, beta. Port of <c>AnimefireProvider</c> in provider/animefire/AnimefireProvider.kt. The site is
/// a SPA over a JSON API (<c>api.animefire.one</c>, every request carries <c>v=3</c>); episodes resolve to HLS master playlists
/// on the site's CDN, tagged <c>legendado</c> (hardsub pt-BR) or <c>dublado</c>.
/// </summary>
public sealed class AnimeFireProvider(HttpClient client, IProviderConfig config) : IAnimeProvider
{
    /// <summary>Provider id; same string as the Kotlin app.</summary>
    internal const string ProviderId = "animefire";
    internal const string DefaultBaseUrl = "https://animefire.one";
    internal const string DefaultApiUrl = "https://api.animefire.one";

    private readonly HostHttp _http = new(client, "pt-BR,pt;q=0.9,en;q=0.7");

    public string Id => ProviderId;

    public string DisplayName => "AnimeFire";

    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string>(StringComparer.Ordinal) { "pt" };

    public bool GfwBlocked => false;

    public int Priority => 0;

    public async Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        var search = query.Trim();
        if (search.Length == 0) return [];
        return await Mirrors.WithMirrorsAsync(Bases(), async site =>
        {
            var json = await GetJsonAsync(site, ["animes", "pesquisar"], [("q", search), ("page", "1")], cancellationToken);
            return AnimeFireParsers.ParseSearch(json, ProviderId);
        });
    }

    public async Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        Anime hero;
        try
        {
            hero = await Mirrors.WithMirrorsAsync(Bases(), async site =>
                AnimeFireParsers.ParseDetails(await GetJsonAsync(site, ["anime", anime.Id], [], cancellationToken), ProviderId));
        }
        catch (ProviderException)
        {
            // Best-effort enrichment: keep the search hit. Cloudflare challenges still propagate.
            return anime;
        }
        return anime with
        {
            Synopsis = anime.Synopsis ?? hero.Synopsis,
            CoverUrl = anime.CoverUrl ?? hero.CoverUrl,
        };
    }

    /// <summary>Episodes are not split by audio: the same list serves SUB and DUB, as in the Kotlin app.</summary>
    public async Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default) =>
        await Mirrors.WithMirrorsAsync(Bases(), async site =>
            AnimeFireParsers.ParseEpisodes(await GetJsonAsync(site, ["anime", anime.Id], [], cancellationToken)));

    public Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync(Bases(), site => StreamsAtAsync(site, episode, mode, cancellationToken));

    private async Task<StreamSet> StreamsAtAsync(string site, Episode episode, TranslationMode mode, CancellationToken cancellationToken)
    {
        var dub = mode == TranslationMode.Dub;
        var json = await GetJsonAsync(site, ["episode", episode.Id], [], cancellationToken);
        var master = AnimeFireParsers.ParseStreamUrl(json, dub)
            ?? throw new ProviderException($"No {(dub ? "dublado" : "legendado")} source for episode {episode.Number}");
        var playlistText = await _http.GetStringAsync(master, referer: $"{site}/", cancellationToken: cancellationToken);
        var playlist = M3u8.ParseMaster(playlistText, master);
        var variants = playlist.Variants.Count > 0
            ? playlist.Variants
            : new List<StreamVariant> { new(null, master) };
        return new StreamSet
        {
            MasterUrl = master,
            Variants = variants,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Referer"] = $"{site}/",
                ["Origin"] = site,
                ["User-Agent"] = NetworkConstants.AniCliUserAgent,
            },
            ExternalAudioUrl = playlist.ExternalAudioUrl,
            HardsubLanguage = dub ? null : "pt",
        };
    }

    private Task<string> GetJsonAsync(
        string site, IReadOnlyList<string> segments, IReadOnlyList<(string Key, string Value)> query, CancellationToken cancellationToken) =>
        _http.GetStringAsync(BuildUrl(Api(), segments, query), referer: $"{site}/", cancellationToken: cancellationToken);

    /// <summary>Appends path segments and query, then <c>v=3</c> as the last parameter, like the Kotlin <c>endpoint</c>.</summary>
    private static string BuildUrl(string api, IReadOnlyList<string> segments, IReadOnlyList<(string Key, string Value)> query)
    {
        var url = new StringBuilder(api);
        foreach (var segment in segments)
        {
            url.Append('/').Append(Uri.EscapeDataString(segment));
        }
        var separator = '?';
        foreach (var (key, value) in query)
        {
            url.Append(separator).Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            separator = '&';
        }
        return url.Append(separator).Append("v=3").ToString();
    }

    /// <summary>Base URLs (primary first, then mirrors), read from the remote config on every call.</summary>
    private IReadOnlyList<string> Bases() => config.ForProvider(ProviderId).MirrorsOr([DefaultBaseUrl]);

    /// <summary>API host from <c>params.apiUrl</c>, read on every call.</summary>
    private string Api() => config.ForProvider(ProviderId).Param("apiUrl", DefaultApiUrl).TrimEnd('/');
}
