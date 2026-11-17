using System.Text;

namespace AnimaWindows.Core.Subtitles;

/// <summary>
/// Normalises raw cue text from WebVTT / SRT / ASS into plain text with <c>\n</c> breaks and only <c>&lt;i&gt;</c>, <c>&lt;b&gt;</c>,
/// <c>&lt;u&gt;</c>. Scanned by hand (same as the Kotlin original). Port of <c>CueMarkup</c>.
/// </summary>
public static class CueMarkup
{
    private static readonly HashSet<string> BasicTags = ["i", "b", "u", "/i", "/b", "/u"];

    public static string Normalize(string raw)
    {
        var output = new StringBuilder(raw.Length);
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == '{' && i + 1 < raw.Length && raw[i + 1] == '\\')
            {
                // ASS / SSA override block such as {\an8} or {\i1\pos(1,2)}; also used inside SRT.
                var end = raw.IndexOf('}', i);
                i = end < 0 ? raw.Length : end + 1;
            }
            else if (c == '\\' && i + 1 < raw.Length && (raw[i + 1] == 'N' || raw[i + 1] == 'n'))
            {
                output.Append('\n');
                i += 2;
            }
            else if (c == '\\' && i + 1 < raw.Length && raw[i + 1] == 'h')
            {
                output.Append(' ');
                i += 2;
            }
            else if (c == '<')
            {
                var end = raw.IndexOf('>', i);
                if (end < 0)
                {
                    output.Append(c);
                    i++;
                }
                else
                {
                    var name = raw.Substring(i + 1, end - i - 1).Trim().ToLowerInvariant();
                    if (BasicTags.Contains(name)) output.Append('<').Append(name).Append('>');
                    i = end + 1;
                }
            }
            else if (c == '&')
            {
                var end = raw.IndexOf(';', i);
                string? entity = end >= i + 1 && end <= i + 7 ? raw.Substring(i + 1, end - i - 1) : null;
                var decoded = entity switch
                {
                    "amp" => "&",
                    "lt" => "<",
                    "gt" => ">",
                    "quot" => "\"",
                    "apos" => "'",
                    "nbsp" => " ",
                    "lrm" or "rlm" => "",
                    _ => null,
                };
                if (decoded is not null)
                {
                    output.Append(decoded);
                    i = end + 1;
                }
                else
                {
                    output.Append(c);
                    i++;
                }
            }
            else if (c == '\r')
            {
                i++;
            }
            else
            {
                output.Append(c);
                i++;
            }
        }

        return string.Join("\n", output.ToString().Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
    }

    /// <summary>Text without any tags, for measuring and tests.</summary>
    public static string Plain(string text) => StripTags(text);

    internal static string StripTags(string text)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < text.Length)
        {
            var tag = TagAt(text, i);
            if (tag is not null) i += tag.Length;
            else sb.Append(text[i++]);
        }

        return sb.ToString();
    }

    /// <summary>The basic tag starting at <paramref name="i"/> (e.g. <c>&lt;i&gt;</c> or <c>&lt;/b&gt;</c>), or null.</summary>
    internal static string? TagAt(string text, int i)
    {
        if (text[i] != '<') return null;
        var end = text.IndexOf('>', i);
        if (end < 0 || end - i > 4) return null;
        var tag = text.Substring(i, end - i + 1);
        return BasicTags.Contains(tag[1..^1]) ? tag : null;
    }
}

/// <summary>A run of text with basic styling. Port of <c>StyledText</c>.</summary>
public sealed record StyledText(string Text, IReadOnlyList<StyledText.Span> Spans)
{
    public enum Kind
    {
        Italic,
        Bold,
        Underline,
    }

    public sealed record Span(Kind Kind, int Start, int End);

    /// <summary>Parses normalised markup (see <see cref="CueMarkup"/>) into plain text plus style ranges.</summary>
    public static StyledText Parse(string markup)
    {
        var sb = new StringBuilder();
        var open = new Dictionary<Kind, int>();
        var spans = new List<Span>();
        var i = 0;
        while (i < markup.Length)
        {
            var tag = CueMarkup.TagAt(markup, i);
            if (tag is null)
            {
                sb.Append(markup[i++]);
                continue;
            }

            i += tag.Length;
            var closing = tag[1] == '/';
            var kind = tag.Trim('<', '>', '/') switch
            {
                "i" => Kind.Italic,
                "b" => Kind.Bold,
                _ => Kind.Underline,
            };
            if (closing)
            {
                if (open.Remove(kind, out var start) && sb.Length > start) spans.Add(new Span(kind, start, sb.Length));
            }
            else
            {
                open[kind] = sb.Length;
            }
        }

        foreach (var (kind, start) in open)
            if (sb.Length > start) spans.Add(new Span(kind, start, sb.Length));
        return new StyledText(sb.ToString(), spans);
    }
}
