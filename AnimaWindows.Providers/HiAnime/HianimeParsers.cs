using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnimaWindows.Core.Models;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.HiAnime;

/// <summary>Pure parsing helpers for hianime (ports of ani-cli v5 shell functions). Mirrors <c>HianimeParsers</c> in HianimeParsers.kt.</summary>
internal static partial class HianimeParsers
{
    public const string EmbedKey = "otaku-embed-v1";
    public const string ServerName = "ZokoAnime";

    [GeneratedRegex(@"^(https?://[^/]*)")]
    private static partial Regex OriginRegex();

    [GeneratedRegex(@"/mal/(\d+)/")]
    private static partial Regex MalIdRegex();

    [GeneratedRegex("window\\.__P=\"([^\"]*)\"")]
    private static partial Regex BlobRegex();

    /// <summary>ani-cli <c>b64_decode</c>; tolerant of missing padding and url-safe alphabet. Throws <see cref="FormatException"/> on invalid input.</summary>
    public static byte[] B64Decode(string input)
    {
        var s = input.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
        return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '='));
    }

    /// <summary>ani-cli <c>deobfuscate_blob</c> core: XOR with the repeating key. Symmetric, so also encodes.</summary>
    public static byte[] Xor(byte[] data, string key = EmbedKey)
    {
        var k = Encoding.ASCII.GetBytes(key);
        var result = new byte[data.Length];
        for (var i = 0; i < data.Length; i++) result[i] = (byte)(data[i] ^ k[i % k.Length]);
        return result;
    }

    /// <summary>ani-cli <c>deobfuscate_blob</c>: base64 then XOR with the repeating key.</summary>
    public static string DeobfuscateBlob(string blob, string key = EmbedKey) =>
        Encoding.UTF8.GetString(Xor(B64Decode(blob), key));

    /// <summary>
    /// Endpoints answer <c>{"status":true,"html":"..."}</c>. Returns the decoded html, or the raw text with
    /// backslash escapes stripped (ani-cli behaviour) when it is not JSON.
    /// </summary>
    public static string HtmlPayload(string body)
    {
        var trimmed = body.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            string? html = null;
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("html", out var h))
                {
                    html = h.ValueKind switch
                    {
                        JsonValueKind.String => h.GetString(),
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => h.GetRawText(),
                        _ => null,
                    };
                }
            }
            catch (JsonException)
            {
            }
            if (html is not null) return html;
        }
        return body.Replace("\\", "");
    }

    public static IReadOnlyList<Anime> ParseSearch(string html, string providerId)
    {
        var doc = HtmlJsoupCompat.Parse(html);
        foreach (var sidebar in doc.QuerySelectorAll("#main-sidebar").ToList()) sidebar.Remove();
        var seen = new HashSet<string>();
        var result = new List<Anime>();
        foreach (var detail in doc.QuerySelectorAll("div.film-detail"))
        {
            var a = detail.QuerySelector("h3.film-name a");
            if (a is null) continue;
            var id = a.Attr("href").SubstringBefore('?').TrimEnd('/').SubstringAfterLast('/');
            var title = a.Attr("title");
            if (string.IsNullOrWhiteSpace(title)) title = a.JsoupText();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !seen.Add(id)) continue;
            var item = detail.ParentElement;
            var img = item?.QuerySelector(".film-poster img");
            string? cover = null;
            if (img is not null)
            {
                cover = new[] { img.Attr("data-src"), img.Attr("src") }
                    .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s) && !s.StartsWith("data:", StringComparison.Ordinal));
            }
            result.Add(new Anime
            {
                Id = id,
                ProviderId = providerId,
                Title = title,
                CoverUrl = cover,
                SubEpisodes = TickCount(item, ".tick-sub"),
                DubEpisodes = TickCount(item, ".tick-dub"),
            });
        }
        return result;
    }

    private static int? TickCount(AngleSharp.Dom.IElement? item, string selector)
    {
        var el = item?.QuerySelector(selector);
        if (el is null) return null;
        var digits = new string(el.JsoupText().Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : null;
    }

    public static IReadOnlyList<Episode> ParseEpisodes(string payload, string slug)
    {
        var doc = HtmlJsoupCompat.Parse(HtmlPayload(payload));
        List<Episode> Collect(Func<string, bool> hrefOk)
        {
            var list = new List<Episode>();
            foreach (var el in doc.QuerySelectorAll(".ep-item"))
            {
                var number = el.Attr("data-number");
                var id = el.Attr("data-id");
                if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(id) || !hrefOk(el.Attr("href"))) continue;
                var title = el.Attr("title");
                list.Add(new Episode(id, number, string.IsNullOrWhiteSpace(title) ? null : title));
            }
            return list.DistinctBy(e => e.Id)
                .OrderBy(e => HtmlJsoupCompat.ToDoubleOrNull(e.Number) ?? double.MaxValue)
                .ToList();
        }

        // The endpoint is keyed by the numeric id, so every item belongs to the show. Prefer exact slug matches, but
        // fall back to any watch link: imported ani-cli ids can carry a slug the site has since renamed.
        var exact = Collect(h => h.Contains($"/watch/{slug}?ep=", StringComparison.Ordinal));
        return exact.Count > 0
            ? exact
            : Collect(h => h.Contains("/watch/", StringComparison.Ordinal) && h.Contains("?ep=", StringComparison.Ordinal));
    }

    /// <summary>data-hash of the ZokoAnime server for <paramref name="type"/> ("sub"/"dub"), decoded to the embed URL.</summary>
    public static string? ParseEmbedUrl(string payload, string type, string server = ServerName)
    {
        var doc = HtmlJsoupCompat.Parse(HtmlPayload(payload));
        var hash = doc.QuerySelectorAll($".server-item[data-type='{type}']")
            .FirstOrDefault(e => e.Attr("data-server-name") == server)?.Attr("data-hash");
        if (string.IsNullOrWhiteSpace(hash)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(B64Decode(hash)).Trim();
            return decoded.Length > 0 ? decoded : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public static string EmbedOrigin(string embedUrl)
    {
        var m = OriginRegex().Match(embedUrl);
        return m.Success ? m.Groups[1].Value + "/" : embedUrl;
    }

    public static int? MalId(string embedUrl)
    {
        var m = MalIdRegex().Match(embedUrl);
        return m.Success && int.TryParse(m.Groups[1].Value, out var id) ? id : null;
    }

    public static string? ExtractBlob(string embedHtml)
    {
        var m = BlobRegex().Match(embedHtml);
        return m.Success ? m.Groups[1].Value : null;
    }

    public sealed record EmbedConfig(
        string? MasterUrl,
        IReadOnlyList<SubtitleTrack> Subtitles,
        SkipInterval? Intro = null,
        SkipInterval? Outro = null,
        string? SpriteVttUrl = null);

    public static EmbedConfig ParseEmbedConfig(string jsonText)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(jsonText);
        }
        catch (JsonException)
        {
            return new EmbedConfig(null, []);
        }
        using (doc)
        {
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) return new EmbedConfig(null, []);

            var src = StringOrNull(obj, "src");
            if (src is not null && !src.Contains(".m3u8", StringComparison.Ordinal)) src = null;

            var subs = new List<SubtitleTrack>();
            if (obj.TryGetProperty("subtitles", out var subsEl) && subsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in subsEl.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var url = StringOrNull(el, "src");
                    if (url is null) continue;
                    subs.Add(new SubtitleTrack(url, StringOrNull(el, "lang"), StringOrNull(el, "label"), BoolOrNull(el, "default") ?? false));
                }
            }
            var sortedSubs = subs.OrderByDescending(s => s.IsDefault).ToList();

            SkipInterval? intro = null, outro = null;
            if (obj.TryGetProperty("skip", out var skip) && skip.ValueKind == JsonValueKind.Object)
            {
                intro = SkipIntervalOf(skip, "intro");
                outro = SkipIntervalOf(skip, "outro");
            }

            // `sprite_vtt` is "" for almost every embed (survey: 1 of ~40); treat blank/non-string as absent.
            string? sprite = null;
            if (obj.TryGetProperty("sprite_vtt", out var spriteEl) && spriteEl.ValueKind == JsonValueKind.String)
            {
                var t = spriteEl.GetString()?.Trim();
                if (t is not null && (t.StartsWith("http://", StringComparison.Ordinal) || t.StartsWith("https://", StringComparison.Ordinal))) sprite = t;
            }
            return new EmbedConfig(src, sortedSubs, intro, outro, sprite);
        }
    }

    private static string? StringOrNull(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? BoolOrNull(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(v.GetString(), out var b) ? b : null,
            _ => null,
        };
    }

    private static double? DoubleOrNull(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetDouble(out var d) ? d : null,
            JsonValueKind.String => HtmlJsoupCompat.ToDoubleOrNull(v.GetString()),
            _ => null,
        };
    }

    private static SkipInterval? SkipIntervalOf(JsonElement skip, string name)
    {
        if (!skip.TryGetProperty(name, out var o) || o.ValueKind != JsonValueKind.Object) return null;
        var start = DoubleOrNull(o, "start");
        var end = DoubleOrNull(o, "end");
        if (start is null || end is null) return null;
        return end > start && start >= 0 ? new SkipInterval(start.Value, end.Value) : null;
    }

    /// <summary>Synopsis and cover from an anime page.</summary>
    public static (string? Synopsis, string? Cover) ParseDetails(string html)
    {
        var doc = HtmlJsoupCompat.Parse(html);
        var synopsis = NonBlank(doc.QuerySelector(".film-description .text")?.JsoupText())
            ?? NonBlank(doc.QuerySelector("meta[property='og:description']")?.Attr("content"));
        var cover = NonBlank(doc.QuerySelector(".anisc-poster img")?.Attr("src"))
            ?? NonBlank(doc.QuerySelector("meta[property='og:image']")?.Attr("content"));
        return (synopsis, cover);
    }

    private static string? NonBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
