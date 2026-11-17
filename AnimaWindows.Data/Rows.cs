using System.Globalization;
using System.Text.Json;
using AnimaWindows.Core.Json;
using AnimaWindows.Core.Models;
using Microsoft.Data.Sqlite;

namespace AnimaWindows.Data;

/// <summary>Row of the <c>anime</c> table (Kotlin <c>AnimeEntity</c>). Primitive members only so record equality is value equality.</summary>
internal sealed record AnimeRow(
    string ProviderId,
    string AnimeId,
    string Title,
    string? CoverUrl = null,
    string? Synopsis = null,
    int? MalId = null,
    int? SubEpisodes = null,
    int? DubEpisodes = null,
    bool IsFavorite = false,
    long? FavoritedAt = null,
    long? LastWatchedAt = null,
    string? AlternatesJson = null)
{
    public const int ColumnCount = 12;

    private static readonly string[] ColumnNames =
    [
        "providerId", "animeId", "title", "coverUrl", "synopsis", "malId", "subEpisodes", "dubEpisodes",
        "isFavorite", "favoritedAt", "lastWatchedAt", "alternatesJson",
    ];

    public static string Columns { get; } = string.Join(", ", ColumnNames);

    public static string ColumnsFor(string alias) => string.Join(", ", ColumnNames.Select(c => alias + "." + c));

    public static AnimeRow Read(SqliteDataReader r, int o = 0) => new(
        r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), r.Str(o + 3), r.Str(o + 4), r.NInt(o + 5), r.NInt(o + 6),
        r.NInt(o + 7), r.GetInt64(o + 8) != 0, r.NLong(o + 9), r.NLong(o + 10), r.Str(o + 11));

    public static AnimeRow From(Anime a) => new(
        a.ProviderId, a.Id, a.Title, a.CoverUrl, a.Synopsis, a.MalId, a.SubEpisodes, a.DubEpisodes,
        AlternatesJson: AlternatesCodec.Encode(a.Alternates));

    public Anime ToModel() => new()
    {
        Id = AnimeId,
        ProviderId = ProviderId,
        Title = Title,
        CoverUrl = CoverUrl,
        Synopsis = Synopsis,
        SubEpisodes = SubEpisodes,
        DubEpisodes = DubEpisodes,
        MalId = MalId,
        Alternates = AlternatesCodec.Decode(AlternatesJson),
    };

    public (string Name, object? Value)[] Params() =>
    [
        ("$providerId", ProviderId), ("$animeId", AnimeId), ("$title", Title), ("$coverUrl", CoverUrl),
        ("$synopsis", Synopsis), ("$malId", MalId), ("$subEpisodes", SubEpisodes), ("$dubEpisodes", DubEpisodes),
        ("$isFavorite", IsFavorite ? 1L : 0L), ("$favoritedAt", FavoritedAt), ("$lastWatchedAt", LastWatchedAt),
        ("$alternatesJson", AlternatesJson),
    ];
}

/// <summary>Row of <c>episode_progress</c> (Kotlin <c>EpisodeProgressEntity</c>).</summary>
internal sealed record ProgressRow(
    string ProviderId, string AnimeId, string EpisodeNumber, long PositionMs, long DurationMs, long UpdatedAt)
{
    public const int ColumnCount = 6;

    public static string Columns => "providerId, animeId, episodeNumber, positionMs, durationMs, updatedAt";

    public static string ColumnsFor(string alias) =>
        string.Join(", ", Columns.Split(", ").Select(c => alias + "." + c));

    public static ProgressRow Read(SqliteDataReader r, int o = 0) => new(
        r.GetString(o), r.GetString(o + 1), r.GetString(o + 2), r.GetInt64(o + 3), r.GetInt64(o + 4), r.GetInt64(o + 5));

    public EpisodeProgress ToModel() => new(ProviderId, AnimeId, EpisodeNumber, PositionMs, DurationMs, UpdatedAt);

    public (string Name, object? Value)[] Params() =>
    [
        ("$providerId", ProviderId), ("$animeId", AnimeId), ("$episodeNumber", EpisodeNumber),
        ("$positionMs", PositionMs), ("$durationMs", DurationMs), ("$updatedAt", UpdatedAt),
    ];
}

