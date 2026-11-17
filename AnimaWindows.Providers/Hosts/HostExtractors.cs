using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Providers.Common;

namespace AnimaWindows.Providers.Hosts;

/// <summary>A playable result from a video host. <see cref="Hls"/> true means <see cref="Url"/> is an m3u8 playlist. Mirrors <c>ResolvedStream</c>.</summary>
public sealed record ResolvedStream(string Url, IReadOnlyDictionary<string, string> Headers, bool Hls)
{
    public IReadOnlyList<StreamVariant> Variants { get; init; } = [];
}

/// <summary>One video host. Add a new host by implementing this and listing it in <see cref="HostRegistry.All"/>.</summary>
public interface IHostExtractor
{
    string Name { get; }

    /// <summary>Lowercase fragments of shinden's <c>player</c> label that identify this host without an API call.</summary>
    IReadOnlyList<string> PlayerNameHints { get; }

    bool Handles(string host);

    Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default);
}

public static partial class HostRegistry
{
    public static IReadOnlyList<IHostExtractor> All { get; } =
        [SibnetExtractor.Instance, JwPlayerExtractor.Instance, CdaExtractor.Instance, VkExtractor.Instance, VidaraExtractor.Instance, YouruploadExtractor.Instance];

    /// <summary>Shinden player labels that only link out to a commercial service; they never carry an embeddable video.</summary>
    private static readonly string[] External =
        ["crunchyroll", "netflix", "disney", "amazon", "prime", "hidive", "hbo", "apple", "muse", "bilibili", "youtube"];

    private static readonly (string Key, string Reason)[] Unsupported =
        [("mega", "MEGA is end-to-end encrypted and cannot be streamed by this app")];

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    public static IHostExtractor? ByHost(string host)
    {
        var h = host.ToLowerInvariant();
        return All.FirstOrDefault(e => e.Handles(h));
    }

    public static IHostExtractor? ByPlayerName(string name)
    {
        var n = name.ToLowerInvariant();
        return All.FirstOrDefault(e => e.PlayerNameHints.Any(hint => n.Contains(hint, StringComparison.Ordinal)));
    }

    public static string? ExternalReason(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var (key, reason) in Unsupported)
        {
            if (n.Contains(key, StringComparison.Ordinal)) return reason;
        }
        var tokens = NonAlnum().Split(n).Where(t => t.Length > 0).ToHashSet();
        return External.Any(tokens.Contains) ? $"{name} is an external service link, not an embeddable video" : null;
    }
}

internal static class HostUtil
{
    public static string HostOf(string url)
    {
        try { return new Uri(url).Host; }
        catch (UriFormatException) { return ""; }
    }

    public static string UnescapeSlashes(string s) => s.Replace("\\/", "/").Replace("\\u0026", "&").Replace("&amp;", "&");

    public static int? HeightFromLabel(string s)
    {
        var m = Regex.Match(s, "([0-9]{3,4})");
        return m.Success && int.TryParse(m.Groups[1].Value, out var h) ? h : null;
    }

    /// <summary>Resolves <paramref name="reference"/> against <paramref name="baseUrl"/> (Java <c>URI.resolve</c>), or null when impossible.</summary>
    public static string? Resolve(string baseUrl, string reference)
    {
        try { return new Uri(new Uri(baseUrl), reference).AbsoluteUri; }
        catch (UriFormatException) { return null; }
    }

    public static Dictionary<string, string> Headers(params (string, string)[] pairs) => pairs.ToDictionary(p => p.Item1, p => p.Item2);

    public static string? JsonString(JsonElement obj, string key)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    public static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
}

/// <summary>video.sibnet.ru: the shell page names an mp4 path that 302s to a referer-checked CDN host.</summary>
public sealed partial class SibnetExtractor : IHostExtractor
{
    public static SibnetExtractor Instance { get; } = new();
    private SibnetExtractor() { }

    public string Name => "Sibnet";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["sibnet"];
    public bool Handles(string host) => host.EndsWith("sibnet.ru", StringComparison.Ordinal);

    [GeneratedRegex(@"player\.src\(\s*\[\s*\{\s*src\s*:\s*[""']([^""']+\.mp4[^""']*)[""']")]
    private static partial Regex Src();

    [GeneratedRegex(@"src\s*:\s*[""'](/v/[^""']+\.mp4[^""']*)[""']")]
    private static partial Regex SrcAny();

