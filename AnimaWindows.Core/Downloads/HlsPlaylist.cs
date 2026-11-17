using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AnimaWindows.Core.Downloads;

internal enum HlsResourceKind
{
    Segment,
    Key,
    Map,
}

/// <summary>One file to download for a media playlist (segment, AES key, fMP4 init section).</summary>
internal sealed class HlsResource
{
    public required HlsResourceKind Kind { get; init; }

    public required Uri Url { get; init; }

    /// <summary>First byte of a byte-ranged resource; null when the whole resource is meant.</summary>
    public long? Offset { get; init; }

    /// <summary>Length of a byte-ranged resource; null when the whole resource is meant.</summary>
    public long? Length { get; init; }

    /// <summary>File name (no directory) the resource is stored under.</summary>
    public required string LocalName { get; init; }
}

internal sealed class HlsLine
{
    public required string Text { get; init; }

    public HlsResource? Resource { get; init; }

    /// <summary>A consumed <c>#EXT-X-BYTERANGE</c> tag (each ranged segment is stored as its own file, so the tag is dropped).</summary>
    public bool Skip { get; init; }
}

internal sealed record HlsVariantInfo(int? Height, long Bandwidth, Uri Url);

/// <summary>
/// Minimal HLS media-playlist model: finds every downloadable resource (segments, <c>EXT-X-KEY</c> keys, <c>EXT-X-MAP</c> init
/// sections, with byte ranges) and renders a local copy where every URI points at the stored file (relative paths). All other
/// tags are preserved verbatim, so media sequence numbers, IV defaults and discontinuities stay correct. Master playlists are
/// only inspected (<see cref="IsMaster"/>, <see cref="ParseVariants"/>), never downloaded.
/// </summary>
internal sealed class HlsPlaylist
{
    private static readonly Regex UriAttribute = new("URI=\"[^\"]*\"", RegexOptions.Compiled);
    private static readonly Regex ByteRangeAfterComma = new(",BYTERANGE=\"[^\"]*\"", RegexOptions.Compiled);
    private static readonly Regex ByteRangeBeforeComma = new("BYTERANGE=\"[^\"]*\",?", RegexOptions.Compiled);

    private HlsPlaylist(IReadOnlyList<HlsLine> lines, IReadOnlyList<HlsResource> resources, int segmentCount)
    {
        Lines = lines;
        Resources = resources;
        SegmentCount = segmentCount;
    }

    public IReadOnlyList<HlsLine> Lines { get; }

    /// <summary>Unique resources in playlist order (a key used by many segments appears once).</summary>
    public IReadOnlyList<HlsResource> Resources { get; }