/// <summary>Row of <c>download</c> (Kotlin <c>DownloadEntity</c>).</summary>
internal sealed record DownloadRow(
    string ProviderId,
    string AnimeId,
    string EpisodeNumber,
    string EpisodeId,
    string Title,
    string? CoverUrl,
    string Mode,
    int? Quality,
    string State,
    long Bytes,
    float Percent,
    long CreatedAt,
    string? StreamSetJson,
    string? FailureReason)
{
    public static string Columns =>
        "providerId, animeId, episodeNumber, episodeId, title, coverUrl, mode, quality, state, bytes, percent, createdAt, streamSetJson, failureReason";

    public static DownloadRow Read(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.Str(5), r.GetString(6), r.NInt(7),
        r.GetString(8), r.GetInt64(9), (float)r.GetDouble(10), r.GetInt64(11), r.Str(12), r.Str(13));

    public static DownloadRow From(DownloadItem item) => new(
        item.ProviderId, item.AnimeId, item.EpisodeNumber, item.EpisodeId, item.Title, item.CoverUrl,
        EnumNames.ModeName(item.Mode), item.Quality, EnumNames.StateName(item.State), item.Bytes, item.Percent, item.CreatedAt,
        item.StreamSet is null ? null : JsonSerializer.Serialize(item.StreamSet, CoreJsonContext.Default.StreamSet),
        item.FailureReason);

    public DownloadItem ToModel()
    {
        StreamSet? set = null;
        if (StreamSetJson is not null)
        {
            try
            {
                set = JsonSerializer.Deserialize(StreamSetJson, CoreJsonContext.Default.StreamSet);
            }
            catch (JsonException)
            {
                set = null;
            }
        }

        return new DownloadItem(
            ProviderId, AnimeId, EpisodeId, EpisodeNumber, Title, CoverUrl, EnumNames.ParseMode(Mode), Quality,
            EnumNames.ParseState(State), Bytes, Percent, CreatedAt, set, FailureReason);
    }

    public (string Name, object? Value)[] Params() =>
    [
        ("$providerId", ProviderId), ("$animeId", AnimeId), ("$episodeNumber", EpisodeNumber), ("$episodeId", EpisodeId),
        ("$title", Title), ("$coverUrl", CoverUrl), ("$mode", Mode), ("$quality", Quality), ("$state", State),
        ("$bytes", Bytes), ("$percent", (double)Percent), ("$createdAt", CreatedAt), ("$streamSetJson", StreamSetJson),
        ("$failureReason", FailureReason),
    ];
}

/// <summary>Kotlin enum constant names as stored in the database.</summary>
internal static class EnumNames
{
    public static string ModeName(TranslationMode mode) => mode == TranslationMode.Dub ? "DUB" : "SUB";

    public static TranslationMode ParseMode(string s) => s == "DUB" ? TranslationMode.Dub : TranslationMode.Sub;

    public static string StateName(DownloadState state) => state switch
    {
        DownloadState.Queued => "QUEUED",
        DownloadState.Downloading => "DOWNLOADING",
        DownloadState.Paused => "PAUSED",
        DownloadState.Completed => "COMPLETED",
        _ => "FAILED",
    };

    /// <summary>Unknown names map to Failed (Kotlin <c>getOrDefault(FAILED)</c>).</summary>
    public static DownloadState ParseState(string s) => s switch
    {
        "QUEUED" => DownloadState.Queued,
        "DOWNLOADING" => DownloadState.Downloading,
        "PAUSED" => DownloadState.Paused,
        "COMPLETED" => DownloadState.Completed,
        _ => DownloadState.Failed,
    };
}

