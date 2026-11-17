using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// A source of anime, mirroring ani-cli's search, episodes, links pipeline. Mirrors <c>AnimeProvider</c> in
/// provider/api/AnimeProvider.kt. Implementations live in <c>AnimaWindows.Providers</c>, are registered by id, and are
/// consumed only through <see cref="IProviderChain"/>.
/// <para>
/// Error contract: methods throw <see cref="ProviderException"/> for expected failures (parse failure, empty/blocked
/// response, missing episode) and <see cref="CloudflareChallengeException"/> when Cloudflare serves a challenge. They honour
/// <see cref="OperationCanceledException"/> (never swallow it). They never return null.
/// </para>
/// </summary>
public interface IAnimeProvider
{
    /// <summary>Stable lowercase id (e.g. "hianime"); the key in settings, remote config, <see cref="Anime.ProviderId"/> and persisted rows.</summary>
    string Id { get; }

    /// <summary>Human-readable name for the UI.</summary>
    string DisplayName { get; }

    /// <summary>ML-Kit-style language codes of the content this provider serves natively ("en", "pl", "zh", ...).</summary>
    IReadOnlySet<string> ContentLanguages { get; }

    /// <summary>
    /// True when search results carry a reliable <see cref="Anime.DubEpisodes"/> (null/0 then means "no recorded dub").
    /// Providers that leave it unset cannot tell from a search hit, so their results must never be hidden in DUB mode.
    /// Default false.
    /// </summary>
    bool ReportsDubAvailability => false;

    /// <summary>True when the site is unreachable from mainland China; the chain drops such providers for "zh". Default false.</summary>
    bool GfwBlocked => false;

    /// <summary>Default position among providers of the same language (lower is tried first); ties break by <see cref="Id"/>. Default 100.</summary>
    int Priority => 100;

    /// <summary>Searches the provider. Returns an empty list for no hits. Result items have <see cref="Anime.ProviderId"/> = <see cref="Id"/>.</summary>
    Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default);

    /// <summary>Optional enrichment (cover, synopsis) for the details screen. Default implementation returns <paramref name="anime"/> unchanged.</summary>
    Task<Anime> DetailsAsync(Anime anime, CancellationToken cancellationToken = default) => Task.FromResult(anime);

    /// <summary>Episodes sorted ascending by numeric value of <see cref="Episode.Number"/>.</summary>
    Task<IReadOnlyList<Episode>> EpisodesAsync(Anime anime, TranslationMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves playable streams for one episode. Throws <see cref="ProviderException"/> when nothing playable could be
    /// found; the chain additionally treats a set with neither master nor variants as a failure.
    /// </summary>
    Task<StreamSet> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default);
}
