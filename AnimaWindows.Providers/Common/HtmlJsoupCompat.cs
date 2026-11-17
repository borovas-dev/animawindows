using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace AnimaWindows.Providers.Common;

/// <summary>
/// Small helpers that give AngleSharp the Jsoup semantics the Kotlin providers rely on (<c>attr()</c> returns "" for a missing
/// attribute, <c>text()</c> normalises whitespace). Also string/number helpers shared by the English providers.
/// </summary>
internal static partial class HtmlJsoupCompat
{
    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>Jsoup <c>Jsoup.parse(html)</c>.</summary>
    public static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    /// <summary>Jsoup <c>Element.attr(name)</c>: the attribute value, or "" when absent.</summary>
    public static string Attr(this IElement element, string name) => element.GetAttribute(name) ?? "";

    /// <summary>Jsoup <c>Element.text()</c>: all descendant text with whitespace runs collapsed and trimmed.</summary>
    public static string JsoupText(this IElement element) => WhitespaceRegex().Replace(element.TextContent, " ").Trim();

    /// <summary>Kotlin <c>String.toDoubleOrNull()</c> (invariant culture).</summary>
    public static double? ToDoubleOrNull(string? s) =>
        s is not null && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;

    /// <summary>Kotlin <c>substringBefore(delimiter)</c> (whole string when absent).</summary>
    public static string SubstringBefore(this string s, char delimiter)
    {
        var i = s.IndexOf(delimiter);
        return i < 0 ? s : s[..i];
    }

    /// <summary>Kotlin <c>substringAfterLast(delimiter)</c> (whole string when absent).</summary>
    public static string SubstringAfterLast(this string s, char delimiter)
    {
        var i = s.LastIndexOf(delimiter);
        return i < 0 ? s : s[(i + 1)..];
    }
}
