using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Tracking;

/// <summary>OAuth tokens per service in <see cref="ISecretStore"/> (JSON under keys <c>sync.anilist</c> / <c>sync.mal</c>). Mirrors <c>TokenStore</c> in core/sync/Storage.kt.</summary>
public sealed class SecretTokenStore
{
    private readonly ISecretStore _secrets;

    public SecretTokenStore(ISecretStore secrets) => _secrets = secrets;

    public static string KeyOf(SyncService service) => service == SyncService.AniList ? "sync.anilist" : "sync.mal";

    public Tokens? Get(SyncService service)
    {
        try
        {
            var json = _secrets.Get(KeyOf(service));
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize(json, CoreJsonContext.Default.Tokens);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Put(SyncService service, Tokens tokens) =>
        _secrets.Set(KeyOf(service), JsonSerializer.Serialize(tokens, CoreJsonContext.Default.Tokens));

    public void Clear(SyncService service) => _secrets.Remove(KeyOf(service));
}

/// <summary>
/// Pending updates in <c>sync_queue.json</c> inside the data directory; written atomically (temp file + rename). Contains no
/// secrets. Mirrors <c>FileQueueStore</c> in core/sync/Storage.kt. A missing or corrupt file reads as empty.
/// </summary>
public sealed class SyncQueueStore
{
    public const string FileName = "sync_queue.json";

    private readonly string _path;
    private readonly object _lock = new();

    public SyncQueueStore(string dataDirectory) => _path = Path.Combine(dataDirectory, FileName);

    public string FilePath => _path;

    public IReadOnlyList<PendingUpdate> Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return [];
                }

                var list = JsonSerializer.Deserialize(File.ReadAllText(_path), CoreJsonContext.Default.ListPendingUpdate);
                return list ?? [];
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    public void Save(IReadOnlyList<PendingUpdate> items)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items.ToList(), CoreJsonContext.Default.ListPendingUpdate));
            File.Move(tmp, _path, overwrite: true);
        }
    }
}
