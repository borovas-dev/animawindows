using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data;

/// <summary>
/// SQLite implementation of <see cref="ILibraryRepository"/>; query semantics ported from the Room DAOs and
/// <c>RoomLibraryRepository</c> in core/database.
/// </summary>
public sealed class SqliteLibraryRepository(AnimaDatabase db, IClock clock) : ILibraryRepository
{
    private static readonly string[] SearchTables = [AnimaSql.SearchTable];
    private static readonly string[] AnimeTables = [AnimaSql.AnimeTable];
    private static readonly string[] ProgressTables = [AnimaSql.ProgressTable];
    private static readonly string[] AnimeAndProgress = [AnimaSql.AnimeTable, AnimaSql.ProgressTable];

    public IAsyncEnumerable<IReadOnlyList<string>> ObserveRecentSearches(int limit = 20, CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            SearchTables,
            c => c.Query(null, "SELECT query FROM search_history ORDER BY searchedAt DESC LIMIT $limit", r => r.GetString(0), ("$limit", limit)),
            l => (IReadOnlyList<string>)l,
            ListComparer<string>.Instance,
            cancellationToken);

    public Task AddSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var q = ILibraryRepository.NormalizeQuery(query);
        if (q.Length == 0)
        {
            return Task.CompletedTask;
        }

