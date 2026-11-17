using System.Globalization;

namespace AnimaWindows.Core.Subtitles;

/// <summary>Parses WebVTT, SRT and ASS/SSA into cues with their timing untouched (text goes through <see cref="CueMarkup"/>). Port of <c>SubtitleParser</c>.</summary>
public static class SubtitleParser
{
    public static IReadOnlyList<SubtitleCue> Parse(string content)
    {
        var text = content.StartsWith('﻿') ? content[1..] : content;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var cues = text.Contains("[Events]", StringComparison.OrdinalIgnoreCase) ? ParseAss(text) : ParseTimed(text);
        return cues.Where(c => c.Text.Length > 0 && c.EndMs > c.StartMs).OrderBy(c => c.StartMs).ToList();
    }

    /// <summary>WebVTT and SRT share the "start --> end" block layout (SRT uses a comma for millis).</summary>
    private static List<SubtitleCue> ParseTimed(string text)
    {
        var output = new List<SubtitleCue>();
        foreach (var block in text.Split("\n\n"))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0) continue;
            var parts = lines[timing].Split("-->");
            var start = ParseTime(parts[0].Trim());
            if (start is null) continue;
            var endRaw = parts[1].Trim();
            var sp = endRaw.IndexOf(' ');
            if (sp >= 0) endRaw = endRaw[..sp];
            var end = ParseTime(endRaw);
            if (end is null) continue;
            var body = string.Join("\n", lines.Skip(timing + 1));
            output.Add(new SubtitleCue(start.Value, end.Value, CueMarkup.Normalize(body)));
        }

        return output;
    }

    private static List<SubtitleCue> ParseAss(string text)
    {
        var inEvents = false;
        IReadOnlyList<string> columns = [];
        var output = new List<SubtitleCue>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inEvents) continue;
            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                columns = line[(line.IndexOf(':') + 1)..].Split(',').Select(c => c.Trim().ToLowerInvariant()).ToList();
            }
            else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) && columns.Count > 0)
            {
                var fields = line[(line.IndexOf(':') + 1)..].TrimStart().Split(',', columns.Count);
                if (fields.Length < columns.Count) continue;
                var si = IndexOf(columns, "start");
                var ei = IndexOf(columns, "end");
                var ti = IndexOf(columns, "text");
                if (si < 0 || ei < 0 || ti < 0) continue;
                var start = ParseTime(fields[si].Trim());
                if (start is null) continue;
                var end = ParseTime(fields[ei].Trim());
                if (end is null) continue;
                var body = fields[ti];
                if (body.Contains("\\p1", StringComparison.Ordinal)) continue; // vector drawing, not text
                output.Add(new SubtitleCue(start.Value, end.Value, CueMarkup.Normalize(body)));
            }
        }

        return output;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
            if (list[i] == value) return i;
        return -1;
    }

    /// <summary><c>[hh:]mm:ss[.,]fff</c> (also ASS <c>h:mm:ss.cc</c>) to milliseconds.</summary>
    internal static long? ParseTime(string s)
    {
        var parts = s.Split(':');
        if (parts.Length is < 2 or > 3) return null;
        var secPart = parts[^1].Replace(',', '.');
        var dot = secPart.IndexOf('.');
        var wholeText = dot < 0 ? secPart : secPart[..dot];
        var frac = dot < 0 ? "" : secPart[(dot + 1)..];
        if (!long.TryParse(wholeText, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole)) return null;
        if (frac.Any(c => !char.IsAsciiDigit(c))) return null;
        var millis = frac.Length == 0 ? 0L : long.Parse((frac + "00")[..3], CultureInfo.InvariantCulture);
        if (!long.TryParse(parts[^2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var min)) return null;
        long hours = 0;
        if (parts.Length == 3 && !long.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out hours)) return null;
        return ((hours * 60 + min) * 60 + whole) * 1000 + millis;
    }
}
