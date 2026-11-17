using Microsoft.Data.Sqlite;

namespace AnimaWindows.Data.Tests;

public class DatabaseTests : DataTestBase
{
    [Fact]
    public void NewDatabase_HasCurrentVersionAndRoomV4Tables()
    {
        Assert.Equal(AnimaDatabase.CurrentVersion, Db.GetSchemaVersion());
        var tables = Db.Read(c => c.Query(null, "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", r => r.GetString(0)));
        Assert.Equal(new[] { "anime", "download", "episode_progress", "search_history" }, tables);

        var animeColumns = Db.Read(c => c.Query(null, "SELECT name FROM pragma_table_info('anime')", r => r.GetString(0)));
        Assert.Equal(new[] { "providerId", "animeId", "title", "coverUrl", "synopsis", "malId", "subEpisodes", "dubEpisodes", "isFavorite", "favoritedAt", "lastWatchedAt", "alternatesJson" },
            animeColumns);
    }

    [Fact]
    public void Database_UsesWal()
    {
        var mode = Db.Read(c =>
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode";
            return (string?)cmd.ExecuteScalar();
        });
        Assert.Equal("wal", mode);
    }

    [Fact]
    public void Reopen_KeepsDataAndVersion()
    {
        var path = Path.Combine(Dir, "reopen.db");
        using (var first = new AnimaDatabase(path))
        {
            first.Read(c => c.Exec(null, "INSERT INTO search_history (query, searchedAt) VALUES ('x', 1)"));
        }

        using var second = new AnimaDatabase(path);
        Assert.Equal(1, second.GetSchemaVersion());
        Assert.Equal(new[] { "x" }, second.Read(c => c.Query(null, "SELECT query FROM search_history", r => r.GetString(0))));
    }

    [Fact]
    public void NewerDatabase_IsRejected()
    {
        var path = Path.Combine(Dir, "future.db");
        using (var conn = new SqliteConnection($"Data Source={path}"))
        {
            conn.Open();
            conn.Exec(null, "PRAGMA user_version = 99");
        }

        SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(() => new AnimaDatabase(path));
    }

    [Fact]
    public void AppPathsConstructor_PlacesFileInDataDirectory()
    {
        var dir = Path.Combine(Dir, "appdata");
        using var db = new AnimaDatabase(new FakePaths(dir));
        Assert.True(File.Exists(Path.Combine(dir, "animawindows.db")));
    }
}
