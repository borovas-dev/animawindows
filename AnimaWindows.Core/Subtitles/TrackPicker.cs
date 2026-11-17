using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Subtitles;

/// <summary>
/// Picks the provider subtitle track matching the preferred language. Port of <c>TrackPicker</c> without the machine-translation
/// branch (the Windows port does not translate).
/// </summary>
public static class TrackPicker
{
    /// <summary>Language a provider track declares (code, else label), or null when it says nothing usable.</summary>
    public static string? LanguageOf(SubtitleTrack t) => SubtitleLanguages.Normalize(t.Language) ?? SubtitleLanguages.Normalize(t.Label);

    private static bool IsRealTrack(SubtitleTrack t) => t.Label?.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) != true;

    /// <summary>
    /// The provider track in <paramref name="preferred"/> language, or null when there is none, the language is unset, or the
    /// video already has hard subs (<see cref="StreamSet.HardsubLanguage"/>) in that language.
    /// </summary>
    public static SubtitleTrack? Pick(IEnumerable<SubtitleTrack> tracks, string? preferred, string? hardsubLanguage = null)
    {
        var target = SubtitleLanguages.Normalize(preferred);
        if (target is null) return null;
        if (SubtitleLanguages.Normalize(hardsubLanguage) == target) return null;
        return tracks.Where(IsRealTrack).FirstOrDefault(t => LanguageOf(t) == target);
    }
}
