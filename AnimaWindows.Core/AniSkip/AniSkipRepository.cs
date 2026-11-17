using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.AniSkip;

/// <summary>
/// AniSkip API client with an in-memory LRU cache (64 entries; negative results are cached too, failures are not).
/// Never throws except for cancellation. Port of <c>AniSkipRepository</c> in feature/aniskip.
/// </summary>
public sealed class AniSkipRepository : ISkipTimesRepository
{
    public const string DefaultBaseUrl = "https://api.aniskip.com";

    private readonly HttpClient _client;
    private readonly string _baseUrl;
    private readonly LruCache<string, SkipTimes?> _cache;

    public AniSkipRepository(HttpClient client, string baseUrl = DefaultBaseUrl, int cacheSize = 64)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
        _cache = new LruCache<string, SkipTimes?>(cacheSize);
    }

    public async Task<SkipTimes?> SkipTimesAsync(int malId, int episode, CancellationToken cancellationToken = default)
    {
        var key = $"{malId}:{episode}";
        if (_cache.TryGet(key, out var cached)) return cached;
        try
        {
            var result = await FetchAsync(malId, episode, cancellationToken).ConfigureAwait(false);
            _cache.Put(key, result);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<SkipTimes?> FetchAsync(int malId, int episode, CancellationToken ct)
    {
        var url = $"{_baseUrl}/v1/skip-times/{malId}/{episode}?types=op&types=ed";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", AniSkipParsing.UserAgent);
        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)resp.StatusCode}");
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return AniSkipParsing.ParseResponse(body);
    }
}

/// <summary>Small thread-safe access-ordered LRU map (Kotlin <c>LinkedHashMap(accessOrder = true)</c>).</summary>
internal sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<KeyValuePair<TKey, TValue>>> _map = new();
    private readonly LinkedList<KeyValuePair<TKey, TValue>> _order = new();
    private readonly object _lock = new();

    public LruCache(int capacity) => _capacity = capacity;

    public int Count
    {
        get { lock (_lock) return _map.Count; }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddLast(node);
                value = node.Value.Value;
                return true;
            }

            value = default!;
            return false;
        }
    }

    public void Put(TKey key, TValue value)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _order.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<KeyValuePair<TKey, TValue>>(new KeyValuePair<TKey, TValue>(key, value));
            _order.AddLast(node);
            _map[key] = node;
            while (_map.Count > _capacity)
            {
                var eldest = _order.First!;
                _order.RemoveFirst();
                _map.Remove(eldest.Value.Key);
            }
        }
    }
}
