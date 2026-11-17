using System.Collections.Concurrent;
using System.Net;
using System.Text;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Tests.Tracking;

namespace AnimaWindows.Core.Tests.Downloads;

internal sealed record CdnRequest(string Path, string Url, string? Range, IReadOnlyDictionary<string, string> Headers);

/// <summary>In-memory CDN: serves <see cref="Files"/> by path with Range support; hooks to block or fail requests.</summary>
internal sealed class FakeCdn : HttpMessageHandler
{
    public ConcurrentDictionary<string, byte[]> Files { get; } = new();

    public ConcurrentQueue<CdnRequest> Requests { get; } = new();

    /// <summary>Awaited before answering (cancellation-aware); use to block a request.</summary>
    public Func<CdnRequest, Task>? Before { get; set; }

    /// <summary>Non-null result = answer with that status instead of the file.</summary>
    public Func<CdnRequest, HttpStatusCode?>? Fault { get; set; }

    /// <summary>Answer ranged requests with the full body and 200 (misbehaving server).</summary>
    public bool IgnoreRange { get; set; }

    public void Put(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

    public int Count(Func<CdnRequest, bool> predicate) => Requests.Count(predicate);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var range = request.Headers.Range?.Ranges.FirstOrDefault();
        var record = new CdnRequest(
            request.RequestUri!.AbsolutePath,
            request.RequestUri.AbsoluteUri,
            range is null ? null : $"bytes={range.From}-{range.To}",
            request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value)));
        Requests.Enqueue(record);
        if (Before is not null)
        {
            await Before(record).WaitAsync(cancellationToken);
        }

        if (Fault?.Invoke(record) is { } status)
        {
            return new HttpResponseMessage(status);
        }

        if (!Files.TryGetValue(record.Path, out var data))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (range is not null && !IgnoreRange)
        {
            var from = (int)(range.From ?? 0);
            if (from >= data.Length)
            {
                return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            }

            var to = (int)Math.Min(range.To ?? data.Length - 1, data.Length - 1);
            return new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[from..(to + 1)]) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    }
}

internal sealed class InMemoryDownloadRepository : IDownloadRepository
{
    private readonly object _lock = new();
    private readonly Dictionary<string, DownloadItem> _rows = new();

    public ConcurrentQueue<(string Key, DownloadState State, string? Reason)> Log { get; } = new();

    public IAsyncEnumerable<IReadOnlyList<DownloadItem>> ObserveDownloads(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<IReadOnlyList<DownloadGroup>> ObserveGroupedByAnime(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<IReadOnlyDictionary<string, DownloadItem>> ObserveForAnime(
        string providerId, string animeId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<DownloadItem?> Observe(
        string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DownloadItem?> GetAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.TryGetValue(KeyOf(providerId, animeId, episodeNumber), out var item) ? item : null);
        }
    }

    public Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DownloadItem>>(_rows.Values.OrderByDescending(i => i.CreatedAt).ToList());
        }
    }

    public Task UpsertAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _rows[item.Key] = item;
        }

        Log.Enqueue((item.Key, item.State, item.FailureReason));
        return Task.CompletedTask;
    }

    public Task UpdateProgressAsync(
        string providerId, string animeId, string episodeNumber, DownloadState state, long bytes, float percent,
        string? failureReason = null, CancellationToken cancellationToken = default)
    {
        var key = KeyOf(providerId, animeId, episodeNumber);
        lock (_lock)
        {
            if (!_rows.TryGetValue(key, out var item))
            {
                return Task.CompletedTask;
            }

            _rows[key] = item with { State = state, Bytes = bytes, Percent = percent, FailureReason = failureReason };
        }

        Log.Enqueue((key, state, failureReason));
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _rows.Remove(KeyOf(providerId, animeId, episodeNumber));
        }

        return Task.CompletedTask;
    }

    private static string KeyOf(string providerId, string animeId, string episodeNumber) => $"{providerId}/{animeId}/{episodeNumber}";
}

internal sealed class FakeSettings : ISettingsStore
{
    public AppSettings Current { get; private set; } = AppSettings.Fresh;

