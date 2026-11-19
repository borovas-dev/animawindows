using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Hosts;

namespace AnimaWindows.Providers.Shinden;

/// <summary>One entry of an episode's "Playery" list. Audio/Sub are shinden language codes ("jp", "pl", "en", "").</summary>
internal sealed record PlayerEntry(string OnlineId, string Player, string Audio, string Sub, int? Res, string? Translator, bool Machine);

/// <summary>What <c>player_show</c> returned for one online id.</summary>
internal abstract record PlayerShow
{
    /// <summary>Embeddable iframe, to be handed to a host extractor.</summary>
    public sealed record Embed(string Url) : PlayerShow;

    /// <summary>Link-out only (Crunchyroll, Netflix, ...), cannot be played in-app.</summary>
    public sealed record External(string Url, string? Label) : PlayerShow;

    public sealed record Failure(string Message) : PlayerShow;
}

internal sealed record SeriesDetails(string? Synopsis, string? Cover, int? MalId);

/// <summary>Mirrors ShindenParsers.kt.</summary>
internal static partial class ShindenParsers
{
    [GeneratedRegex("""/(series|titles)/([0-9]+-[^/?#"\s]+)""")]
    private static partial Regex SeriesHref();

    [GeneratedRegex(@"myanimelist\.net/anime/([0-9]+)")]
    private static partial Regex Mal();

    [GeneratedRegex(@"/episode/[^/]+/view/([0-9]+)")]
    private static partial Regex View();