    public static string? Parse(string html)
    {
        var m = Src().Match(html);
        if (!m.Success) m = SrcAny().Match(html);
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var page = await http.GetStringAsync(embedUrl, referer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var path = Parse(page) ?? throw new ProviderException("Sibnet: no mp4 source in embed page (video removed?)");
        var abs = HostUtil.Resolve(embedUrl, path) ?? throw new ProviderException($"Sibnet: bad mp4 path '{path}'");
        var final = await http.RedirectTargetAsync(abs, embedUrl, cancellationToken).ConfigureAwait(false) ?? abs;
        return new ResolvedStream(final, HostUtil.Headers(("Referer", "https://video.sibnet.ru/"), ("User-Agent", NetworkConstants.AniCliUserAgent)), Hls: false);
    }
}

/// <summary>streamwish / filemoon / playerwish and friends: JWPlayer <c>sources</c>, <c>hlsN</c> keys, optionally Dean Edwards packed.</summary>
public sealed partial class JwPlayerExtractor : IHostExtractor
{
    public static JwPlayerExtractor Instance { get; } = new();
    private JwPlayerExtractor() { }

    private static readonly string[] Hosts =
    [
        "streamwish", "playerwish", "hlswish", "wishembed", "awish", "dwish", "swdyu", "strwish", "sfastwish", "flaswish",
        "filemoon", "moonplayer", "kerapoxy", "vidhide", "dhtpre", "minochinos", "streamhide",
    ];

    public string Name => "JWPlayer family";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["streamwish", "filemoon", "vidhide", "playerwish", "wishembed"];
    public bool Handles(string host) => Hosts.Any(h => host.Contains(h, StringComparison.Ordinal));

