using System.Text.Json.Serialization;

namespace AnimaWindows.Core.Models;

// Enum members carry explicit JSON names equal to the Kotlin enum constants (SUB, BEST_QUALITY, ...) so settings and
// backup JSON written by the Android app can be read as-is, and vice versa.

/// <summary>Sub or dub. Mirrors <c>TranslationMode</c> in core/model/Models.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TranslationMode>))]
public enum TranslationMode
{
    [JsonStringEnumMemberName("SUB")] Sub,
    [JsonStringEnumMemberName("DUB")] Dub,
}

/// <summary>Preferred stream quality. Mirrors <c>QualityPreference</c> in core/model/Models.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QualityPreference>))]
public enum QualityPreference
{
    [JsonStringEnumMemberName("BEST")] Best,
    [JsonStringEnumMemberName("WORST")] Worst,
    [JsonStringEnumMemberName("P1080")] P1080,
    [JsonStringEnumMemberName("P720")] P720,
    [JsonStringEnumMemberName("P480")] P480,
    [JsonStringEnumMemberName("P360")] P360,
}

/// <summary>App theme. Mirrors <c>ThemeMode</c> in core/model/Contracts.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ThemeMode>))]
public enum ThemeMode
{
    [JsonStringEnumMemberName("SYSTEM")] System,
    [JsonStringEnumMemberName("LIGHT")] Light,
    [JsonStringEnumMemberName("DARK")] Dark,
}

/// <summary>How an intro/outro skip is offered. Mirrors <c>SkipBehavior</c> in core/model/Contracts.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SkipBehavior>))]
public enum SkipBehavior
{
    [JsonStringEnumMemberName("AUTO")] Auto,
    [JsonStringEnumMemberName("BUTTON")] Button,
}

/// <summary>
/// How the provider chain picks a stream source: fail over only on errors (<see cref="Reliable"/>), or compare the
/// top two sources (<see cref="BestQuality"/>). Mirrors <c>SourceStrategy</c> in core/model/Contracts.kt.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<SourceStrategy>))]
public enum SourceStrategy
{
    [JsonStringEnumMemberName("RELIABLE")] Reliable,
    [JsonStringEnumMemberName("BEST_QUALITY")] BestQuality,
}

/// <summary>
/// Which playback engine to use. New in the Windows port (no Kotlin counterpart).
/// <see cref="Auto"/> = app decides (native MediaPlayer for plain HLS/MP4, libmpv when needed).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PlaybackEngineChoice>))]
public enum PlaybackEngineChoice
{
    [JsonStringEnumMemberName("AUTO")] Auto,
    [JsonStringEnumMemberName("NATIVE")] Native,
    [JsonStringEnumMemberName("MPV")] Mpv,
}

/// <summary>Lifecycle of one episode download. Mirrors <c>DownloadState</c> in core/model/Downloads.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DownloadState>))]
public enum DownloadState
{
    [JsonStringEnumMemberName("QUEUED")] Queued,
    [JsonStringEnumMemberName("DOWNLOADING")] Downloading,
    [JsonStringEnumMemberName("PAUSED")] Paused,
    [JsonStringEnumMemberName("COMPLETED")] Completed,
    [JsonStringEnumMemberName("FAILED")] Failed,
}

/// <summary>External tracker. Mirrors <c>SyncService</c> in core/sync/SyncApi.kt.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SyncService>))]
public enum SyncService
{
    [JsonStringEnumMemberName("ANILIST")] AniList,
    [JsonStringEnumMemberName("MAL")] Mal,
}

/// <summary>Segment an AniSkip action refers to. Mirrors <c>SkipSegment</c> in feature/aniskip/SkipController.kt.</summary>
public enum SkipSegment
{
    Opening,
    Ending,
}

public static class DownloadStateExtensions
{
    /// <summary>True for <see cref="DownloadState.Queued"/> and <see cref="DownloadState.Downloading"/> (Kotlin <c>isActive</c>).</summary>
    public static bool IsActive(this DownloadState state) => state is DownloadState.Queued or DownloadState.Downloading;
}
