namespace AnimaWindows.Core.Models;

// JSON-compatible with core/database/BackupManager.kt so backups can move between the Android app and this one.

/// <summary>Mirrors <c>BackupAnime</c> in core/database/BackupManager.kt.</summary>
public sealed record BackupAnime
{
    public required string ProviderId { get; init; }
    public required string AnimeId { get; init; }
    public required string Title { get; init; }
    public string? CoverUrl { get; init; }
    public string? Synopsis { get; init; }
    public int? MalId { get; init; }
    public int? SubEpisodes { get; init; }
    public int? DubEpisodes { get; init; }
    public bool IsFavorite { get; init; }
    public long? FavoritedAt { get; init; }
    public long? LastWatchedAt { get; init; }

    /// <summary>providerId to animeId on other providers (absent in pre-0.2 backups).</summary>
    public IReadOnlyDictionary<string, string> Alternates { get; init; } = new Dictionary<string, string>();
}

/// <summary>Mirrors <c>BackupProgress</c> in core/database/BackupManager.kt.</summary>
public sealed record BackupProgress(string ProviderId, string AnimeId, string EpisodeNumber, long PositionMs, long DurationMs, long UpdatedAt);

/// <summary>Mirrors <c>BackupSearch</c> in core/database/BackupManager.kt.</summary>
public sealed record BackupSearch(string Query, long SearchedAt);

/// <summary>Mirrors <c>BackupFile</c> in core/database/BackupManager.kt.</summary>
public sealed record BackupFile
{
    public int Version { get; init; } = 1;
    public IReadOnlyList<BackupAnime> Anime { get; init; } = [];
    public IReadOnlyList<BackupProgress> Progress { get; init; } = [];
    public IReadOnlyList<BackupSearch> Searches { get; init; } = [];

    /// <summary>Opaque settings JSON produced by <c>ISettingsStore.ExportJsonAsync</c> (optional).</summary>
    public string? Settings { get; init; }
}

/// <summary>Counts of imported rows. Mirrors <c>ImportResult</c> in core/database/BackupManager.kt.</summary>
public sealed record ImportResult(int Anime, int Progress, int Searches, string? SettingsJson);
