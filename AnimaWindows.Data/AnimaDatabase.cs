using System.Runtime.CompilerServices;
using AnimaWindows.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace AnimaWindows.Data;

/// <summary>
/// The app's SQLite database (one file, WAL). Schema is identical to the Room v4 tables of the Android app
/// (anime, episode_progress, search_history, download); the version is tracked in <c>PRAGMA user_version</c>
/// (this app starts at 1). Writes are serialised through a semaphore; reads use their own pooled connections.
/// Writers publish table names on an internal <see cref="ChangeBus"/> after commit, which drives the Observe* streams.
/// </summary>
public sealed class AnimaDatabase : IDisposable
{
    /// <summary>Current schema version (<c>PRAGMA user_version</c>).</summary>
    public const int CurrentVersion = 1;

    public const string FileName = "animawindows.db";

    // Each entry upgrades the database to Version. Version 1 creates the schema equal to Room v4 (see
    // src/animadroid/core/database/schemas/.../4.json). Add later steps as (2, [...]) etc.
    private static readonly (int Version, string[] Statements)[] Migrations =
    [
        (1,
        [
            "CREATE TABLE IF NOT EXISTS `anime` (`providerId` TEXT NOT NULL, `animeId` TEXT NOT NULL, `title` TEXT NOT NULL, " +
            "`coverUrl` TEXT, `synopsis` TEXT, `malId` INTEGER, `subEpisodes` INTEGER, `dubEpisodes` INTEGER, " +
            "`isFavorite` INTEGER NOT NULL, `favoritedAt` INTEGER, `lastWatchedAt` INTEGER, `alternatesJson` TEXT, " +
            "PRIMARY KEY(`providerId`, `animeId`))",
            "CREATE TABLE IF NOT EXISTS `episode_progress` (`providerId` TEXT NOT NULL, `animeId` TEXT NOT NULL, " +
            "`episodeNumber` TEXT NOT NULL, `positionMs` INTEGER NOT NULL, `durationMs` INTEGER NOT NULL, " +
            "`updatedAt` INTEGER NOT NULL, PRIMARY KEY(`providerId`, `animeId`, `episodeNumber`))",
            "CREATE TABLE IF NOT EXISTS `search_history` (`query` TEXT NOT NULL, `searchedAt` INTEGER NOT NULL, PRIMARY KEY(`query`))",
            "CREATE TABLE IF NOT EXISTS `download` (`providerId` TEXT NOT NULL, `animeId` TEXT NOT NULL, " +
            "`episodeNumber` TEXT NOT NULL, `episodeId` TEXT NOT NULL, `title` TEXT NOT NULL, `coverUrl` TEXT, " +
            "`mode` TEXT NOT NULL, `quality` INTEGER, `state` TEXT NOT NULL, `bytes` INTEGER NOT NULL, " +
            "`percent` REAL NOT NULL, `createdAt` INTEGER NOT NULL, `streamSetJson` TEXT, `failureReason` TEXT, " +
            "PRIMARY KEY(`providerId`, `animeId`, `episodeNumber`))",
        ]),
    ];

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Opens (creating and migrating if needed) <c>animawindows.db</c> in <see cref="IAppPaths.DataDirectory"/>.</summary>
    public AnimaDatabase(IAppPaths paths)
        : this(System.IO.Path.Combine(paths.DataDirectory, FileName))
    {
    }

    /// <summary>Opens (creating and migrating if needed) the database at an explicit file path.</summary>
    public AnimaDatabase(string filePath)
    {
        FilePath = filePath;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        Initialize();
    }

    public string FilePath { get; }

    internal ChangeBus Changes { get; } = new();

    /// <summary>Reads <c>PRAGMA user_version</c> (for tests and diagnostics).</summary>
    public int GetSchemaVersion()
    {
        using var conn = Open();
        return ReadUserVersion(conn);
    }

    internal SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    internal T Read<T>(Func<SqliteConnection, T> query)
    {
        using var conn = Open();
        return query(conn);
    }

    internal Task<T> ReadAsync<T>(Func<SqliteConnection, T> query, CancellationToken cancellationToken) =>
        Task.Run(() => Read(query), cancellationToken);

    /// <summary>
    /// Runs <paramref name="work"/> in one transaction on the serialised writer, then publishes <paramref name="tables"/> as changed.
    /// </summary>
    internal async Task<T> WriteAsync<T>(
        Func<SqliteConnection, SqliteTransaction, T> work, string[] tables, CancellationToken cancellationToken)
    {
        T result;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            result = await Task.Run(
                () =>
                {
                    using var conn = Open();
                    using var tx = conn.BeginTransaction();
                    var r = work(conn, tx);
                    tx.Commit();
                    return r;
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        Changes.Publish(tables);
        return result;
    }

    internal Task ExecuteWriteAsync(Action<SqliteConnection, SqliteTransaction> work, string[] tables, CancellationToken cancellationToken) =>
        WriteAsync(
            (c, t) =>
            {
                work(c, t);
                return 0;
            },
            tables,
            cancellationToken);

    /// <summary>
    /// Cold stream: yields the current value of <paramref name="query"/>, then re-queries after each committed change to one of
    /// <paramref name="tables"/>, emitting only values that differ (by <paramref name="comparer"/> on the raw query result).
    /// Ends quietly when <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    internal async IAsyncEnumerable<T> ObserveAsync<TRaw, T>(
        string[] tables,
        Func<SqliteConnection, TRaw> query,
        Func<TRaw, T> map,
        IEqualityComparer<TRaw>? comparer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        comparer ??= EqualityComparer<TRaw>.Default;

        // Subscribe before the first read so a change committed in between is never missed.
        using var sub = Changes.Subscribe(tables);
        var last = default(TRaw)!;
        var hasLast = false;

        while (!cancellationToken.IsCancellationRequested)
        {
            var (ok, raw) = await TryReadAsync(query, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                yield break;
            }

            if (!hasLast || !comparer.Equals(last, raw))
            {
                last = raw;
                hasLast = true;
                yield return map(raw);
            }

            if (!await WaitForChangeAsync(sub.Reader, cancellationToken).ConfigureAwait(false))
            {
                yield break;
            }

            while (sub.Reader.TryRead(out _))
            {
            }
        }
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private async Task<(bool Ok, TRaw Value)> TryReadAsync<TRaw>(Func<SqliteConnection, TRaw> query, CancellationToken cancellationToken)
    {
        try
        {
            return (true, await ReadAsync(query, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (false, default!);
        }
    }

    private static async Task<bool> WaitForChangeAsync(System.Threading.Channels.ChannelReader<bool> reader, CancellationToken cancellationToken)
    {
        try
        {
            return await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Initialize()
    {
        using var conn = Open();
        conn.Exec(null, "PRAGMA journal_mode=WAL");

        var current = ReadUserVersion(conn);
        if (current > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database '{FilePath}' has schema version {current}, newer than the supported version {CurrentVersion}.");
        }

        foreach (var (version, statements) in Migrations)
        {
            if (version <= current)
            {
                continue;
            }

            using var tx = conn.BeginTransaction();
            foreach (var sql in statements)
            {
                conn.Exec(tx, sql);
            }

            conn.Exec(tx, $"PRAGMA user_version = {version}");
            tx.Commit();
        }
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
