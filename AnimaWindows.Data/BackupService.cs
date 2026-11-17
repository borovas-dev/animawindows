using System.Text;
using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data;

/// <summary>
/// <see cref="IBackupService"/>: backup JSON v1 compatible with animadroid's <c>BackupManager</c> (same field names), plus the
/// ani-cli <c>ani-hsts</c> import.
/// </summary>
public sealed class BackupService(AnimaDatabase db, IClock clock) : IBackupService
{
    /// <summary>Provider id given to shows imported from ani-cli history.</summary>
    public const string AniCliProvider = "hianime";

    private static readonly string[] AllTables = [AnimaSql.AnimeTable, AnimaSql.ProgressTable, AnimaSql.SearchTable];

    public async Task ExportAsync(Stream output, string? settingsJson = null, CancellationToken cancellationToken = default)
    {
        var file = await db.ReadAsync(
            c =>
            {
                using var tx = c.BeginTransaction();
                var anime = c.Query(tx, "SELECT " + AnimeRow.Columns + " FROM anime", r => AnimeRow.Read(r))
                    .Select(a => new BackupAnime
                    {
                        ProviderId = a.ProviderId,
                        AnimeId = a.AnimeId,
                        Title = a.Title,
                        CoverUrl = a.CoverUrl,
                        Synopsis = a.Synopsis,
                        MalId = a.MalId,
                        SubEpisodes = a.SubEpisodes,
                        DubEpisodes = a.DubEpisodes,
                        IsFavorite = a.IsFavorite,
                        FavoritedAt = a.FavoritedAt,
                        LastWatchedAt = a.LastWatchedAt,
                        Alternates = AlternatesCodec.Decode(a.AlternatesJson),
                    })
                    .ToList();
                var progress = c.Query(tx, "SELECT " + ProgressRow.Columns + " FROM episode_progress", r => ProgressRow.Read(r))
                    .Select(p => new BackupProgress(p.ProviderId, p.AnimeId, p.EpisodeNumber, p.PositionMs, p.DurationMs, p.UpdatedAt))
                    .ToList();
                var searches = c.Query(tx, "SELECT query, searchedAt FROM search_history", r => new BackupSearch(r.GetString(0), r.GetInt64(1)));
                return new BackupFile { Anime = anime, Progress = progress, Searches = searches, Settings = settingsJson };
            },
            cancellationToken).ConfigureAwait(false);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(file, DataJsonContext.Default.BackupFile);
        await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ImportResult> ImportAsync(Stream input, CancellationToken cancellationToken = default)
    {
        string text;
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var file = JsonSerializer.Deserialize(text, DataJsonContext.Default.BackupFile)
            ?? throw new JsonException("Backup must be a JSON object.");
        if (file.Version != 1)
        {
            throw new JsonException($"Unsupported backup version {file.Version}");
        }

        return await db.WriteAsync(
            (c, t) =>
            {
                foreach (var b in file.Anime)
                {
                    MergeAnime(c, t, b);
                }

                foreach (var p in file.Progress)
                {
                    var existing = AnimaSql.GetProgress(c, t, p.ProviderId, p.AnimeId, p.EpisodeNumber);
                    if (existing is null || p.UpdatedAt > existing.UpdatedAt)
                    {
                        AnimaSql.UpsertProgress(
                            c, t, new ProgressRow(p.ProviderId, p.AnimeId, p.EpisodeNumber, p.PositionMs, p.DurationMs, p.UpdatedAt));
                    }
                }

                var have = c.Query(t, "SELECT query, searchedAt FROM search_history", r => (Query: r.GetString(0), At: r.GetInt64(1)))
                    .ToDictionary(x => x.Query, x => x.At);
                foreach (var s in file.Searches)
                {
                    var q = ILibraryRepository.NormalizeQuery(s.Query);
                    if (q.Length > 0 && s.SearchedAt > have.GetValueOrDefault(q, -1))
                    {
                        AnimaSql.UpsertSearch(c, t, q, s.SearchedAt);
                    }
                }

                AnimaSql.TrimSearches(c, t, ILibraryRepository.MaxSearches);
                return new ImportResult(file.Anime.Count, file.Progress.Count, file.Searches.Count, file.Settings);
            },
            AllTables,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports an ani-cli <c>ani-hsts</c> file: lines of <c>ep_no TAB anime_id TAB title</c>. Each becomes a hianime anime with a
    /// finished progress row (positionMs = durationMs = 1) for that episode; existing progress rows are kept. Returns the number of
    /// lines imported.
    /// </summary>
    public async Task<int> ImportAniCliHistoryAsync(Stream input, long? now = null, CancellationToken cancellationToken = default)
    {
        var at = now ?? clock.UnixMilliseconds;
        string text;
        using (var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var lines = text.Split('\n');
        return await db.WriteAsync(
            (c, t) =>
            {
                var count = 0;
                foreach (var raw in lines)
                {
                    var parts = raw.TrimEnd('\r').Split('\t', 3);
                    if (parts.Length < 3)
                    {
                        continue;
                    }

                    var ep = parts[0].Trim();
                    var id = parts[1].Trim();
                    var title = parts[2].Trim();
                    if (ep.Length == 0 || id.Length == 0)
                    {
                        continue;
                    }

                    AnimaSql.TouchWatched(c, t, new AnimeRow(AniCliProvider, id, title.Length == 0 ? id : title, LastWatchedAt: at), at);
                    AnimaSql.InsertIgnoreProgress(c, t, new ProgressRow(AniCliProvider, id, ep, 1, 1, at));
                    count++;
                }

                return count;
            },
            AllTables,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Newer timestamps win; alternates are unioned (existing wins on key clash); see Kotlin <c>BackupManager.importJson</c>.</summary>
    private static void MergeAnime(Microsoft.Data.Sqlite.SqliteConnection c, Microsoft.Data.Sqlite.SqliteTransaction t, BackupAnime b)
    {
        var incoming = new AnimeRow(
            b.ProviderId, b.AnimeId, b.Title, b.CoverUrl, b.Synopsis, b.MalId, b.SubEpisodes, b.DubEpisodes, b.IsFavorite,
            b.FavoritedAt, b.LastWatchedAt, AlternatesCodec.Encode(b.Alternates));
        var existing = AnimaSql.GetAnime(c, t, b.ProviderId, b.AnimeId);
        if (existing is null)
        {
            AnimaSql.UpsertAnime(c, t, incoming);
            return;
        }

        var fav = (incoming.FavoritedAt ?? -1) > (existing.FavoritedAt ?? -1) ? incoming : existing;
        var baseRow = (incoming.LastWatchedAt ?? -1) > (existing.LastWatchedAt ?? -1) ? incoming : existing;

        var alternates = new Dictionary<string, string>(AlternatesCodec.Decode(incoming.AlternatesJson));
        foreach (var (k, v) in AlternatesCodec.Decode(existing.AlternatesJson))
        {
            alternates[k] = v;
        }

        AnimaSql.UpsertAnime(
            c,
            t,
            baseRow with
            {
                CoverUrl = baseRow.CoverUrl ?? existing.CoverUrl ?? incoming.CoverUrl,
                Synopsis = baseRow.Synopsis ?? existing.Synopsis ?? incoming.Synopsis,
                MalId = baseRow.MalId ?? existing.MalId ?? incoming.MalId,
                AlternatesJson = AlternatesCodec.Encode(alternates),
                IsFavorite = fav.IsFavorite,
                FavoritedAt = fav.FavoritedAt,
                LastWatchedAt = MaxOfNullable(existing.LastWatchedAt, incoming.LastWatchedAt),
            });
    }

    private static long? MaxOfNullable(long? a, long? b) => a is null ? b : b is null ? a : Math.Max(a.Value, b.Value);
}
