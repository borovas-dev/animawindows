using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Read side of the remote provider configuration, as providers consume it (base URLs, mirrors, free-form params, kill
/// switch). Mirrors <c>RemoteConfigRepository.forProvider</c> in core/remoteconfig/RemoteConfigRepository.kt. Providers must call
/// <see cref="ForProvider"/> on every request (not cache it in a field) so mirror changes apply without restart.
/// </summary>
public interface IProviderConfig
{
    /// <summary>
    /// Config for provider <paramref name="id"/> with null-safe defaults (<see cref="ProviderRemoteConfig.Default"/> when absent).
    /// <see cref="ProviderRemoteConfig.Enabled"/> is already false when this app is older than <see cref="ProviderRemoteConfig.MinAppVersion"/>.
    /// Synchronous and non-throwing.
    /// </summary>
    ProviderRemoteConfig ForProvider(string id);

    /// <summary>Raised (on any thread) after the active document changed, e.g. after a successful refresh.</summary>
    event EventHandler? Changed;
}

/// <summary>
/// Owner of the remote config document: bundled copy, signed/cached remote copy, refresh. Mirrors
/// <c>RemoteConfigRepository</c> in core/remoteconfig/RemoteConfigRepository.kt and the constants in RemoteConfigKeys.kt.
/// </summary>
public interface IRemoteConfigSource : IProviderConfig
{
    /// <summary>Active document: the newest valid one among bundled and cached copies (higher <see cref="RemoteConfigDocument.Revision"/> wins; never rolls back).</summary>
    RemoteConfigDocument Current { get; }

    /// <summary>
    /// Fetches the remote document (ETag-conditional), verifies it (Ed25519 signature unless unsigned configs are allowed; a
    /// present-but-wrong signature is always rejected), persists it atomically and raises <see cref="IProviderConfig.Changed"/>.
    /// Returns true when a newer document was adopted. Never throws for network/verification problems (returns false).
    /// </summary>
    Task<bool> RefreshAsync(CancellationToken cancellationToken = default);
}
