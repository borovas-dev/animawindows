using System.Text.Json;
using System.Text.RegularExpressions;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.AniSkip;

internal sealed record PrefixItem(int Id, string Name);

/// <summary>Fallback title to MAL id lookup via MyAnimeList's prefix search. Never throws except for cancellation. Port of <c>MalIdResolver</c>.</summary>
public sealed partial class MalIdResolver : IMalIdResolver
{
    public const string DefaultBaseUrl = "https://myanimelist.net";

    private readonly HttpClient _client;
    private readonly string _baseUrl;

    public MalIdResolver(HttpClient client, string baseUrl = DefaultBaseUrl)
    {
        _client = client;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<int?> ResolveAsync(Anime anime, CancellationToken cancellationToken = default) =>
        anime.MalId ?? await ResolveTitleAsync(anime.Title, cancellationToken).ConfigureAwait(false);

    public async Task<int?> ResolveTitleAsync(string title, CancellationToken cancellationToken = default)
    {
        var keyword = Spaces().Replace(NonAlnum().Replace(title, " "), " ").Trim();
        if (keyword.Length == 0) return null;
        try
        {
            return PickBest(title, await FetchAsync(keyword, cancellationToken).ConfigureAwait(false));
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

    private async Task<List<PrefixItem>> FetchAsync(string keyword, CancellationToken ct)
    {
        var url = $"{_baseUrl}/search/prefix.json?type=anime&keyword={Uri.EscapeDataString(keyword)}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", AniSkipParsing.UserAgent);
        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return [];
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        var items = new List<PrefixItem>();
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array)
            return items;
        foreach (var cat in cats.EnumerateArray())
        {
            var type = cat.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
            if (type != "anime" && type.Length != 0) continue;
            if (!cat.TryGetProperty("items", out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var it in arr.EnumerateArray())
            {
                if (!it.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number) throw new JsonException("id");
                var name = it.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
                items.Add(new PrefixItem(id.GetInt32(), name));
            }
        }

        return items;
    }

    internal static string NormalizeTitle(string s) => NonLowerAlnum().Replace(s.ToLowerInvariant(), " ").Trim();

    /// <summary>Exact normalised match, else contains (either direction), else the first item.</summary>
    internal static int? PickBest(string title, IReadOnlyList<PrefixItem> items)
    {
        if (items.Count == 0) return null;
        var t = NormalizeTitle(title);
        var best = items.FirstOrDefault(i => NormalizeTitle(i.Name) == t)
                   ?? items.FirstOrDefault(i =>
                   {
                       var n = NormalizeTitle(i.Name);
                       return n.Contains(t, StringComparison.Ordinal) || t.Contains(n, StringComparison.Ordinal);
                   })
                   ?? items[0];
        return best.Id;
    }

    [GeneratedRegex("[^A-Za-z0-9]")]
    private static partial Regex NonAlnum();

    [GeneratedRegex(" +")]
    private static partial Regex Spaces();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonLowerAlnum();
}