    [GeneratedRegex(@"""?sources""?\s*:\s*\[\s*\{[^}]*""?file""?\s*:\s*[""']([^""']+\.(?:mp4|m3u8)[^""']*)[""']", RegexOptions.Singleline)]
    private static partial Regex Sources();

    [GeneratedRegex(@"""?file""?\s*:\s*[""']([^""']{20,}\.(?:mp4|m3u8)[^""']*)[""']")]
    private static partial Regex File();

    [GeneratedRegex(@"""hls([234]?)""\s*:\s*""([^""]+\.m3u8[^""]*)""")]
    private static partial Regex Hls();

    [GeneratedRegex(@"\}\s*\(\s*'((?:[^'\\]|\\.)*?)'\s*,\s*([0-9]+)\s*,\s*[0-9]+\s*,\s*'([^']*)'\s*(?:\.split\('\|'\))?\s*\)\s*\)", RegexOptions.Singleline)]
    private static partial Regex Packer();

    [GeneratedRegex("[A-Za-z0-9_]+")]
    private static partial Regex Word();

    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static int? DecodeBase(string word, int radix)
    {
        long n = 0;
        foreach (var ch in word)
        {
            var idx = Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (idx < 0 || idx >= radix) return null;
            n = n * radix + idx;
            if (n > int.MaxValue) return null;
        }
        return (int)n;
    }

    /// <summary>Decodes a Dean Edwards packed script and returns the page text with the decoded script appended; unchanged if none.</summary>
    public static string Unpack(string html)
    {
        var m = Packer().Match(html);
        if (!m.Success) return html;
        var packed = m.Groups[1].Value.Replace("\\'", "'");
        var radix = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var keys = m.Groups[3].Value.Split('|');
        var decoded = Word().Replace(packed, w =>
        {
            var n = DecodeBase(w.Value, radix);
            return n is { } i && i < keys.Length && keys[i].Length > 0 ? keys[i] : w.Value;
        });
        return html + "\n" + decoded;
    }

    /// <summary>Returns the stream URL (resolved against <paramref name="embedUrl"/>) or null.</summary>
    public static string? Parse(string html, string embedUrl)
    {
        var text = Unpack(html);
        string? raw = null;
        var s = Sources().Match(text);
        if (s.Success) raw = s.Groups[1].Value;
        if (raw is null)
        {
            var found = new Dictionary<string, string>();
            foreach (Match h in Hls().Matches(text)) found[h.Groups[1].Value] = h.Groups[2].Value;
            foreach (var key in new[] { "4", "3", "2", "" })
            {
                if (found.TryGetValue(key, out var v)) { raw = v; break; }
            }
        }
        if (raw is null)
        {
            var f = File().Match(text);
            if (f.Success) raw = f.Groups[1].Value;
        }
        return raw is null ? null : HostUtil.Resolve(embedUrl, raw.Replace("\\/", "/"));
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var page = await http.GetStringAsync(embedUrl, referer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var url = Parse(page, embedUrl) ?? throw new ProviderException($"{HostUtil.HostOf(embedUrl)}: no video URL in embed page");
        var u = new Uri(embedUrl);
        var origin = $"{u.Scheme}://{u.Host}/";
        return new ResolvedStream(url, HostUtil.Headers(("Referer", origin), ("User-Agent", NetworkConstants.AniCliUserAgent)), Hls: url.Contains(".m3u8", StringComparison.Ordinal));
    }
}

/// <summary>cda.pl / ebd.cda.pl: <c>player_data</c> JSON; HLS via <c>manifest_apple</c> (verified live), legacy scrambled <c>file</c> ported from youtube-dl.</summary>
public sealed partial class CdaExtractor : IHostExtractor
{
    public static CdaExtractor Instance { get; } = new();
    private CdaExtractor() { }

    public string Name => "CDA";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["cda"];
    public bool Handles(string host) => host == "cda.pl" || host.EndsWith(".cda.pl", StringComparison.Ordinal);

    [GeneratedRegex(@"player_data\s*=\s*(?:'(\{.*?\})'|""(\{.*?\})"")", RegexOptions.Singleline)]
    private static partial Regex Attr();

    public static string DecryptFile(string input)
    {
        var a = input;
        foreach (var p in new[] { "_XDDD", "_CDA", "_ADC", "_CXD", "_QWE", "_Q5", "_IKSDE" }) a = a.Replace(p, "");
        a = WebUtility.UrlDecode(a);
        var sb = new StringBuilder(a.Length);
        foreach (var c in a)
        {
            int f = c;
            sb.Append(f is >= 33 and <= 126 ? (char)(33 + (f + 14) % 94) : c);
        }
        a = sb.ToString().Replace(".cda.mp4", "").Replace(".2cda.pl", ".cda.pl").Replace(".3cda.pl", ".cda.pl");
        return a.Contains("/upstream", StringComparison.Ordinal) ? "https://" + a.Replace("/upstream", ".mp4/upstream") : $"https://{a}.mp4";
    }

    /// <summary>A resolved CDA source: <see cref="Hls"/> true when <see cref="Url"/> is the apple (m3u8) manifest.</summary>
    public sealed record Source(string Url, int? Height, bool Hls);

    /// <summary>
    /// Current CDA pages leave <c>video.file</c> empty and expose <c>manifest_apple</c> (HLS); older ones carry a scrambled
    /// <c>file</c> (mp4). Prefer the mp4 when present, else the HLS manifest.
    /// </summary>
    public static Source? Parse(string html)
    {
        var doc = new HtmlParser().ParseDocument(html);
        var raw = doc.QuerySelector("[player_data]")?.GetAttribute("player_data");
        if (raw is null)
        {
            var m = Attr().Match(html);
            if (!m.Success) return null;
            raw = WebUtility.HtmlDecode(m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value);
        }
        JsonDocument json;
        try { json = JsonDocument.Parse(raw, HostUtil.Lenient); }
        catch (JsonException) { return null; }
        using (json)
        {
            if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("video", out var video) || video.ValueKind != JsonValueKind.Object) return null;
            string? Str(string k)
            {
                var v = HostUtil.JsonString(video, k);
                return string.IsNullOrEmpty(v) || v == "None" ? null : v;
            }
            var height = int.TryParse(Str("height"), out var hh) ? hh : Str("quality") is { } q ? HostUtil.HeightFromLabel(q) : null;
            if (Str("file") is { } f)
            {
                var url = f.StartsWith("http", StringComparison.Ordinal) ? f : DecryptFile(f);
                return new Source(url, height, url.Contains(".m3u8", StringComparison.Ordinal));
            }
            if (Str("manifest_apple") is { } man && man.StartsWith("http", StringComparison.Ordinal)) return new Source(man, height, true);
            return null;
        }
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var page = await http.GetStringAsync(embedUrl, referer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var src = Parse(page) ?? throw new ProviderException("CDA: no playable file in player_data (video removed or protected?)");
        return new ResolvedStream(
            src.Url,
            HostUtil.Headers(("Referer", "https://ebd.cda.pl/"), ("User-Agent", NetworkConstants.AniCliUserAgent)),
            src.Hls)
        {
            Variants = src.Hls ? Array.Empty<StreamVariant>() : new[] { new StreamVariant(src.Height, src.Url) },
        };
    }
}

/// <summary>vk.com / vkvideo.ru <c>video_ext.php</c>: direct <c>urlNNN</c> mp4 entries and an <c>hls</c> fallback in the page JSON.</summary>
public sealed partial class VkExtractor : IHostExtractor
{
    public static VkExtractor Instance { get; } = new();
    private VkExtractor() { }

    public string Name => "VK";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["vk"];
    public bool Handles(string host) =>
        host == "vk.com" || host.EndsWith(".vk.com", StringComparison.Ordinal) || host == "vkvideo.ru" || host.EndsWith(".vkvideo.ru", StringComparison.Ordinal);

    [GeneratedRegex(@"""url([0-9]{3,4})""\s*:\s*""([^""]+)""")]
    private static partial Regex Mp4();

    [GeneratedRegex(@"""hls""\s*:\s*""([^""]+)""")]
    private static partial Regex Hls();

    /// <summary>mp4 variants best first, plus an optional HLS master.</summary>
    public static (IReadOnlyList<StreamVariant> Mp4, string? Hls) Parse(string html)
    {
        var mp4 = Mp4().Matches(html)
            .Select(m => new StreamVariant(int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), HostUtil.UnescapeSlashes(m.Groups[2].Value)))
            .DistinctBy(v => v.Quality)
            .OrderByDescending(v => v.Quality)
            .ToList();
        var h = Hls().Match(html);
        return (mp4, h.Success ? HostUtil.UnescapeSlashes(h.Groups[1].Value) : null);
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var page = await http.GetStringAsync(embedUrl, referer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var (mp4, hls) = Parse(page);
        var headers = HostUtil.Headers(("Referer", "https://vk.com/"), ("User-Agent", NetworkConstants.AniCliUserAgent));
        if (mp4.Count > 0) return new ResolvedStream(mp4[0].Url, headers, false) { Variants = mp4 };
        if (hls is not null) return new ResolvedStream(hls, headers, true);
        throw new ProviderException("VK: no playable url in video_ext page (private or removed video?)");
    }
}

/// <summary>
/// Vidara (rotating domains, e.g. ykfrtza.com; embed <c>/e/&lt;filecode&gt;</c>): <c>POST /api/stream</c> returns an HLS master.
/// Identified by shinden's player label, since the domain changes. Verified live against a real embed (2026-11-01).
/// </summary>
public sealed partial class VidaraExtractor : IHostExtractor
{
    public static VidaraExtractor Instance { get; } = new();
    private VidaraExtractor() { }

    public string Name => "Vidara";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["vidara"];
    public bool Handles(string host) => false;

    [GeneratedRegex(@"^(https?://[^/]+)/(?:e|d|f|v)/([A-Za-z0-9_-]+)")]
    private static partial Regex Embed();

    public static string? ParseStreamingUrl(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body, HostUtil.Lenient);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("streaming_url", out var v) || v.ValueKind != JsonValueKind.String) return null;
            var s = v.GetString();
            return s is not null && s.StartsWith("http", StringComparison.Ordinal) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var m = Embed().Match(embedUrl);
        if (!m.Success) throw new ProviderException($"Vidara: unrecognised embed url {embedUrl}");
        var origin = m.Groups[1].Value;
        var code = m.Groups[2].Value;
        var body = await http.PostJsonAsync($"{origin}/api/stream", $$"""{"filecode":"{{code}}","device":"web"}""", embedUrl, origin, cancellationToken: cancellationToken).ConfigureAwait(false);
        var url = ParseStreamingUrl(body) ?? throw new ProviderException("Vidara: no streaming_url (video removed?)");
        return new ResolvedStream(
            url,
            HostUtil.Headers(("Referer", embedUrl), ("Origin", origin), ("User-Agent", NetworkConstants.AniCliUserAgent)),
            url.Contains(".m3u8", StringComparison.Ordinal));
    }
}

/// <summary>yourupload.com embed: the page's <c>og:video</c> / jwplayer <c>file:</c> is an mp4 URL that redirects to a CDN and needs the embed Referer.</summary>
public sealed partial class YouruploadExtractor : IHostExtractor
{
    public static YouruploadExtractor Instance { get; } = new();
    private YouruploadExtractor() { }

