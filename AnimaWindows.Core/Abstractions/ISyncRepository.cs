using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Tracker sync (AniList, MyAnimeList). Mirrors <c>SyncRepository</c> in core/sync/SyncApi.kt. Implemented in
/// <c>AnimaWindows.Core/Tracking</c>; tokens live in <see cref="ISecretStore"/>, the pending queue in a JSON file in
/// <c>IAppPaths.DataDirectory</c> (no secrets).
/// </summary>
public interface ISyncRepository
{
    /// <summary>Latest state; never null.</summary>
    SyncState State { get; }

    /// <summary>Raised after every state change (any thread).</summary>
    event EventHandler<SyncState>? StateChanged;

    /// <summary>URL to open in the system browser (PKCE), or null when <paramref name="service"/> is not configured.</summary>
    string? AuthorizeUrl(SyncService service);

    /// <summary>
    /// Handles <c>animawindows://auth/...</c> redirects (protocol activation). Returns true when <paramref name="uri"/> was an auth
    /// redirect (successful or not), false when it should be handled elsewhere.
    /// </summary>
    Task<bool> HandleRedirectAsync(string uri, CancellationToken cancellationToken = default);

    /// <summary>Forgets the tokens of <paramref name="service"/>.</summary>
    Task DisconnectAsync(SyncService service, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fire-and-forget: queues the update for every connected service and tries to deliver it. Coalesces per MAL id keeping
    /// the furthest episode. Ignored when the anime has no MalId or the episode number is not an integer.
    /// </summary>
    void OnEpisodeWatched(Anime anime, string episodeNumber, int? totalEpisodes);

    /// <summary>Retries everything queued. Never throws; failures go to <see cref="SyncState.Error"/>.</summary>
    Task SyncNowAsync(CancellationToken cancellationToken = default);
}
