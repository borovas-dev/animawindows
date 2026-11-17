using System.Globalization;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data;

/// <summary>SQLite implementation of <see cref="IDownloadRepository"/>; ported from <c>RoomDownloadRepository</c>/<c>DownloadDao</c>.</summary>
public sealed class SqliteDownloadRepository(AnimaDatabase db) : IDownloadRepository
{
    private static readonly string[] Tables = [AnimaSql.DownloadTable];

    private static readonly string Select = "SELECT " + DownloadRow.Columns + " FROM download";

    public IAsyncEnumerable<IReadOnlyList<DownloadItem>> ObserveDownloads(CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            Tables,
            c => c.Query(null, Select + " ORDER BY createdAt DESC", DownloadRow.Read),
            rows => (IReadOnlyList<DownloadItem>)rows.Select(r => r.ToModel()).ToList(),
            ListComparer<DownloadRow>.Instance,
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyList<DownloadGroup>> ObserveGroupedByAnime(CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            Tables,
            c => c.Query(null, Select + " ORDER BY createdAt DESC", DownloadRow.Read),
            rows => GroupDownloads(rows.Select(r => r.ToModel()).ToList()),
            ListComparer<DownloadRow>.Instance,
            cancellationToken);

    public IAsyncEnumerable<IReadOnlyDictionary<string, DownloadItem>> ObserveForAnime(
        string providerId, string animeId, CancellationToken cancellationToken = default) =>
        db.ObserveAsync(
            Tables,
            c => c.Query(null, Select + " WHERE providerId = $p AND animeId = $a", DownloadRow.Read, ("$p", providerId), ("$a", animeId)),
            rows =>
            {
                var map = new Dictionary<string, DownloadItem>();
                foreach (var row in rows)
                {
                    map[row.EpisodeNumber] = row.ToModel();
                }

                return (IReadOnlyDictionary<string, DownloadItem>)map;
            },
            ListComparer<DownloadRow>.Instance,
            cancellationToken);

    public IAsyncEnumerable<DownloadItem?> Observe(
        string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default) =>
        db.ObserveAsync<DownloadRow?, DownloadItem?>(
            Tables,
            c => QueryOne(c, providerId, animeId, episodeNumber),
            row => row?.ToModel(),
            null,
            cancellationToken);

    public async Task<DownloadItem?> GetAsync(
        string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        var row = await db.ReadAsync(c => QueryOne(c, providerId, animeId, episodeNumber), cancellationToken).ConfigureAwait(false);
        return row?.ToModel();
    }

    public async Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.ReadAsync(c => c.Query(null, Select, DownloadRow.Read), cancellationToken).ConfigureAwait(false);
        return rows.Select(r => r.ToModel()).ToList();
    }

    public Task UpsertAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        var row = DownloadRow.From(item);
        return db.ExecuteWriteAsync(
            (c, t) => c.Exec(
                t,
                "INSERT INTO download (" + DownloadRow.Columns + ") VALUES ($providerId, $animeId, $episodeNumber, $episodeId, $title, " +
                "$coverUrl, $mode, $quality, $state, $bytes, $percent, $createdAt, $streamSetJson, $failureReason) " +
                "ON CONFLICT(providerId, animeId, episodeNumber) DO UPDATE SET episodeId = excluded.episodeId, title = excluded.title, " +
                "coverUrl = excluded.coverUrl, mode = excluded.mode, quality = excluded.quality, state = excluded.state, " +
                "bytes = excluded.bytes, percent = excluded.percent, createdAt = excluded.createdAt, " +
                "streamSetJson = excluded.streamSetJson, failureReason = excluded.failureReason",
                row.Params()),
            Tables,
            cancellationToken);
    }

    public Task UpdateProgressAsync(
        string providerId, string animeId, string episodeNumber, DownloadState state, long bytes, float percent,
        string? failureReason = null, CancellationToken cancellationToken = default) =>
        db.ExecuteWriteAsync(
            (c, t) => c.Exec(
                t,
                "UPDATE download SET state = $state, bytes = $bytes, percent = $percent, failureReason = $failureReason " +
                "WHERE providerId = $p AND animeId = $a AND episodeNumber = $e",
                ("$state", EnumNames.StateName(state)), ("$bytes", bytes), ("$percent", (double)percent),
                ("$failureReason", failureReason), ("$p", providerId), ("$a", animeId), ("$e", episodeNumber)),
            Tables,
            cancellationToken);

    public Task DeleteAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default) =>
        db.ExecuteWriteAsync(
            (c, t) => c.Exec(
                t,
                "DELETE FROM download WHERE providerId = $p AND animeId = $a AND episodeNumber = $e",
                ("$p", providerId), ("$a", animeId), ("$e", episodeNumber)),
            Tables,
            cancellationToken);

    /// <summary>
    /// Groups by anime (input is expected newest first); episodes sorted by numeric episode number (non-numeric last, then
    /// ordinal); groups sorted by their newest download, newest first. Kotlin <c>groupDownloads</c>.
    /// </summary>
    internal static IReadOnlyList<DownloadGroup> GroupDownloads(IReadOnlyList<DownloadItem> items) =>
        items
            .GroupBy(i => (i.ProviderId, i.AnimeId))
            .Select(g =>
            {
                var first = g.First();
                var sorted = g
                    .OrderBy(i => double.TryParse(i.EpisodeNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.MaxValue)
                    .ThenBy(i => i.EpisodeNumber, StringComparer.Ordinal)
                    .ToList();
                return new DownloadGroup(first.ProviderId, first.AnimeId, first.Title, g.Select(i => i.CoverUrl).FirstOrDefault(u => u is not null), sorted);
            })
            .OrderByDescending(g => g.Items.Max(i => i.CreatedAt))
            .ToList();

    private static DownloadRow? QueryOne(Microsoft.Data.Sqlite.SqliteConnection c, string providerId, string animeId, string episodeNumber) =>
        c.Query(
            null,
            Select + " WHERE providerId = $p AND animeId = $a AND episodeNumber = $e",
            DownloadRow.Read,
            ("$p", providerId), ("$a", animeId), ("$e", episodeNumber)).FirstOrDefault();
}
