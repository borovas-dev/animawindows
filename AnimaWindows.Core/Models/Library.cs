using System.Text.Json.Serialization;

namespace AnimaWindows.Core.Models;

/// <summary>Progress of a single episode (exact resume position). Mirrors <c>EpisodeProgress</c> in core/model/Contracts.kt.</summary>
/// <param name="EpisodeNumber">Provider episode number string (see <see cref="Episode.Number"/>).</param>
/// <param name="UpdatedAt">Unix epoch milliseconds.</param>
public sealed record EpisodeProgress(
    string ProviderId,
    string AnimeId,
    string EpisodeNumber,
    long PositionMs,
    long DurationMs,
    long UpdatedAt)
{
    /// <summary>0..1; 0 when the duration is unknown.</summary>
    [JsonIgnore]
    public float Fraction => DurationMs > 0 ? Math.Clamp((float)PositionMs / DurationMs, 0f, 1f) : 0f;

    /// <summary>True once 90% or more was watched.</summary>
    [JsonIgnore]
    public bool IsFinished => Fraction >= 0.9f;
}

/// <summary>One row of "Continue watching" (latest progress per show). Mirrors <c>WatchEntry</c> in core/model/Contracts.kt.</summary>
public sealed record WatchEntry(Anime Anime, EpisodeProgress Last);
