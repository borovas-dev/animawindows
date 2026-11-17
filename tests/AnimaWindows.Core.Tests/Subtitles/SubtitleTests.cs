#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Subtitles;

namespace AnimaWindows.Core.Tests.Subtitles;

public class SubtitleParserTests
{
    [Fact]
    public void VttKeepsExactTiming()
    {
        var vtt = "﻿WEBVTT\n\nNOTE comment\n\n1\n00:00:01.500 --> 00:00:03.250 align:start\n<i>Hello</i>\nworld\n\n01:02:03.004 --> 01:02:04.000\n<c.yellow>Bye</c>\n";
        var cues = SubtitleParser.Parse(vtt);
        Assert.Equal([new SubtitleCue(1500, 3250, "<i>Hello</i>\nworld"), new SubtitleCue(3_723_004, 3_724_000, "Bye")], cues);
    }

    [Fact]
    public void SrtCommaMillisAndCrlf()
    {
        var srt = "1\r\n00:00:00,040 --> 00:00:02,000\r\n{\\an8}Top line\r\n\r\n2\r\n00:00:02,500 --> 00:00:04,000\r\nTwo\r\n";
        var cues = SubtitleParser.Parse(srt);
        Assert.Equal(40L, cues[0].StartMs);
        Assert.Equal("Top line", cues[0].Text);
        Assert.Equal(2500L, cues[1].StartMs);
    }

    [Fact]
    public void AssStripsOverridesAndKeepsCommasInText()
    {
        var ass = string.Join("\n",
            "[Script Info]",
            "Title: x",
            "",
            "[Events]",
            "Format: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text",
            "Dialogue: 0,0:00:05.10,0:00:07.00,Default,,0,0,0,,{\\i1\\pos(10,20)}Well, hello\\Nthere{\\i0}",
            "Dialogue: 0,0:00:01.00,0:00:02.00,Default,,0,0,0,,{\\p1}m 0 0 l 10 10{\\p0}",
            "Dialogue: 0,0:00:03.00,0:00:04.00,Default,,0,0,0,,Plain");
        var cues = SubtitleParser.Parse(ass);
        Assert.Equal([new SubtitleCue(3000, 4000, "Plain"), new SubtitleCue(5100, 7000, "Well, hello\nthere")], cues);
    }

    [Fact]
    public void TimeParsing()
    {
        Assert.Equal(61_500L, SubtitleParser.ParseTime("01:01.500"));
        Assert.Equal(3_600_000L + 5_000L + 120L, SubtitleParser.ParseTime("1:00:05.12"));
        Assert.Null(SubtitleParser.ParseTime("garbage"));
    }

    [Fact]
    public void EmptyOrBrokenInputGivesNoCues()
    {
        Assert.Empty(SubtitleParser.Parse(""));
        Assert.Empty(SubtitleParser.Parse("WEBVTT\n\nnot a cue"));
    }
}

public class CueMarkupTests
{
    [Fact]
    public void KeepsBasicTagsDropsRest() =>
        Assert.Equal("<i>a</i> <b>b</b>", CueMarkup.Normalize("<I>a</I> <b>b</b><font color=\"red\"></font><00:00:01.000>"));

    [Fact]
    public void AssOverrideCodesAndBreaks() =>
        Assert.Equal("one\ntwo three", CueMarkup.Normalize("{\\an8\\b1}one\\Ntwo\\hthree{\\r}"));

    [Fact]
    public void LiteralBracesWithoutBackslashSurvive() => Assert.Equal("a {b} c", CueMarkup.Normalize("a {b} c"));

    [Fact]
    public void EntitiesAndWhitespace() => Assert.Equal("Tom & Jerry <3", CueMarkup.Normalize("  Tom &amp; Jerry &lt;3 \n\n"));

    [Fact]
    public void StyledTextParsesSpans()
    {
        var s = StyledText.Parse("a <i>b</i> <b><u>c</u></b>");
        Assert.Equal("a b c", s.Text);
        Assert.Equal(
            new HashSet<StyledText.Span>
            {
                new(StyledText.Kind.Italic, 2, 3),
                new(StyledText.Kind.Underline, 4, 5),
                new(StyledText.Kind.Bold, 4, 5),
            },
            s.Spans.ToHashSet());
    }

    [Fact]
    public void PlainStripsTags() => Assert.Equal("a b", CueMarkup.Plain("<i>a</i> <b>b</b>"));
}

public class CueTimelineTests
{
    [Fact]
    public void FindsActiveCuesIncludingOverlaps()
    {
        var t = new CueTimeline([
            new SubtitleCue(5000, 6000, "c"),
            new SubtitleCue(0, 10_000, "long"),
            new SubtitleCue(2000, 3000, "b"),
        ]);
        Assert.Empty(t.ActiveAt(-1));
        Assert.Equal(["long"], t.ActiveAt(0).Select(c => c.Text));
        Assert.Equal(["long", "b"], t.ActiveAt(2500).Select(c => c.Text));
        Assert.Equal(["long"], t.ActiveAt(3000).Select(c => c.Text)); // end exclusive
        Assert.Equal(["long", "c"], t.ActiveAt(5000).Select(c => c.Text));
        Assert.Empty(t.ActiveAt(10_000));
        Assert.Equal("b", t.CurrentAt(2500)!.Text);
        Assert.Null(t.CurrentAt(11_000));
    }

    [Fact]
    public void EmptyTimeline()
    {
        var t = new CueTimeline([]);
        Assert.Empty(t.ActiveAt(0));
        Assert.Null(t.CurrentAt(0));
    }
}

public class TrackPickerTests
{
    private static SubtitleTrack T(string? lang, string? label, bool def = false) => new($"https://x/{label}.vtt", lang, label, def);

    [Fact]
    public void LanguageNormalisation()
    {
        Assert.Equal("en", SubtitleLanguages.Normalize("eng"));
        Assert.Equal("zh", SubtitleLanguages.Normalize("zh-Hans"));
        Assert.Equal("zh", SubtitleLanguages.Normalize("chi"));
        Assert.Equal("pl", SubtitleLanguages.Normalize("pol"));
        Assert.Equal("pt", SubtitleLanguages.Normalize("pt_BR"));
        Assert.Equal("en", SubtitleLanguages.Normalize("English"));
        Assert.Null(SubtitleLanguages.Normalize("und"));
        Assert.Null(SubtitleLanguages.Normalize(null));
    }

    [Fact]
    public void HardsubInPreferredLanguageSkipsEverything()
    {
        Assert.Null(TrackPicker.Pick([T("zh", "Chinese")], "zh", "zh-Hans"));
        Assert.NotNull(TrackPicker.Pick([T("zh", "Chinese")], "zh", "pl"));
    }

    [Fact]
    public void ProvidedTrackInPreferredLanguageWins()
    {
        var zh = T("zh", "Chinese");
        Assert.Equal(zh, TrackPicker.Pick([T("en", "English"), zh], "zh"));
    }

    [Fact]
    public void LabelOnlyTracksAreRecognised()
    {
        var pl = T(null, "Polish");
        Assert.Equal(pl, TrackPicker.Pick([pl], "pl"));
    }

    [Fact]
    public void NoMatchUnsetLanguageOrThumbnails()
    {
        Assert.Null(TrackPicker.Pick([T("en", "English")], "zh"));
        Assert.Null(TrackPicker.Pick([T("en", "English")], ""));
        Assert.Null(TrackPicker.Pick([T("pl", "thumbnails")], "pl"));
    }
}
