using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Backup/restore of library and settings as <see cref="BackupFile"/> JSON. Mirrors <c>BackupManager</c> in
/// core/database/BackupManager.kt. Implemented in <c>AnimaWindows.Data</c> (uses <c>CoreJsonContext</c>; output indented).
/// </summary>
public interface IBackupService
{
    /// <summary>Writes all anime, progress, searches and optionally <paramref name="settingsJson"/> to <paramref name="output"/>.</summary>
    Task ExportAsync(Stream output, string? settingsJson = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Merges the backup into the database in one transaction (upsert; existing rows with a newer updatedAt win for progress)
    /// and returns the counts. Throws <see cref="System.Text.Json.JsonException"/> for invalid input. Does not touch settings:
    /// the caller applies <see cref="ImportResult.SettingsJson"/> via <see cref="ISettingsStore.ImportJsonAsync"/>.
    /// </summary>
    Task<ImportResult> ImportAsync(Stream input, CancellationToken cancellationToken = default);
}