    public event EventHandler<AppSettings>? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Func<AppSettings, AppSettings> transform, CancellationToken cancellationToken = default)
    {
        var next = transform(Current);
        if (!ReferenceEquals(next, Current))
        {
            Current = next;
            Changed?.Invoke(this, next);
        }

        return Task.CompletedTask;
    }

    public Task<string> ExportJsonAsync(CancellationToken cancellationToken = default) => Task.FromResult("{}");

    public Task ImportJsonAsync(string json, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeChain : IProviderChain
{
    private int _calls;

    public Func<Anime, Episode, TranslationMode, StreamSet> Resolve { get; set; } =
        (_, _, _) => throw new ProviderException("no resolver configured");

    public int Calls => Volatile.Read(ref _calls);

    public IReadOnlyList<string> Ids => [];

    public IAnimeProvider? Provider(string id) => null;

    public string EffectiveLanguage(AppSettings settings) => "en";

    public IReadOnlyList<IAnimeProvider> ChainFor(AppSettings settings) => [];

    public Task<IReadOnlyList<IAnimeProvider>> ChainAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<IAnimeProvider>>([]);

    public Task<IReadOnlyList<Anime>> SearchAsync(string query, TranslationMode mode, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Anime>>([]);

    public Task<ResolvedStreams> StreamsAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _calls);
        var set = Resolve(anime, episode, mode);
        return Task.FromResult(new ResolvedStreams(set, anime.ProviderId, false, anime, episode));
    }
}

internal sealed class FakePaths : IAppPaths
{
    public FakePaths(string root)
    {
        DataDirectory = Path.Combine(root, "data");
        CacheDirectory = Path.Combine(root, "cache");
        DownloadsDirectory = Path.Combine(root, "downloads");
    }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string DownloadsDirectory { get; }
}

/// <summary>Content helpers shared by the download tests.</summary>
internal static class Media
{
    public static byte[] Bytes(int seed, int length)
    {
        var data = new byte[length];
        for (var i = 0; i < length; i++)
        {
            data[i] = (byte)((seed * 31) + i);
        }

        return data;
    }

    public static string Playlist(int segments, string? key = null, string? map = null)
    {
        var sb = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:0\n");
        if (key is not null)
        {
            sb.Append($"#EXT-X-KEY:METHOD=AES-128,URI=\"{key}\",IV=0x00000000000000000000000000000001\n");
        }

        if (map is not null)
        {
            sb.Append($"#EXT-X-MAP:URI=\"{map}\"\n");
        }

        for (var i = 0; i < segments; i++)
        {
            sb.Append($"#EXTINF:4.0,\nseg{i}.ts\n");
        }

        sb.Append("#EXT-X-ENDLIST\n");
        return sb.ToString();
    }

    /// <summary>Adds <c>/{name}/{height}/index.m3u8</c> and its segments; returns the playlist URL.</summary>
    public static string AddRendition(FakeCdn cdn, string name, int height, int segments, int segmentSize = 100)
    {
        var dir = $"/{name}/{height}";
        cdn.Put($"{dir}/index.m3u8", Playlist(segments));
        for (var i = 0; i < segments; i++)
        {
            cdn.Files[$"{dir}/seg{i}.ts"] = Bytes(height + i, segmentSize);
        }

        return $"https://cdn.test{dir}/index.m3u8";
    }

    public static StreamSet SetFor(FakeCdn cdn, string name, int segments)
    {
        return new StreamSet
        {
            Variants =
            [
                new StreamVariant(1080, AddRendition(cdn, name, 1080, segments)),
                new StreamVariant(720, AddRendition(cdn, name, 720, segments)),
                new StreamVariant(360, AddRendition(cdn, name, 360, segments)),
            ],
            Headers = new Dictionary<string, string> { ["Referer"] = "https://site.test/" },
        };
    }

    public static Anime Show(string id = "show") => new() { Id = id, ProviderId = "prov", Title = "Show " + id };

    public static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    public static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(10));

    public static async Task WaitUntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }
}