    [GeneratedRegex("[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"[?&]strona=([0-9]+)")]
    private static partial Regex PageParam();

    [GeneratedRegex(@"\A[0-9]+-[A-Za-z0-9._~-]+\z")]
    private static partial Regex ValidId();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>jsoup <c>text()</c>: whitespace collapsed and trimmed.</summary>
    private static string Text(IElement e) => Whitespace().Replace(e.TextContent, " ").Trim();

    private static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static string? Absolute(string baseUrl, string? url)
    {
        var u = (url ?? "").Trim();
        if (u.Length == 0) return null;
        if (u.StartsWith("//", StringComparison.Ordinal)) return "https:" + u;
        if (u.StartsWith("http", StringComparison.Ordinal)) return u;
        if (u.StartsWith('/')) return baseUrl.TrimEnd('/') + u;
        return null;
    }

    private static int? ToIntOrNull(string? s) =>
        int.TryParse(s?.Trim(), System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Search result list (<c>ol.sh-tagi-wyniki</c>). Anime ids are <c>&lt;number&gt;-&lt;slug&gt;</c>; the /series vs /titles prefix is not encoded.</summary>
    public static List<Anime> ParseSearch(string html, string providerId, string baseUrl)
    {
        var doc = Parse(html);
        var result = new List<Anime>();
        var seen = new HashSet<string>();
        foreach (var li in doc.QuerySelectorAll("li.sh-tagi-wynik"))
        {
            var href = li.QuerySelector("a[href]")?.GetAttribute("href");
            if (href is null) continue;
            var m = SeriesHref().Match(href);
            if (!m.Success) continue;
            var id = m.Groups[2].Value;
            var title = li.QuerySelector(".sh-tagi-nazwa") is { } t ? Text(t) : "";
            if (title.Length == 0) continue;
            var episodes = ToIntOrNull(li.QuerySelector("[data-episodes]")?.GetAttribute("data-episodes"));
            if (episodes is not > 0) episodes = null;
            if (!seen.Add(id)) continue;
            result.Add(new Anime
            {
                Id = id,
                ProviderId = providerId,
                Title = title,
                CoverUrl = Absolute(baseUrl, li.QuerySelector("img")?.GetAttribute("src")),
                SubEpisodes = episodes,
                Year = li.QuerySelector(".sh-tagi-rok") is { } y ? ToIntOrNull(Text(y)) : null,
            });
        }
        return result;
    }

    public static SeriesDetails ParseDetails(string html)
    {
        var doc = Parse(html);
        string? synopsis = null;
        var description = doc.QuerySelector("#description, .title-full-description");
        if (description is not null)
        {
            var paragraphs = description.QuerySelectorAll("p").Select(Text).Where(s => s.Length > 0).ToList();
            var joined = paragraphs.Count > 0 ? string.Join("\n\n", paragraphs) : Text(description);
            synopsis = NullIfEmpty(joined);
        }
        synopsis ??= NullIfEmpty(doc.QuerySelector("meta[property=\"og:description\"]")?.GetAttribute("content")?.Trim());

        string? ldImage = null;
        foreach (var s in doc.QuerySelectorAll("script[type=\"application/ld+json\"]"))
        {
            try
            {
                using var json = JsonDocument.Parse(s.TextContent, HostUtil.Lenient);
                ldImage = FindString(json.RootElement, "image");
            }
            catch (JsonException)
            {
            }
            if (ldImage is not null) break;
        }
        var cover = ldImage ?? NullIfEmpty(doc.QuerySelector("meta[property=\"og:image\"]")?.GetAttribute("content"));
        var mal = Mal().Match(html);
        return new SeriesDetails(synopsis, cover, mal.Success ? ToIntOrNull(mal.Groups[1].Value) : null);
    }

    private static string? FindString(JsonElement e, string key)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                if (e.TryGetProperty(key, out var direct) && direct.ValueKind == JsonValueKind.String) return direct.GetString();
                foreach (var p in e.EnumerateObject())
                {
                    if (FindString(p.Value, key) is { } found) return found;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                {
                    if (FindString(item, key) is { } found) return found;
                }
                return null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Episode rows of one <c>/episodes</c> page, as listed by the site (newest first). Rows without a link carry no
    /// players and are skipped. <see cref="Episode.Id"/> is the numeric <c>view</c> id.
    /// </summary>
    public static List<Episode> ParseEpisodeRows(string html)
    {
        var result = new List<Episode>();
        foreach (var row in Parse(html).QuerySelectorAll("li.ep-row"))
        {
            var href = row.QuerySelector("a.ep-row__link[href]")?.GetAttribute("href");
            if (href is null) continue;
            var m = View().Match(href);
            if (!m.Success) continue;
            var number = row.QuerySelector(".ep-row__no") is { } n ? Text(n) : "";
            if (number.Length == 0) continue;
            var title = row.QuerySelector(".ep-row__title") is { } t ? NullIfEmpty(Text(t)) : null;
            result.Add(new Episode(m.Groups[1].Value, number, title));
        }
        return result;
    }

    /// <summary>Highest page number linked from the pagination (<c>?strona=N</c>), 1 when there is no pagination.</summary>
    public static int PageCount(string html)
    {
        var max = 0;
        foreach (var a in Parse(html).QuerySelectorAll("nav.ep-pages a[href]"))
        {
            var m = PageParam().Match(a.GetAttribute("href") ?? "");
            if (m.Success && ToIntOrNull(m.Groups[1].Value) is { } n && n > max) max = n;
        }
        return max == 0 ? 1 : max;
    }

    /// <summary>Players of an episode page: <c>#episode-players-data</c> JSON (current site) or <c>a[data-episode]</c> buttons (legacy layout).</summary>
    public static List<PlayerEntry> ParsePlayers(string html)
    {
        var doc = Parse(html);
        var script = doc.QuerySelector("script#episode-players-data");
        if (script is not null)
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, HostUtil.Lenient);
                if (json.RootElement.ValueKind == JsonValueKind.Object
                    && json.RootElement.TryGetProperty("players", out var arr)
                    && arr.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<PlayerEntry>();
                    foreach (var o in arr.EnumerateArray())
                    {
                        if (o.ValueKind != JsonValueKind.Object) continue;
                        var id = Str(o, "online_id");
                        var name = Str(o, "player");
                        if (id is null || name is null) continue;
                        var machine = o.TryGetProperty("machine", out var mv)
                            && (mv.ValueKind == JsonValueKind.True || (mv.ValueKind == JsonValueKind.String && mv.GetString() == "true"));
                        list.Add(new PlayerEntry(
                            id, name, (Str(o, "audio") ?? "").ToLowerInvariant(), (Str(o, "sub") ?? "").ToLowerInvariant(),
                            ResOf(Str(o, "res")), Str(o, "translator"), machine));
                    }
                    return list;
                }
            }
            catch (JsonException)
            {
            }
        }
        var legacy = new List<PlayerEntry>();
        foreach (var a in doc.QuerySelectorAll("a[data-episode]"))
        {
            try
            {
                using var json = JsonDocument.Parse(a.GetAttribute("data-episode") ?? "", HostUtil.Lenient);
                var o = json.RootElement;
                if (o.ValueKind != JsonValueKind.Object) continue;
                var id = Str(o, "online_id");
                var name = Str(o, "player");
                if (id is null || name is null) continue;
                legacy.Add(new PlayerEntry(
                    id, name, (Str(o, "lang_audio") ?? "").ToLowerInvariant(), (Str(o, "lang_subs") ?? "").ToLowerInvariant(),
                    ResOf(Str(o, "max_res")), Str(o, "subs_author"), false));
            }
            catch (JsonException)
            {
            }
        }
        return legacy;
    }

    private static int? ResOf(string? s)
    {
        if (s is null) return null;
        var m = Digits().Match(s);
        return m.Success ? ToIntOrNull(m.Value) : null;
    }

    private static string? Str(JsonElement o, string key)
    {
        var v = HostUtil.JsonString(o, key)?.Trim();
        return string.IsNullOrEmpty(v) || v == "null" ? null : v;
    }

    public static PlayerShow ParsePlayerShow(string html)
    {
        var doc = Parse(html);
        var src = doc.QuerySelector("iframe[src]")?.GetAttribute("src");
        if (src is not null)
        {
            var url = Absolute("https://shinden.pl", src);
            return url is null ? new PlayerShow.Failure($"Unexpected iframe src '{src}'") : new PlayerShow.Embed(url);
        }
        var a = doc.QuerySelector("a.button-player[href]");
        if (a is not null) return new PlayerShow.External(a.GetAttribute("href") ?? "", NullIfEmpty(Text(a)));
        var msg = doc.QuerySelector(".player-message, .error") is { } e ? Text(e) : null;
        var trimmed = html.Trim();
        return new PlayerShow.Failure(msg ?? $"player_show returned no player (antibot delay too short?): {(trimmed.Length > 120 ? trimmed[..120] : trimmed)}");
    }

    public static bool IsValidId(string idSlug) => ValidId().IsMatch(idSlug);

    private static readonly System.Text.RegularExpressions.Regex AuthPattern = new(
        @"_Storage\.basic\s*=\s*['""]([^'""]+)['""]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The per-visitor API token the episode page sets for its own player (<c>_Storage.basic = '...'</c>), URL-encoded for a
    /// query string; null when absent. The hard-coded guest token expires, this one is always current.
    /// </summary>
    public static string? ParseAuth(string html)
    {
        var m = AuthPattern.Match(html);
        return m.Success ? Uri.EscapeDataString(Uri.UnescapeDataString(m.Groups[1].Value)) : null;
    }
}