        var now = clock.UnixMilliseconds;
        return db.ExecuteWriteAsync(
            (c, t) =>
            {
                AnimaSql.UpsertSearch(c, t, q, now);
                AnimaSql.TrimSearches(c, t, ILibraryRepository.MaxSearches);
            },
            SearchTables,
            cancellationToken);
    }

    public Task RemoveSearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var q = ILibraryRepository.NormalizeQuery(query);
        return db.ExecuteWriteAsync((c, t) => c.Exec(t, "DELETE FROM search_history WHERE query = $q", ("$q", q)), SearchTables, cancellationToken);
    }

    public Task ClearSearchesAsync(CancellationToken cancellationToken = default) =>
        db.ExecuteWriteAsync((c, t) => c.Exec(t, "DELETE FROM search_history"), SearchTables, cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<WatchEntry>> ObserveContinueWatching(CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            AnimeAndProgress,
            QueryContinueWatching,
            rows => (IReadOnlyList<WatchEntry>)rows.Select(x => new WatchEntry(x.Anime.ToModel(), x.Last.ToModel())).ToList(),
            ListComparer<(AnimeRow Anime, ProgressRow Last)>.Instance,
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<EpisodeProgress>> ObserveProgressForAnime(
        string providerId, string animeId, CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            ProgressTables,
            c => c.Query(
                null,
                "SELECT " + ProgressRow.Columns + " FROM episode_progress WHERE providerId = $p AND animeId = $a",
                r => ProgressRow.Read(r),
                ("$p", providerId), ("$a", animeId)),
            rows => (IReadOnlyList<EpisodeProgress>)rows.Select(r => r.ToModel()).ToList(),
            ListComparer<ProgressRow>.Instance,
            cancellationToken);

    public async Task<EpisodeProgress?> GetProgressAsync(
        string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        var row = await db.ReadAsync(c => AnimaSql.GetProgress(c, null, providerId, animeId, episodeNumber), cancellationToken).ConfigureAwait(false);
        return row?.ToModel();
    }

    public Task SaveProgressAsync(
        Anime anime, string episodeNumber, long positionMs, long durationMs, CancellationToken cancellationToken = default)
    {
        var now = clock.UnixMilliseconds;
        var a = AnimeRow.From(anime);
        return db.ExecuteWriteAsync(
            (c, t) =>
            {
                AnimaSql.TouchWatched(c, t, a, now);
                AnimaSql.UpsertProgress(c, t, new ProgressRow(anime.ProviderId, anime.Id, episodeNumber, positionMs, durationMs, now));
            },
            AnimeAndProgress,
            cancellationToken);
    }

    public Task RemoveFromHistoryAsync(string providerId, string animeId, CancellationToken cancellationToken = default) =>
        db.ExecuteWriteAsync(
            (c, t) =>
            {
                c.Exec(t, "DELETE FROM episode_progress WHERE providerId = $p AND animeId = $a", ("$p", providerId), ("$a", animeId));
                c.Exec(t, "UPDATE anime SET lastWatchedAt = NULL WHERE providerId = $p AND animeId = $a", ("$p", providerId), ("$a", animeId));
            },
            AnimeAndProgress,
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<Anime>> ObserveFavorites(CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            AnimeTables,
            c => c.Query(
                null,
                "SELECT " + AnimeRow.Columns + " FROM anime WHERE isFavorite = 1 ORDER BY favoritedAt DESC",
                r => AnimeRow.Read(r)),
            rows => (IReadOnlyList<Anime>)rows.Select(r => r.ToModel()).ToList(),
            ListComparer<AnimeRow>.Instance,
            cancellationToken);

    public IAsyncEnumerable<bool> ObserveIsFavorite(string providerId, string animeId, CancellationToken cancellationToken = default) =>
        db.ObserveAsync<bool, bool>(
            AnimeTables,
            c => c.Query(
                null,
                "SELECT isFavorite FROM anime WHERE providerId = $p AND animeId = $a",
                r => r.GetInt64(0) != 0,
                ("$p", providerId), ("$a", animeId)).FirstOrDefault(),
            b => b,
            null,
            cancellationToken);

    public Task SetFavoriteAsync(Anime anime, bool favorite, CancellationToken cancellationToken = default)
    {
        var a = AnimeRow.From(anime);
        var now = clock.UnixMilliseconds;
        return db.ExecuteWriteAsync(
            (c, t) =>
            {
                AnimaSql.InsertIgnoreAnime(c, t, a);
                c.Exec(
                    t,
                    "UPDATE anime SET isFavorite = $favorite, favoritedAt = $at, title = $title, coverUrl = COALESCE($coverUrl, coverUrl), " +
                    "synopsis = COALESCE($synopsis, synopsis) WHERE providerId = $p AND animeId = $a",
                    ("$favorite", favorite ? 1L : 0L), ("$at", favorite ? (long?)now : null), ("$title", a.Title),
                    ("$coverUrl", a.CoverUrl), ("$synopsis", a.Synopsis), ("$p", a.ProviderId), ("$a", a.AnimeId));
            },
            AnimeTables,
            cancellationToken);
    }

    /// <summary>
    /// Watched anime (lastWatchedAt not null, newest first) joined with the most recently updated progress row of each
    /// (Kotlin <c>continueWatching</c> = <c>watched()</c> x <c>latestPerAnime()</c>; one row per anime, first wins on ties).
    /// </summary>
    private static List<(AnimeRow Anime, ProgressRow Last)> QueryContinueWatching(Microsoft.Data.Sqlite.SqliteConnection c)
    {
        var rows = c.Query(
            null,
            "SELECT " + AnimeRow.ColumnsFor("a") + ", " + ProgressRow.ColumnsFor("p") + " FROM anime a " +
            "JOIN episode_progress p ON p.providerId = a.providerId AND p.animeId = a.animeId " +
            "WHERE a.lastWatchedAt IS NOT NULL AND p.updatedAt = (SELECT MAX(q.updatedAt) FROM episode_progress q " +
            "WHERE q.providerId = p.providerId AND q.animeId = p.animeId) " +
            "ORDER BY a.lastWatchedAt DESC, a.providerId, a.animeId, p.episodeNumber",
            r => (Anime: AnimeRow.Read(r), Last: ProgressRow.Read(r, AnimeRow.ColumnCount)));

        var seen = new HashSet<(string, string)>();
        return rows.Where(x => seen.Add((x.Anime.ProviderId, x.Anime.AnimeId))).ToList();
    }
}
