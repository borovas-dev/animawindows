using System.Security.Cryptography;
using System.Text;

namespace AnimaWindows.Core.Downloads;

/// <summary>
/// Part ids and on-disk names. One episode = one main part (video) plus sidecars (external audio, subtitles). Mirrors
/// <c>DownloadIds</c> in feature/downloads/DownloadPlanning.kt: ids are <c>key</c> / <c>key#audio</c> / <c>key#subN</c>.
/// </summary>
public static class DownloadIds
{
    private const char Sep = '\u001f';
    private const char Part = '#';

    public static string EpisodeKey(string providerId, string animeId, string episodeNumber) =>
        $"{providerId}{Sep}{animeId}{Sep}{episodeNumber}";

    public static string MainId(string key) => key;

    public static string AudioId(string key) => $"{key}{Part}audio";

    public static string SubtitleId(string key, int index) => $"{key}{Part}sub{index}";

    /// <summary>Episode key of any part id.</summary>
    public static string KeyOf(string partId)
    {
        var i = partId.IndexOf(Part);
        return i < 0 ? partId : partId[..i];
    }

    public static bool IsMain(string partId) => !partId.Contains(Part);

    /// <summary>File/directory stem of a part: <c>main</c>, <c>audio</c> or <c>subN</c>.</summary>
    public static string PartName(string partId)
    {
        var i = partId.IndexOf(Part);
        return i < 0 ? "main" : partId[(i + 1)..];
    }

    public static (string ProviderId, string AnimeId, string EpisodeNumber)? Parse(string key)
    {
        var p = key.Split(Sep);
        return p.Length == 3 ? (p[0], p[1], p[2]) : null;
    }

    /// <summary>
    /// Safe single path segment for <paramref name="name"/>: invalid file-name characters become '_' and a short hash of the
    /// original is appended whenever anything had to change (so "a/b" and "a_b" never collide).
    /// </summary>
    public static string SafeSegment(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 || c == ':' ? '_' : c);
        }

        var cleaned = sb.ToString().Trim().TrimEnd('.', ' ');
        var changed = cleaned != name || cleaned.Length == 0 || cleaned.Length > 80;
        if (cleaned.Length > 80)
        {
            cleaned = cleaned[..80];
        }

        if (cleaned.Length == 0)
        {
            cleaned = "_";
        }

        if (!changed)
        {
            return cleaned;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..6].ToLowerInvariant();
        return $"{cleaned}-{hash}";
    }

    /// <summary><c>&lt;root&gt;/&lt;provider_anime&gt;/&lt;episode&gt;/</c>.</summary>
    public static string EpisodeDirectory(string root, string providerId, string animeId, string episodeNumber) =>
        Path.Combine(root, SafeSegment($"{providerId}_{animeId}"), SafeSegment(episodeNumber));
}
