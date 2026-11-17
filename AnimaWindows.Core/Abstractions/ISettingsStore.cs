using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Persistent, observable <see cref="AppSettings"/>. Mirrors <c>SettingsRepository</c> in core/model/Contracts.kt and
/// <c>DataStoreSettingsRepository</c> in core/datastore. Implemented in <c>AnimaWindows.Data</c> (JSON file in <c>IAppPaths.DataDirectory</c>,
/// written atomically via temp file + rename, serialised with <c>CoreJsonContext</c>).
/// </summary>
public interface ISettingsStore
{
    /// <summary>
    /// Latest settings. Before <see cref="LoadAsync"/> completes this is <see cref="AppSettings.Fresh"/>. Safe to read from any thread.
    /// </summary>
    AppSettings Current { get; }

    /// <summary>
    /// Raised after every committed change, with the new value, on the thread that performed the update (not the UI thread).
    /// Not raised when the transform returns an equal value.
    /// </summary>
    event EventHandler<AppSettings>? Changed;

    /// <summary>
    /// Reads the file once at startup. A missing, empty or corrupt file yields <see cref="AppSettings.Fresh"/> (corruption replaces
    /// the file). Applies the one-time v2 migration (legacy ProviderId: "shinden" gives "pl", "hianime" gives "en", unless
    /// ContentLanguage is already set; then MigratedV2 = true). Idempotent.
    /// </summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically applies <paramref name="transform"/> to the latest value (serialised against other updates), persists the
    /// result and raises <see cref="Changed"/>. The transform must be pure and fast.
    /// </summary>
    Task UpdateAsync(Func<AppSettings, AppSettings> transform, CancellationToken cancellationToken = default);

    /// <summary>Current settings as JSON (camelCase, defaults included, same format as the file) for <see cref="BackupFile.Settings"/>.</summary>
    Task<string> ExportJsonAsync(CancellationToken cancellationToken = default);

    /// <summary>Replaces the settings with imported JSON (unknown fields ignored, v2 migration applied) and raises <see cref="Changed"/>. Throws <see cref="System.Text.Json.JsonException"/> on invalid input.</summary>
    Task ImportJsonAsync(string json, CancellationToken cancellationToken = default);
}
