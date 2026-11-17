using System.Globalization;

namespace AnimaWindows.Core.Subtitles;

/// <summary>Language code handling shared by the picker and settings. Port of <c>SubtitleLanguages</c> (no ML Kit).</summary>
public static class SubtitleLanguages
{
    // (iso2, iso3 terminology, iso3 bibliographic or null, English name). Static so behaviour does not depend on the host's ICU/NLS data.
    private static readonly (string Two, string Three, string? Biblio, string Name)[] Known =
    [
        ("en", "eng", null, "english"), ("ja", "jpn", null, "japanese"), ("zh", "zho", "chi", "chinese"),
        ("pl", "pol", null, "polish"), ("pt", "por", null, "portuguese"), ("es", "spa", null, "spanish"),
        ("de", "deu", "ger", "german"), ("fr", "fra", "fre", "french"), ("it", "ita", null, "italian"),
        ("ru", "rus", null, "russian"), ("ko", "kor", null, "korean"), ("ar", "ara", null, "arabic"),
        ("tr", "tur", null, "turkish"), ("nl", "nld", "dut", "dutch"), ("sv", "swe", null, "swedish"),
        ("id", "ind", null, "indonesian"), ("vi", "vie", null, "vietnamese"), ("th", "tha", null, "thai"),
        ("uk", "ukr", null, "ukrainian"), ("cs", "ces", "cze", "czech"), ("sk", "slk", "slo", "slovak"),
        ("ro", "ron", "rum", "romanian"), ("hu", "hun", null, "hungarian"), ("el", "ell", "gre", "greek"),
        ("he", "heb", null, "hebrew"), ("hi", "hin", null, "hindi"), ("fa", "fas", "per", "persian"),
        ("fi", "fin", null, "finnish"), ("da", "dan", null, "danish"), ("no", "nor", null, "norwegian"),
        ("bg", "bul", null, "bulgarian"), ("hr", "hrv", null, "croatian"), ("sr", "srp", null, "serbian"),
        ("ms", "msa", "may", "malay"), ("ca", "cat", null, "catalan"), ("is", "isl", "ice", "icelandic"),
        ("mk", "mkd", "mac", "macedonian"), ("my", "mya", "bur", "burmese"), ("ka", "kat", "geo", "georgian"),
        ("hy", "hye", "arm", "armenian"), ("eu", "eus", "baq", "basque"), ("cy", "cym", "wel", "welsh"),
        ("sq", "sqi", "alb", "albanian"), ("lt", "lit", null, "lithuanian"), ("lv", "lav", null, "latvian"),
        ("et", "est", null, "estonian"), ("sl", "slv", null, "slovenian"), ("bn", "ben", null, "bengali"),
        ("ta", "tam", null, "tamil"), ("tl", "tgl", null, "tagalog"),
    ];

    private static readonly Lazy<(Dictionary<string, string> Iso3, Dictionary<string, string> Names)> Tables = new(Build);

    private static (Dictionary<string, string>, Dictionary<string, string>) Build()
    {
        var iso3 = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var c in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
            {
                var two = c.TwoLetterISOLanguageName;
                if (two.Length != 2) continue;
                iso3.TryAdd(c.ThreeLetterISOLanguageName.ToLowerInvariant(), two);
                names.TryAdd(c.EnglishName.ToLowerInvariant(), two);
            }
        }
        catch (Exception)
        {
            // Culture data unavailable (e.g. invariant globalization); the static table below still applies.
        }

        foreach (var (two, three, biblio, name) in Known)
        {
            iso3[three] = two;
            if (biblio is not null) iso3[biblio] = two;
            names[name] = two;
        }

        return (iso3, names);
    }

    /// <summary>"en", "eng", "en-US", "pt_BR", "zh-Hans" or "English" to a two-letter code; null if unrecognised.</summary>
    public static string? Normalize(string? tagOrName)
    {
        var s = tagOrName?.Trim().ToLowerInvariant() ?? "";
        if (s.Length == 0 || s == "und") return null;
        var primary = s;
        var i = primary.IndexOf('-');
        if (i >= 0) primary = primary[..i];
        i = primary.IndexOf('_');
        if (i >= 0) primary = primary[..i];
        if (primary.Length == 2 && primary.All(c => c is >= 'a' and <= 'z')) return primary;
        if (primary.Length == 3) return Tables.Value.Iso3.GetValueOrDefault(primary);
        return Tables.Value.Names.GetValueOrDefault(primary);
    }
}
