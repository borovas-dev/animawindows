using System.Text.RegularExpressions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Providers.Common;

/// <summary>Shared HLS helpers for providers that expose an m3u8 master playlist. Mirrors <c>M3u8</c> in provider/common/M3u8.kt.</summary>
public static partial class M3u8
{
    /// <summary>Variants sorted best to worst plus the default external audio rendition (if any); URLs are absolute.</summary>
    public sealed record MasterPlaylist(IReadOnlyList<StreamVariant> Variants, string? ExternalAudioUrl);

    [GeneratedRegex(@"RESOLUTION=\d+x(\d+)")]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex("URI=\"([^\"]*)\"")]
    private static partial Regex UriRegex();

    /// <summary>Parses <paramref name="text"/> (a master playlist fetched from <paramref name="masterUrl"/>); relative URIs are resolved against <paramref name="masterUrl"/>.</summary>
    public static MasterPlaylist ParseMaster(string text, string masterUrl)
    {
        var lines = text.Split(["\r\n", "\n", "\r"], StringSplitOptions.None).Select(l => l.Trim()).ToArray();
        var variants = new List<StreamVariant>();
        string? audio = null;
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var m = ResolutionRegex().Match(line);
                int? height = m.Success && int.TryParse(m.Groups[1].Value, out var h) ? h : null;
                var j = i + 1;
                while (j < lines.Length && (lines[j].Length == 0 || lines[j].StartsWith('#'))) j++;
                if (j < lines.Length) variants.Add(new StreamVariant(height, Resolve(masterUrl, lines[j])));
                i = j;
            }
            else if (audio is null && line.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal) && line.Contains("DEFAULT=YES", StringComparison.Ordinal))
            {
                var m = UriRegex().Match(line);
                if (m.Success) audio = Resolve(masterUrl, m.Groups[1].Value);
            }
            i++;
        }
        // OrderByDescending is stable, like Kotlin's sortedByDescending.
        return new MasterPlaylist(variants.OrderByDescending(v => v.Quality ?? -1).ToList(), audio);
    }

    /// <summary>Resolves <paramref name="reference"/> against <paramref name="baseUrl"/>; absolute http(s) refs are returned unchanged.</summary>
    public static string Resolve(string baseUrl, string reference) =>
        reference.StartsWith("http://", StringComparison.Ordinal) || reference.StartsWith("https://", StringComparison.Ordinal)
            ? reference
            : new Uri(new Uri(baseUrl), reference).AbsoluteUri;
}
