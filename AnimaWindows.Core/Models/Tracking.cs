using System.Collections.Immutable;

namespace AnimaWindows.Core.Models;

/// <summary>
/// OAuth client ids. Blank = not configured. Mirrors <c>SyncConfig</c> in core/sync/SyncApi.kt.
/// </summary>
public sealed record SyncConfig(string AnilistClientId = "", string MalClientId = "")
{
    public string ClientId(SyncService service) => service == SyncService.AniList ? AnilistClientId : MalClientId;
    public bool IsConfigured(SyncService service) => !string.IsNullOrWhiteSpace(ClientId(service));
}

/// <summary>Mirrors <c>ServiceState</c> in core/sync/SyncApi.kt.</summary>
public sealed record ServiceState(bool Configured = false, bool Connected = false, string? Username = null);

/// <summary>Mirrors <c>SyncState</c> in core/sync/SyncApi.kt.</summary>
/// <param name="Services">State per tracker; always contains every <see cref="SyncService"/>.</param>
/// <param name="Pending">Queued (not yet delivered) updates.</param>
/// <param name="Syncing">A delivery run is in progress.</param>
/// <param name="Error">Last failure, human readable (not localized); null when the last run was fine.</param>
public sealed record SyncState(
    IReadOnlyDictionary<SyncService, ServiceState> Services,
    int Pending = 0,
    bool Syncing = false,
    string? Error = null)
{
    /// <summary>Initial state: every service present and unconfigured.</summary>
    public static SyncState Initial { get; } = new(
        Enum.GetValues<SyncService>().ToImmutableDictionary(s => s, _ => new ServiceState()));
}

/// <summary>OAuth tokens of one service. Mirrors <c>Tokens</c> in core/sync/Storage.kt. Stored only via <c>ISecretStore</c>.</summary>
/// <param name="ExpiresAt">Unix epoch milliseconds; 0 = unknown.</param>
public sealed record Tokens(string Access, string? Refresh = null, long ExpiresAt = 0, string? Username = null);

/// <summary>
/// One show with the furthest episode watched, still to be delivered to <see cref="Services"/>. Keyed by MAL id so updates
/// coalesce. Mirrors <c>PendingUpdate</c> in core/sync/Storage.kt.
/// </summary>
public sealed record PendingUpdate(int MalId, int Episode, int? Total, ImmutableHashSet<SyncService> Services);
