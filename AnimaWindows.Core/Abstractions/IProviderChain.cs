using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Registry of provider ids. Mirrors <c>ProviderRegistry</c> in core/model/Contracts.kt (implemented by the provider chain).
/// </summary>
public interface IProviderRegistry
{
    /// <summary>Ids of every registered provider, in registration order. Includes disabled providers.</summary>
    IReadOnlyList<string> Ids { get; }
}

/// <summary>
/// Tells the chain whether a provider is currently allowed (remote kill switch). Mirrors <c>ProviderAvailability</c> in
/// provider/api/ProviderChain.kt. Implemented in the app from <see cref="IProviderConfig"/>.
/// </summary>
public interface IProviderAvailability
{
    /// <summary>True when provider <paramref name="id"/> may be used right now. Must be cheap and synchronous.</summary>
    bool IsEnabled(string id);
}

/// <summary>Options of the chain implementation. Mirrors the constructor defaults of <c>ProviderChain</c>.</summary>
/// <param name="Timeout">Per-provider limit for resolving streams, episode lists and search (Kotlin default 20 s). Search/streams attempts get Timeout + 5 s overall.</param>
public sealed record ProviderChainOptions(TimeSpan Timeout)
{
    public static ProviderChainOptions Default { get; } = new(TimeSpan.FromSeconds(20));
}

/// <summary>
/// Language-aware, failover-capable facade over all <see cref="IAnimeProvider"/>s. Mirrors <c>ProviderChain</c> in
/// provider/api/ProviderChain.kt (public shape). A singleton; UI and services depend on this, never on individual providers.
/// <para>
/// Chain rules (<see cref="ChainFor"/>): the content language is <see cref="AppSettings.ContentLanguage"/> ("auto" = subtitle
/// language, else device language if some provider serves it, else "en"); providers serving it come first (ordered by
/// <see cref="AppSettings.ProviderOrder"/>[lang], then <see cref="IAnimeProvider.Priority"/>, then id), followed by English
/// providers as fallback for non-English languages. For "zh", <see cref="IAnimeProvider.GfwBlocked"/> providers are always
/// excluded. <see cref="AppSettings.DisabledProviders"/> and providers disabled via <see cref="IProviderAvailability"/> are
/// excluded.
/// </para>
/// </summary>
public interface IProviderChain : IProviderRegistry
{
    /// <summary>Any registered provider by id (ignores disabled/language filters), e.g. to open a library entry; null if unknown.</summary>
    IAnimeProvider? Provider(string id);

    /// <summary>
    /// Concrete language for <paramref name="settings"/>: explicit ContentLanguage, else SubtitleLanguage, else the device
    /// language when some provider serves it, else "en". Result is normalised: trimmed, lowercase, region suffix ("-"/"_") dropped.
    /// </summary>
    string EffectiveLanguage(AppSettings settings);

    /// <summary>Ordered providers to use for <paramref name="settings"/>; see the type remarks. Pure with respect to settings.</summary>
    IReadOnlyList<IAnimeProvider> ChainFor(AppSettings settings);

    /// <summary>Chain for the current settings (<c>ISettingsStore.Current</c>).</summary>
    Task<IReadOnlyList<IAnimeProvider>> ChainAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches every provider in the chain in parallel and merges results (by MAL id, else normalised title containing only
    /// letters/digits lowercased; at most one entry per provider per merged show). Earlier chain providers come first; each merged
    /// <see cref="Anime"/> keeps the first provider's ids and lists the others in <see cref="Anime.Alternates"/>. Individual
    /// failures are tolerated; if every provider fails the first failure (a <see cref="CloudflareChallengeException"/> if any) is
    /// rethrown. Returns an empty list when the chain is empty.
    /// </summary>
    Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves streams for <paramref name="episode"/> of <paramref name="anime"/>. Candidates: the anime's own provider, then
    /// providers in <see cref="Anime.Alternates"/> (in chain order); alternates are matched by numerically-equal episode number via
    /// <c>EpisodesAsync</c>. A candidate fails on exception, timeout, or no playable variant/master. With
    /// <see cref="SourceStrategy.BestQuality"/> the first two candidates are resolved concurrently and the better one (subtitles
    /// if wanted, then max height) wins; ties favour the earlier one. Throws if all candidates fail (a
    /// <see cref="CloudflareChallengeException"/> if any raised one), or <see cref="NoProviderAvailableException"/> when there are
    /// no candidates.
    /// </summary>
    Task<ResolvedStreams> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default);
}
