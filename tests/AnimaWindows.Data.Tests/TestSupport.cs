using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Data;
using Microsoft.Data.Sqlite;

namespace AnimaWindows.Data.Tests;

/// <summary>Deterministic clock; with <see cref="AutoAdvance"/> every read moves time forward by one millisecond.</summary>
internal sealed class FakeClock : IClock
{
    private long _ms;

    public FakeClock(long startMs = 1_000_000) => _ms = startMs;

    public bool AutoAdvance { get; set; } = true;

    public DateTimeOffset UtcNow
    {
        get
        {
            var v = _ms;
            if (AutoAdvance)
            {
                _ms++;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(v);
        }
    }

    public void Advance(long ms) => _ms += ms;

    public void Set(long ms) => _ms = ms;
}

internal sealed class FakePaths(string dir) : IAppPaths
{
    public string DataDirectory { get; } = dir;

    public string CacheDirectory { get; } = Path.Combine(dir, "cache");

    public string DownloadsDirectory { get; } = Path.Combine(dir, "downloads");
}

/// <summary>A temp directory with a fresh database, clock and repositories. Deleted on dispose.</summary>
public abstract class DataTestBase : IDisposable
{
    private readonly List<IDisposable> _extra = [];

    protected DataTestBase()
    {
        Dir = Path.Combine(Path.GetTempPath(), "animawindows-data-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Dir);
        Clock = new FakeClock();
        Db = NewDb("main.db");
        Library = new SqliteLibraryRepository(Db, Clock);
        Downloads = new SqliteDownloadRepository(Db);
        Backup = new BackupService(Db, Clock);
    }

    internal string Dir { get; }

    internal FakeClock Clock { get; }

    internal AnimaDatabase Db { get; }

    internal SqliteLibraryRepository Library { get; }

    internal SqliteDownloadRepository Downloads { get; }

    internal BackupService Backup { get; }

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    internal AnimaDatabase NewDb(string name)
    {
        var db = new AnimaDatabase(Path.Combine(Dir, name));
        _extra.Add(db);
        return db;
    }

    protected static Anime MakeAnime(string id, string title = "T", string provider = "hianime", string? cover = null) =>
        new() { Id = id, ProviderId = provider, Title = title, CoverUrl = cover };

    /// <summary>Takes <paramref name="count"/> emissions, running <paramref name="afterEach"/> (by index) after each one.</summary>
    protected static async Task<List<T>> TakeAsync<T>(IAsyncEnumerable<T> source, int count, Func<int, Task>? afterEach = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cts.Token))
        {
            list.Add(item);
            if (list.Count >= count)
            {
                break;
            }

            if (afterEach is not null)
            {
                await afterEach(list.Count - 1);
            }
        }

        return list;
    }

    public void Dispose()
    {
        foreach (var d in _extra)
        {
            d.Dispose();
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