    public int SegmentCount { get; }

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static HlsPlaylist Parse(string text, Uri baseUri)
    {
        var lines = new List<HlsLine>();
        var resources = new List<HlsResource>();
        var byKey = new Dictionary<string, HlsResource>(StringComparer.Ordinal);
        var nextOffset = new Dictionary<string, long>(StringComparer.Ordinal);
        (long? Offset, long Length)? pending = null;
        var segments = 0;
        var keys = 0;
        var maps = 0;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                lines.Add(new HlsLine { Text = line });
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.Ordinal))
            {
                pending = ParseRange(line["#EXT-X-BYTERANGE:".Length..]);
                lines.Add(new HlsLine { Text = line, Skip = true });
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var method = GetAttribute(line, "METHOD");
                var uri = GetAttribute(line, "URI");
                var target = uri is not null && method != "NONE" ? TryResolve(baseUri, uri) : null;
                if (target is null)
                {
                    lines.Add(new HlsLine { Text = line });
                    continue;
                }

                var id = "k|" + target.AbsoluteUri;
                if (!byKey.TryGetValue(id, out var res))
                {
                    res = new HlsResource { Kind = HlsResourceKind.Key, Url = target, LocalName = $"key{keys++}.key" };
                    byKey[id] = res;
                    resources.Add(res);
                }

                lines.Add(new HlsLine { Text = line, Resource = res });
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var uri = GetAttribute(line, "URI");
                var target = uri is null ? null : TryResolve(baseUri, uri);
                if (target is null)
                {
                    lines.Add(new HlsLine { Text = line });
                    continue;
                }

                var range = GetAttribute(line, "BYTERANGE") is { } br ? ParseRange(br) : null;
                var offset = range is null ? (long?)null : range.Value.Offset ?? 0;
                var length = range?.Length;
                var id = $"m|{target.AbsoluteUri}|{offset}|{length}";
                if (!byKey.TryGetValue(id, out var res))
                {
                    res = new HlsResource
                    {
                        Kind = HlsResourceKind.Map,
                        Url = target,
                        Offset = offset,
                        Length = length,
                        LocalName = $"init{maps++}.mp4",
                    };
                    byKey[id] = res;
                    resources.Add(res);
                }

                lines.Add(new HlsLine { Text = line, Resource = res });
            }
            else if (line[0] == '#')
            {
                lines.Add(new HlsLine { Text = line });
            }
            else
            {
                var target = new Uri(baseUri, line);
                long? offset = null;
                long? length = null;
                if (pending is { } p)
                {
                    var start = p.Offset ?? (nextOffset.TryGetValue(target.AbsoluteUri, out var next) ? next : 0);
                    offset = start;
                    length = p.Length;
                    nextOffset[target.AbsoluteUri] = start + p.Length;
                    pending = null;
                }

                var res = new HlsResource
                {
                    Kind = HlsResourceKind.Segment,
                    Url = target,
                    Offset = offset,
                    Length = length,
                    LocalName = $"seg{segments++:D5}{ExtensionOf(target)}",
                };
                resources.Add(res);
                lines.Add(new HlsLine { Text = line, Resource = res });
            }
        }

        return new HlsPlaylist(lines, resources, segments);
    }

    /// <summary>Local playlist text; <paramref name="relativeDirectory"/> is where the resources live relative to the playlist file.</summary>
    public string Render(string relativeDirectory)
    {
        var sb = new StringBuilder();
        foreach (var line in Lines)
        {
            if (line.Skip)
            {
                continue;
            }

            var text = line.Text;
            if (line.Resource is { } res)
            {
                var local = relativeDirectory + "/" + res.LocalName;
                switch (res.Kind)
                {
                    case HlsResourceKind.Segment:
                        text = local;
                        break;
                    case HlsResourceKind.Key:
                        text = UriAttribute.Replace(text, _ => "URI=\"" + local + "\"");
                        break;
                    default:
                        text = UriAttribute.Replace(text, _ => "URI=\"" + local + "\"");
                        text = ByteRangeAfterComma.Replace(text, string.Empty);
                        text = ByteRangeBeforeComma.Replace(text, string.Empty);
                        break;
                }
            }

            sb.Append(text).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Variants of a master playlist (<c>EXT-X-STREAM-INF</c> + the URI line that follows).</summary>
    public static IReadOnlyList<HlsVariantInfo> ParseVariants(string text, Uri baseUri)
    {
        var result = new List<HlsVariantInfo>();
        string? attrs = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                attrs = line;
            }
            else if (line.Length > 0 && line[0] != '#' && attrs is not null)
            {
                var url = TryResolve(baseUri, line);
                if (url is not null)
                {
                    var bandwidth = long.TryParse(GetAttribute(attrs, "BANDWIDTH"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) ? b : 0;
                    int? height = null;
                    var res = GetAttribute(attrs, "RESOLUTION");
                    var x = res?.IndexOfAny(['x', 'X']) ?? -1;
                    if (res is not null && x > 0 && int.TryParse(res[(x + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    {
                        height = h;
                    }

                    result.Add(new HlsVariantInfo(height, bandwidth, url));
                }

                attrs = null;
            }
        }

        return result;
    }

    private static Uri? TryResolve(Uri baseUri, string reference)
    {
        if (!Uri.TryCreate(baseUri, reference, out var uri))
        {
            return null;
        }

        return uri.Scheme is "http" or "https" ? uri : null;
    }

    private static string ExtensionOf(Uri url)
    {
        var ext = Path.GetExtension(url.AbsolutePath);
        if (ext.Length is >= 2 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit))
        {
            return ext.ToLowerInvariant();
        }

        return ".ts";
    }

    /// <summary><c>n[@o]</c> as used by EXT-X-BYTERANGE and the MAP BYTERANGE attribute.</summary>
    private static (long? Offset, long Length)? ParseRange(string value)
    {
        var parts = value.Trim().Trim('"').Split('@');
        if (!long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var length))
        {
            return null;
        }

        long? offset = null;
        if (parts.Length > 1 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var o))
        {
            offset = o;
        }

        return (offset, length);
    }

    /// <summary>Value of attribute <paramref name="name"/> in a tag line, without quotes; null when absent.</summary>
    internal static string? GetAttribute(string line, string name)
    {
        var colon = line.IndexOf(':');
        var i = colon < 0 ? 0 : colon + 1;
        while (i < line.Length)
        {
            var eq = line.IndexOf('=', i);
            if (eq < 0)
            {
                return null;
            }

            var key = line[i..eq].Trim();
            string value;
            int end;
            if (eq + 1 < line.Length && line[eq + 1] == '"')
            {
                var close = line.IndexOf('"', eq + 2);
                if (close < 0)
                {
                    close = line.Length;
                }

                value = line[(eq + 2)..close];
                end = close + 1;
            }
            else
            {
                var comma = line.IndexOf(',', eq + 1);
                if (comma < 0)
                {
                    comma = line.Length;
                }

                value = line[(eq + 1)..comma];
                end = comma;
            }

            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return value;
            }

            i = end < line.Length && line[end] == ',' ? end + 1 : end;
            if (i == end && end < line.Length)
            {
                i = end + 1;
            }
        }

        return null;
    }
}
