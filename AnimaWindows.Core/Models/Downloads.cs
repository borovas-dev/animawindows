using System.Text.Json.Serialization;

namespace AnimaWindows.Core.Models;

/// <summary>
/// One downloaded (or downloading) episode. Mirrors <c>DownloadItem</c> in core/model/Downloads.kt.
/// <see cref="StreamSet"/> is a snapshot of what was actually downloaded; playing it back must work without any provider call.
/// </summary>
/// <param name="EpisodeId">Provider-specific episode id the stream was resolved with.</param>
/// <param name="EpisodeNumber">Episode number string; part of the identity (<see cref="Key"/>).</param>
/// <param name="Title">Anime title.</param>
/// <param name="Quality">Height of the downloaded variant, when known.</param>
/// <param name="Percent">0..1.</param>
/// <param name="CreatedAt">Unix epoch milliseconds.</param>
/// <param name="FailureReason">Human-readable reason for Failed, or a note such as "Paused by system" for Paused.</param>
public sealed record DownloadItem(
    string ProviderId,
    string AnimeId,
    string EpisodeId,
    string EpisodeNumber,
    string Title,
    string? CoverUrl = null,
    TranslationMode Mode = TranslationMode.Sub,
    int? Quality = null,
    DownloadState State = DownloadState.Queued,
    long Bytes = 0,
    float Percent = 0f,
    long CreatedAt = 0,
    StreamSet? StreamSet = null,
    string? FailureReason = null)
{
    /// <summary>Identity: <c>providerId/animeId/episodeNumber</c>.</summary>
    [JsonIgnore]
    public string Key => $"{ProviderId}/{AnimeId}/{EpisodeNumber}";

    /// <summary>Completed and replayable offline.</summary>
    [JsonIgnore]
    public bool IsPlayable => State == DownloadState.Completed && StreamSet is not null;
}

/// <summary>All downloads of one anime, for the grouped downloads screen. Mirrors <c>DownloadGroup</c> in core/model/Downloads.kt.</summary>
public sealed record DownloadGroup(string ProviderId, string AnimeId, string Title, string? CoverUrl, IReadOnlyList<DownloadItem> Items)
{
    public long TotalBytes => Items.Sum(i => i.Bytes);
}

/// <summary>
/// Download request metadata: how the downloader must talk to the CDN. Mirrors <c>DownloadPayload</c> in
/// feature/downloads/DownloadPlanning.kt.
/// </summary>
public sealed record DownloadPayload
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
    public string Label { get; init; } = "";
}

/// <summary>One part (main stream, external audio, subtitle) of a download. Mirrors <c>PartRequest</c> in feature/downloads/DownloadPlanning.kt.</summary>
public sealed record PartRequest(string Id, string Url, bool IsHls);
