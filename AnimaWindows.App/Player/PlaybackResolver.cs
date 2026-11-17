using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.App.Player;

/// <summary>Streams ready to play plus where they came from.</summary>
/// <param name="Set">What to play (local <c>file:</c> URIs when <paramref name="IsOffline"/>).</param>
/// <param name="SourceLabel">Display name of the serving provider; null offline.</param>
/// <param name="IsFallback">The chain failed over to a provider other than the show's own.</param>
/// <param name="IsOffline">Plays a completed download.</param>
public sealed record ResolvedPlayback(StreamSet Set, string? SourceLabel, bool IsFallback, bool IsOffline);

/// <summary>Stream resolution of the player: a finished download wins, otherwise the provider chain.</summary>
public sealed class PlaybackResolver
{
    private readonly IProviderChain _chain;
    private readonly IDownloadRepository _downloads;
    private readonly ISettingsStore _settings;

    public PlaybackResolver(IProviderChain chain, IDownloadRepository downloads, ISettingsStore settings)
    {
        _chain = chain;
        _downloads = downloads;
        _settings = settings;
    }

    /// <summary>The completed download of <paramref name="episode"/>, or null.</summary>
    public async Task<ResolvedPlayback?> TryOfflineAsync(string providerId, Anime anime, Episode episode, CancellationToken cancellationToken)
    {
        var item = await _downloads.GetAsync(providerId, anime.Id, episode.Number, cancellationToken);
        return item is { IsPlayable: true, StreamSet: { } set } ? new ResolvedPlayback(set, null, false, true) : null;
    }

    /// <summary>Resolves streams through the provider chain for the current translation mode.</summary>
    public async Task<ResolvedPlayback> ResolveOnlineAsync(Anime anime, Episode episode, CancellationToken cancellationToken)
    {
        var resolved = await _chain.StreamsAsync(anime, episode, _settings.Current.Mode, cancellationToken);
        var label = _chain.Provider(resolved.ProviderId)?.DisplayName ?? resolved.ProviderId;
        return new ResolvedPlayback(resolved.Set, label, resolved.IsFallback, false);
    }
}