/// <summary>JSON object {providerId: animeId}; null when empty (Kotlin <c>encodeAlternates</c>/<c>decodeAlternates</c>).</summary>
internal static class AlternatesCodec
{
    public static string? Encode(IReadOnlyDictionary<string, string> map) =>
        map.Count == 0
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, string>(map), CoreJsonContext.Default.DictionaryStringString);

    public static IReadOnlyDictionary<string, string> Decode(string? json)
    {
        if (json is null)
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize(json, CoreJsonContext.Default.DictionaryStringString)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>Shared SQL used by the library repository and the backup service.</summary>
internal static class AnimaSql
{
    public const string AnimeTable = "anime";
    public const string ProgressTable = "episode_progress";
    public const string SearchTable = "search_history";
    public const string DownloadTable = "download";

    public static void InsertIgnoreAnime(SqliteConnection c, SqliteTransaction t, AnimeRow a) =>
        c.Exec(
            t,
            "INSERT OR IGNORE INTO anime (" + AnimeRow.Columns + ") VALUES ($providerId, $animeId, $title, $coverUrl, $synopsis, " +
            "$malId, $subEpisodes, $dubEpisodes, $isFavorite, $favoritedAt, $lastWatchedAt, $alternatesJson)",
            a.Params());

    public static void UpsertAnime(SqliteConnection c, SqliteTransaction t, AnimeRow a) =>
        c.Exec(
            t,
            "INSERT INTO anime (" + AnimeRow.Columns + ") VALUES ($providerId, $animeId, $title, $coverUrl, $synopsis, " +
            "$malId, $subEpisodes, $dubEpisodes, $isFavorite, $favoritedAt, $lastWatchedAt, $alternatesJson) " +
            "ON CONFLICT(providerId, animeId) DO UPDATE SET title = excluded.title, coverUrl = excluded.coverUrl, " +
            "synopsis = excluded.synopsis, malId = excluded.malId, subEpisodes = excluded.subEpisodes, " +
            "dubEpisodes = excluded.dubEpisodes, isFavorite = excluded.isFavorite, favoritedAt = excluded.favoritedAt, " +
            "lastWatchedAt = excluded.lastWatchedAt, alternatesJson = excluded.alternatesJson",
            a.Params());

    public static AnimeRow? GetAnime(SqliteConnection c, SqliteTransaction? t, string providerId, string animeId) =>
        c.Query(
            t,
            "SELECT " + AnimeRow.Columns + " FROM anime WHERE providerId = $p AND animeId = $a",
            r => AnimeRow.Read(r),
            ("$p", providerId), ("$a", animeId)).FirstOrDefault();

    public static void UpsertProgress(SqliteConnection c, SqliteTransaction t, ProgressRow p) =>
        c.Exec(
            t,
            "INSERT INTO episode_progress (" + ProgressRow.Columns + ") VALUES ($providerId, $animeId, $episodeNumber, " +
            "$positionMs, $durationMs, $updatedAt) ON CONFLICT(providerId, animeId, episodeNumber) DO UPDATE SET " +
            "positionMs = excluded.positionMs, durationMs = excluded.durationMs, updatedAt = excluded.updatedAt",
            p.Params());

    public static void InsertIgnoreProgress(SqliteConnection c, SqliteTransaction t, ProgressRow p) =>
        c.Exec(
            t,
            "INSERT OR IGNORE INTO episode_progress (" + ProgressRow.Columns + ") VALUES ($providerId, $animeId, $episodeNumber, " +
            "$positionMs, $durationMs, $updatedAt)",
            p.Params());

    public static ProgressRow? GetProgress(SqliteConnection c, SqliteTransaction? t, string providerId, string animeId, string episode) =>
        c.Query(
            t,
            "SELECT " + ProgressRow.Columns + " FROM episode_progress WHERE providerId = $p AND animeId = $a AND episodeNumber = $e",
            r => ProgressRow.Read(r),
            ("$p", providerId), ("$a", animeId), ("$e", episode)).FirstOrDefault();

    public static void UpsertSearch(SqliteConnection c, SqliteTransaction t, string query, long searchedAt) =>
        c.Exec(
            t,
            "INSERT INTO search_history (query, searchedAt) VALUES ($q, $t) " +
            "ON CONFLICT(query) DO UPDATE SET searchedAt = excluded.searchedAt",
            ("$q", query), ("$t", searchedAt));

    public static void TrimSearches(SqliteConnection c, SqliteTransaction t, int keep) =>
        c.Exec(
            t,
            "DELETE FROM search_history WHERE query NOT IN (SELECT query FROM search_history ORDER BY searchedAt DESC LIMIT $keep)",
            ("$keep", keep));

    /// <summary>Inserts the anime if missing, then refreshes its metadata and last-watched time (Kotlin <c>touchWatched</c>). Call inside a transaction.</summary>
    public static void TouchWatched(SqliteConnection c, SqliteTransaction t, AnimeRow a, long watchedAt)
    {
        InsertIgnoreAnime(c, t, a with { LastWatchedAt = watchedAt });
        c.Exec(
            t,
            "UPDATE anime SET title = $title, coverUrl = COALESCE($coverUrl, coverUrl), synopsis = COALESCE($synopsis, synopsis), " +
            "malId = COALESCE($malId, malId), alternatesJson = COALESCE($alternatesJson, alternatesJson), " +
            "subEpisodes = COALESCE($sub, subEpisodes), dubEpisodes = COALESCE($dub, dubEpisodes), lastWatchedAt = $watchedAt " +
            "WHERE providerId = $providerId AND animeId = $animeId",
            ("$providerId", a.ProviderId), ("$animeId", a.AnimeId), ("$title", a.Title), ("$coverUrl", a.CoverUrl),
            ("$synopsis", a.Synopsis), ("$malId", a.MalId), ("$alternatesJson", a.AlternatesJson), ("$sub", a.SubEpisodes),
            ("$dub", a.DubEpisodes), ("$watchedAt", watchedAt));
    }
}
