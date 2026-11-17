using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Search history, watch progress, continue-watching and favourites. Mirrors <c>LibraryRepository</c> in
/// core/model/Contracts.kt and the Room DAOs / <c>RoomLibraryRepository</c> in core/database. Implemented in <c>AnimaWindows.Data</c> (SQLite).
/// <para>
/// OBSERVATION CONVENTION (applies to every <c>Observe*</c> method in this and <see cref="IDownloadRepository"/>): the Kotlin
/// <c>Flow</c>s become cold <see cref="IAsyncEnumerable{T}"/>. Enumerating yields the current value immediately, then a new value
/// after every committed change that affects the result (consecutive equal values are suppressed, like
/// <c>distinctUntilChanged</c>). The sequence never completes on its own and ends only when <c>cancellationToken</c> is
/// cancelled (cancellation ends enumeration quietly or throws OperationCanceledException, consumers must tolerate both).
/// Consume with <c>await foreach</c> on a background task and marshal to the UI thread yourself.
/// </para>
/// <para>
/// All timestamps are Unix epoch milliseconds from <see cref="IClock"/>. Identity of an anime is (providerId, animeId); of an
/// episode (providerId, animeId, episodeNumber).
/// </para>
/// </summary>
public interface ILibraryRepository
{
    /// <summary>Maximum stored search-history entries (older ones are trimmed on <see cref="AddSearchAsync"/>). Kotlin <c>MAX_SEARCHES</c>.</summary>
    const int MaxSearches = 50;

    /// <summary>Normalisation applied to history queries: trim then lowercase invariant. Kotlin <c>normalizeQuery</c>.</summary>
    static string NormalizeQuery(string query) => query.Trim().ToLowerInvariant();

    /// <summary>Most recent searches, newest first, at most <paramref name="limit"/> (default 20).</summary>
    IAsyncEnumerable<IReadOnlyList<string>> ObserveRecentSearches(int limit = 20, CancellationToken cancellationToken = default);

    /// <summary>Records a search (normalised; blank is a no-op; refreshes its timestamp if present) and trims history to <see cref="MaxSearches"/>, atomically.</summary>
    Task AddSearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Removes one (normalised) query; no-op when absent.</summary>
    Task RemoveSearchAsync(string query, CancellationToken cancellationToken = default);

    /// <summary>Deletes the whole search history.</summary>
    Task ClearSearchesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// "Continue watching": one entry per anime that has a last-watched time and progress, newest last-watched first; <see cref="WatchEntry.Last"/>
    /// is that anime's most recently updated progress row.
    /// </summary>
    IAsyncEnumerable<IReadOnlyList<WatchEntry>> ObserveContinueWatching(CancellationToken cancellationToken = default);

    /// <summary>All progress rows of one anime (any order).</summary>
    IAsyncEnumerable<IReadOnlyList<EpisodeProgress>> ObserveProgressForAnime(string providerId, string animeId, CancellationToken cancellationToken = default);

    /// <summary>Progress of one episode, or null.</summary>
    Task<EpisodeProgress?> GetProgressAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);

    /// <summary>
    /// In one transaction: inserts the anime row if missing, refreshes its metadata (title always; cover, synopsis, malId,
    /// alternates, episode counts only when the new value is non-null, i.e. COALESCE semantics) and last-watched time = now,
    /// then upserts the episode progress with updatedAt = now.
    /// </summary>
    Task SaveProgressAsync(Anime anime, string episodeNumber, long positionMs, long durationMs, CancellationToken cancellationToken = default);

    /// <summary>In one transaction: deletes all progress of the anime and clears its last-watched time (the anime row and favourite flag stay).</summary>
    Task RemoveFromHistoryAsync(string providerId, string animeId, CancellationToken cancellationToken = default);

    /// <summary>Favourite anime, most recently favourited first.</summary>
    IAsyncEnumerable<IReadOnlyList<Anime>> ObserveFavorites(CancellationToken cancellationToken = default);

    /// <summary>Whether the anime is a favourite (false when unknown).</summary>
    IAsyncEnumerable<bool> ObserveIsFavorite(string providerId, string animeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// In one transaction: inserts the anime row if missing, then sets the favourite flag (favouritedAt = now when true, null
    /// when false) and refreshes title / cover / synopsis (COALESCE for the latter two).
    /// </summary>
    Task SetFavoriteAsync(Anime anime, bool favorite, CancellationToken cancellationToken = default);
}
