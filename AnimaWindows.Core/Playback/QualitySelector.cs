using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Playback;

/// <summary>Pure quality-selection logic (ani-cli style: exact match, else nearest lower, else best). Port of <c>QualitySelector</c> in feature/player MediaItemFactory.kt.</summary>
public static class QualitySelector
{
    public static int? TargetHeight(QualityPreference pref) => pref switch
    {
        QualityPreference.P1080 => 1080,
        QualityPreference.P720 => 720,
        QualityPreference.P480 => 480,
        QualityPreference.P360 => 360,
        _ => null,
    };

    /// <summary>Whether the master (ABR) playlist should be played instead of a single variant.</summary>
    public static bool UseMaster(StreamSet streams, QualityPreference pref, int? explicitHeight = null) =>
        streams.MasterUrl is not null &&
        ((pref == QualityPreference.Best && explicitHeight is null) || streams.Variants.Count == 0);

    public static StreamVariant? SelectVariant(IReadOnlyList<StreamVariant> variants, QualityPreference pref, int? explicitHeight = null)
    {
        if (variants.Count == 0) return null;
        var known = variants.Where(v => v.Quality is not null).ToList();
        var height = explicitHeight ?? TargetHeight(pref);
        if (height is { } h)
        {
            return known.FirstOrDefault(v => v.Quality == h)
                   ?? known.Where(v => v.Quality < h).OrderByDescending(v => v.Quality).FirstOrDefault()
                   ?? BestOf(variants);
        }

        if (pref == QualityPreference.Worst)
            return known.OrderBy(v => v.Quality).FirstOrDefault() ?? variants[variants.Count - 1];
        return BestOf(variants);
    }

    private static StreamVariant BestOf(IReadOnlyList<StreamVariant> variants) =>
        variants.Where(v => v.Quality is not null).OrderByDescending(v => v.Quality).FirstOrDefault() ?? variants[0];

    /// <summary>Resolves the URL to play, or null when there is nothing playable.</summary>
    public static string? ResolveUrl(StreamSet streams, QualityPreference pref, int? explicitHeight = null) =>
        UseMaster(streams, pref, explicitHeight)
            ? streams.MasterUrl
            : SelectVariant(streams.Variants, pref, explicitHeight)?.Url ?? streams.MasterUrl;

    /// <summary>Subtitle MIME type by extension: text/vtt (default), application/x-subrip, text/x-ssa.</summary>
    public static string SubtitleMimeType(string url)
    {
        var path = url;
        var i = path.IndexOf('?');
        if (i >= 0) path = path[..i];
        i = path.IndexOf('#');
        if (i >= 0) path = path[..i];
        path = path.ToLowerInvariant();
        if (path.EndsWith(".srt", StringComparison.Ordinal)) return "application/x-subrip";
        if (path.EndsWith(".ass", StringComparison.Ordinal) || path.EndsWith(".ssa", StringComparison.Ordinal)) return "text/x-ssa";
        return "text/vtt";
    }
}
