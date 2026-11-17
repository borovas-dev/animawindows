using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.HiAnime;

/// <summary>
/// Port of ani-cli v5 hianime scraping. Base URL(s), server name and XOR key come from the provider config on every request.
/// Mirrors <c>HianimeProvider</c> in HianimeProvider.kt.
/// </summary>
public sealed class HianimeProvider : IAnimeProvider
{
    public const string ProviderId = "hianime";
    public const string DefaultBaseUrl = "https://hianime.at";

    private readonly HostHttp _http;
    private readonly IProviderConfig _config;

    public HianimeProvider(HttpClient client, IProviderConfig config)
    {
        _http = new HostHttp(client);
        _config = config;
    }

    public string Id => ProviderId;
    public string DisplayName => "HiAnime";
    public IReadOnlySet<string> ContentLanguages { get; } = new HashSet<string> { "en" };

    /// <summary>hianime.at is reachable per greatfire.org (the older hianime.to/.com domains were blocked).</summary>
    public bool GfwBlocked => false;

    public bool ReportsDubAvailability => true;
    public int Priority => 0;

    private ProviderRemoteConfig Cfg => _config.ForProvider(Id);

    private IReadOnlyList<string> Bases() => Cfg.MirrorsOr([DefaultBaseUrl]);

    public Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync<IReadOnlyList<Anime>>(Bases(), async baseUrl =>
        {
            var url = $"{baseUrl.TrimEnd('/')}/search?keyword={Uri.EscapeDataString(query.Trim()).Replace("%20", "+")}";
            var page = await GetAsync(url, null, cancellationToken).ConfigureAwait(false);
            if (page.Contains("<title>Just a moment", StringComparison.OrdinalIgnoreCase)) throw new CloudflareChallengeException(url);
            return HianimeParsers.ParseSearch(page, Id);
        });

    public async Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default)
    {
        var baseUrl = Bases()[0];
        foreach (var path in new[] { $"/{anime.Id}", $"/watch/{anime.Id}" })
        {
            string html;
            try
            {
                html = await GetAsync(baseUrl + path, null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            (string? Synopsis, string? Cover) parsed;
            try
            {
                parsed = HianimeParsers.ParseDetails(html);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                continue;
            }
            if (parsed.Synopsis is not null || parsed.Cover is not null)
            {
                return anime with { Synopsis = anime.Synopsis ?? parsed.Synopsis, CoverUrl = anime.CoverUrl ?? parsed.Cover };
            }
        }
        return anime;
    }

    public Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync<IReadOnlyList<Episode>>(Bases(), async baseUrl =>
        {
            var numeric = anime.Id.SubstringAfterLast('-');
            if (numeric.Length == 0 || !numeric.All(char.IsAsciiDigit))
            {
                throw new ProviderException($"Unsupported HiAnime id '{anime.Id}' (expected <slug>-<number>)");
            }
            var body = await GetAsync($"{baseUrl.TrimEnd('/')}/api/theme/episode/list/{numeric}", null, cancellationToken).ConfigureAwait(false);
            var all = HianimeParsers.ParseEpisodes(body, anime.Id);
            // The site lists every (sub) episode; dub usually lags behind, so cap to the dub count when known.
            var dubCount = anime.DubEpisodes;
            return mode == TranslationMode.Dub && dubCount is > 0 ? all.Take(dubCount.Value).ToList() : all;
        });

    public Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Mirrors.WithMirrorsAsync(Bases(), async baseUrl =>
        {
            baseUrl = baseUrl.TrimEnd('/');
            var cfg = Cfg;
            var serverName = cfg.Param("serverName", HianimeParsers.ServerName);
            var xorKey = cfg.Param("xorKey", HianimeParsers.EmbedKey);
            var type = mode == TranslationMode.Dub ? "dub" : "sub";
            var servers = await GetAsync($"{baseUrl}/api/theme/episode/servers?episodeId={episode.Id}", null, cancellationToken).ConfigureAwait(false);
            var embed = HianimeParsers.ParseEmbedUrl(servers, type, serverName)
                ?? throw new ProviderException($"No {serverName} {type} source for episode {episode.Number}");
            var refr = HianimeParsers.EmbedOrigin(embed);
            var malId = HianimeParsers.MalId(embed);

            var embedPage = await GetAsync(embed, $"{baseUrl}/", cancellationToken).ConfigureAwait(false);
            var blob = HianimeParsers.ExtractBlob(embedPage)
                ?? throw new ProviderException("Embed page has no stream config (window.__P)");
            HianimeParsers.EmbedConfig embedConfig;
            try
            {
                embedConfig = HianimeParsers.ParseEmbedConfig(HianimeParsers.DeobfuscateBlob(blob, xorKey));
            }
            catch (FormatException e)
            {
                throw new ProviderException("Could not decode embed stream config", e);
            }
            var master = embedConfig.MasterUrl ?? throw new ProviderException("No m3u8 source in embed config");

            var playlist = await GetAsync(master, refr, cancellationToken).ConfigureAwait(false);
            var parsed = M3u8.ParseMaster(playlist, master);
            if (parsed.Variants.Count == 0) throw new ProviderException("Master playlist has no variants");
            return new StreamSet
            {
                MasterUrl = master,
                Variants = parsed.Variants,
                Headers = new Dictionary<string, string>
                {
                    ["Referer"] = refr,
                    ["User-Agent"] = NetworkConstants.AniCliUserAgent,
                },
                ExternalAudioUrl = parsed.ExternalAudioUrl,
                Subtitles = embedConfig.Subtitles,
                MalId = malId,
                Intro = embedConfig.Intro,
                Outro = embedConfig.Outro,
                SpriteVttUrl = embedConfig.SpriteVttUrl,
            };
        });

    private Task<string> GetAsync(string url, string? referer, CancellationToken cancellationToken) =>
        _http.GetStringAsync(url, referer, cancellationToken: cancellationToken);
}