    public string Name => "YourUpload";
    public IReadOnlyList<string> PlayerNameHints { get; } = ["yourupload"];
    public bool Handles(string host) => host.EndsWith("yourupload.com", StringComparison.Ordinal);

    [GeneratedRegex(@"property=[""']og:video[""']\s+content=[""']([^""']+)[""']")]
    private static partial Regex Og();

    [GeneratedRegex(@"file\s*:\s*['""](https?://[^'""]+\.mp4[^'""]*)['""]")]
    private static partial Regex File();

    public static string? Parse(string html)
    {
        var m = Og().Match(html);
        if (!m.Success) m = File().Match(html);
        return m.Success ? m.Groups[1].Value.Replace("&amp;", "&") : null;
    }

    public async Task<ResolvedStream> ExtractAsync(string embedUrl, HostHttp http, string? referer = null, CancellationToken cancellationToken = default)
    {
        var page = await http.GetStringAsync(embedUrl, referer, cancellationToken: cancellationToken).ConfigureAwait(false);
        var url = Parse(page) ?? throw new ProviderException("YourUpload: no video url in embed page (video removed?)");
        return new ResolvedStream(url, HostUtil.Headers(("Referer", embedUrl), ("User-Agent", NetworkConstants.AniCliUserAgent)), false);
    }
}
