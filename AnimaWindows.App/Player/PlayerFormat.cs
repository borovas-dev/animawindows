using System.Globalization;

namespace AnimaWindows.App.Player;

/// <summary>Pure formatting helpers of the player UI.</summary>
public static class PlayerFormat
{
    /// <summary>"m:ss", or "h:mm:ss" from one hour up. Negative values clamp to zero.</summary>
    public static string Time(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        var totalSeconds = (long)value.TotalSeconds;
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        return h > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{h}:{m:00}:{s:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{m}:{s:00}");
    }

    public static string Time(long milliseconds) => Time(TimeSpan.FromMilliseconds(milliseconds));

    /// <summary>"Show title - Episode 12" (Kotlin <c>titleText</c>).</summary>
    public static string Title(string animeTitle, string episodeNumber) => animeTitle + " - Episode " + episodeNumber;

    /// <summary>"1x", "1.5x", "0.75x".</summary>
    public static string Speed(double speed) => string.Create(CultureInfo.InvariantCulture, $"{speed:0.##}x");

    /// <summary>Subtitle font size in DIPs for a given video-area height: about 4.5 % of the height, kept within a readable range.</summary>
    public static double SubtitleFontSize(double videoHeight) => Math.Clamp(videoHeight * 0.045, 14, 64);

    /// <summary>Clamps a seek target into 0..duration (duration 0 = unknown, only the lower bound applies).</summary>
    public static TimeSpan ClampSeek(TimeSpan target, TimeSpan duration)
    {
        if (target < TimeSpan.Zero) return TimeSpan.Zero;
        return duration > TimeSpan.Zero && target > duration ? duration : target;
    }

    /// <summary>Fraction (0..1) of <paramref name="seconds"/> within <paramref name="durationSeconds"/>; 0 when the duration is unknown.</summary>
    public static double Fraction(double seconds, double durationSeconds) =>
        durationSeconds > 0 ? Math.Clamp(seconds / durationSeconds, 0, 1) : 0;
}
