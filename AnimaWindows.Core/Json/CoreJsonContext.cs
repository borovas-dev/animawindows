using System.Text.Json;
using System.Text.Json.Serialization;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Json;

/// <summary>
/// Source-generated System.Text.Json metadata for every serialisable Core model. camelCase property names (matching the
/// Kotlin kotlinx-serialization output), enums as their Kotlin constant names (see <see cref="TranslationMode"/>), defaults
/// always written (Kotlin <c>encodeDefaults = true</c>), unknown members ignored, trailing commas and comments tolerated.
/// Usage: <c>JsonSerializer.Serialize(value, CoreJsonContext.Default.AppSettings)</c>. Never use reflection-based overloads.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(Anime))]
[JsonSerializable(typeof(List<Anime>))]
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(List<Episode>))]
[JsonSerializable(typeof(StreamSet))]
[JsonSerializable(typeof(StreamVariant))]
[JsonSerializable(typeof(SubtitleTrack))]
[JsonSerializable(typeof(SkipInterval))]
[JsonSerializable(typeof(SkipTimes))]
[JsonSerializable(typeof(EpisodeProgress))]
[JsonSerializable(typeof(WatchEntry))]
[JsonSerializable(typeof(DownloadItem))]
[JsonSerializable(typeof(DownloadPayload))]
[JsonSerializable(typeof(RemoteConfigDocument))]
[JsonSerializable(typeof(ProviderRemoteConfig))]
[JsonSerializable(typeof(Tokens))]
[JsonSerializable(typeof(PendingUpdate))]
[JsonSerializable(typeof(List<PendingUpdate>))]
[JsonSerializable(typeof(BackupFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
public sealed partial class CoreJsonContext : JsonSerializerContext;
