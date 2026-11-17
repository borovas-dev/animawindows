using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Persistence of offline downloads (episode granularity). Mirrors <c>DownloadRepository</c> in core/model/Downloads.kt and
/// <c>RoomDownloadRepository</c>/<c>DownloadDao</c> in core/database. Implemented in <c>AnimaWindows.Data</c>.
/// See <see cref="ILibraryRepository"/> for the observation convention of the <c>Observe*</c> methods.
/// The StreamSet snapshot is stored as JSON using <c>CoreJsonContext</c>.
/// </summary>
public interface IDownloadRepository
{
    /// <summary>All downloads, newest <see cref="DownloadItem.CreatedAt"/> first.</summary>
    IAsyncEnumerable<IReadOnlyList<DownloadItem>> ObserveDownloads(CancellationToken cancellationToken = default);

    /// <summary>Grouped by anime, groups newest download first; episodes within a group sorted by numeric episode number (non-numeric last, then ordinal).</summary>
    IAsyncEnumerable<IReadOnlyList<DownloadGroup>> ObserveGroupedByAnime(CancellationToken cancellationToken = default);

    /// <summary>Episode number to download for one anime.</summary>
    IAsyncEnumerable<IReadOnlyDictionary<string, DownloadItem>> ObserveForAnime(string providerId, string animeId, CancellationToken cancellationToken = default);

    /// <summary>One download (null when absent).</summary>
    IAsyncEnumerable<DownloadItem?> Observe(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);

    Task<DownloadItem?> GetAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DownloadItem>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Inserts or replaces the row keyed by (providerId, animeId, episodeNumber).</summary>
    Task UpsertAsync(DownloadItem item, CancellationToken cancellationToken = default);

    /// <summary>Updates state/bytes/percent/failureReason only; no-op when the row does not exist.</summary>
    Task UpdateProgressAsync(
        string providerId, string animeId, string episodeNumber,
        DownloadState state, long bytes, float percent,
        string? failureReason = null,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes the row (not the files; <see cref="IDownloadController.RemoveAsync"/> does both).</summary>
    Task DeleteAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);
}

/// <summary>
/// Queues and controls downloads. Mirrors <c>DownloadController</c> in core/model/Downloads.kt. Implemented in
/// <c>AnimaWindows.Core/Downloads</c> (HTTP + files) on top of <see cref="IDownloadRepository"/>.
/// </summary>
public interface IDownloadController
{
    /// <summary>Resolves streams (quality = user's preference) then queues the download. Never throws (failures become a Failed row).</summary>
    Task EnqueueAsync(Anime anime, Episode episode, TranslationMode mode, CancellationToken cancellationToken = default);

    /// <summary>Queues several episodes one after another (keeps provider load low); continues after the caller leaves.</summary>
    Task EnqueueAllAsync(Anime anime, IReadOnlyList<Episode> episodes, TranslationMode mode, CancellationToken cancellationToken = default);

    Task PauseAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);

    Task ResumeAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);

    /// <summary>Cancels an active download or deletes a finished one, including its files on disk and its row.</summary>
    Task RemoveAsync(string providerId, string animeId, string episodeNumber, CancellationToken cancellationToken = default);
}
