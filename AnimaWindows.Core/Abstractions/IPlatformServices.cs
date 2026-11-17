namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// Solves Cloudflare challenges in an embedded WebView2 and shares the clearance cookie with the HTTP stack. Mirrors the
/// Android <c>CloudflareSolver</c> composable (core/network/CloudflareSolver.kt), <c>WebViewCookieJar</c> and the
/// <c>CloudflareSolved</c> signal in provider/api/AnimeProvider.kt. Implemented in the App project.
/// </summary>
public interface ICloudflareSolver
{
    /// <summary>
    /// Loads <paramref name="url"/> in WebView2 with the user agent <c>NetworkConstants.AniCliUserAgent</c>, polling until the
    /// <c>cf_clearance</c> cookie for the host exists (poll 500 ms). Copies the cookies into the shared cookie container used by
    /// HTTP clients before returning. Returns true when solved, false on timeout (default 120 s) or when the user closes the UI.
    /// Raises <see cref="Solved"/> on success. Must be callable from any thread (marshals to the UI thread itself).
    /// </summary>
    Task<bool> SolveAsync(Uri url, TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Process-wide signal raised once a clearance was obtained, so screens whose request failed with
    /// <c>CloudflareChallengeException</c> can retry automatically (Kotlin <c>CloudflareSolved.events</c>).
    /// </summary>
    event EventHandler? Solved;
}

/// <summary>
/// Encrypted per-user storage for tokens and other secrets (Windows: DPAPI-protected file or PasswordVault). Mirrors
/// <c>TokenStore</c>/<c>KeystoreTokenStore</c> in core/sync/Storage.kt, generalised to string values. Secrets are never part of
/// settings JSON or backups. If a stored value cannot be decrypted it reads as absent.
/// </summary>
public interface ISecretStore
{
    /// <summary>Value for <paramref name="key"/>, or null when absent/undecryptable. Keys are lowercase ascii, e.g. "sync.anilist".</summary>
    string? Get(string key);

    /// <summary>Stores or replaces a value. Throws <see cref="IOException"/> on persistence failure.</summary>
    void Set(string key, string value);

    /// <summary>Removes the value; no-op when absent.</summary>
    void Remove(string key);
}

/// <summary>Coarse network cost class.</summary>
public enum NetworkCost
{
    /// <summary>No usable connection.</summary>
    Offline,

    /// <summary>Connected and unmetered (Android "Wi-Fi").</summary>
    Unmetered,

    /// <summary>Connected on a metered connection (cellular, or Windows "metered connection" flag).</summary>
    Metered,
}

/// <summary>
/// Reports connectivity and metering so downloads and prefetching can honour <c>DownloadWifiOnly</c> / <c>PrefetchOnMetered</c> /
/// <c>ScrubPreviewMobileData</c>. Mirrors the Android connectivity/Requirements checks in feature/downloads/DownloadPolicy.kt.
/// Implemented in the App project with <c>Windows.Networking.Connectivity</c>.
/// </summary>
public interface INetworkCostProvider
{
    /// <summary>Current cost class. Synchronous, cheap, thread-safe.</summary>
    NetworkCost Current { get; }

    /// <summary>Raised when <see cref="Current"/> changes (any thread).</summary>
    event EventHandler<NetworkCost>? Changed;
}

/// <summary>Time source, injectable for tests.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>Unix epoch milliseconds (the unit of every persisted timestamp, Kotlin <c>System.currentTimeMillis()</c>).</summary>
    long UnixMilliseconds => UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>Well-known directories. All paths are absolute; directories exist after first access.</summary>
public interface IAppPaths
{
    /// <summary>Persistent app data (database, settings.json, queues). Packaged: ApplicationData.LocalFolder.</summary>
    string DataDirectory { get; }

    /// <summary>Disposable cache (remote config cache, update packages, thumbnails). Packaged: ApplicationData.LocalCacheFolder.</summary>
    string CacheDirectory { get; }

    /// <summary>
    /// Default location of downloaded episodes. Overridden by <c>AppSettings.DownloadDirectory</c> when set; implementations of
    /// the download feature must resolve the effective directory through the settings first.
    /// </summary>
    string DownloadsDirectory { get; }
}
