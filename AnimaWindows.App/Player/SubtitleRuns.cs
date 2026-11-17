using AnimaWindows.Core.Subtitles;

namespace AnimaWindows.App.Player;

/// <summary>A run of subtitle text with uniform basic styling.</summary>
public sealed record SubtitleRun(string Text, bool Italic, bool Bold, bool Underline);

/// <summary>Flattens normalised cue markup (<c>&lt;i&gt;</c>, <c>&lt;b&gt;</c>, <c>&lt;u&gt;</c>) into runs for a <c>TextBlock</c>.</summary>
public static class SubtitleRuns
{
    public static IReadOnlyList<SubtitleRun> Build(string markup)
    {
        var styled = StyledText.Parse(markup);
        var text = styled.Text;
        if (text.Length == 0) return [];

        var flags = new int[text.Length];
        foreach (var span in styled.Spans)
        {
            var bit = span.Kind switch
            {
                StyledText.Kind.Italic => 1,
                StyledText.Kind.Bold => 2,
                _ => 4,
            };
            var end = Math.Min(text.Length, span.End);
            for (var i = Math.Max(0, span.Start); i < end; i++) flags[i] |= bit;
        }

        var runs = new List<SubtitleRun>();
        var start = 0;
        for (var i = 1; i <= text.Length; i++)
        {
            if (i < text.Length && flags[i] == flags[start]) continue;
            var f = flags[start];
            runs.Add(new SubtitleRun(text[start..i], (f & 1) != 0, (f & 2) != 0, (f & 4) != 0));
            start = i;
        }

        return runs;
    }
}
