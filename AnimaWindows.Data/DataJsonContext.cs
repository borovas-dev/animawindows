using System.Text.Json;
using System.Text.Json.Serialization;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Data;

/// <summary>
/// Indented variant of the Core JSON settings for backup files (same naming/enum rules as <c>CoreJsonContext</c>).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    WriteIndented = true)]
[JsonSerializable(typeof(BackupFile))]
internal sealed partial class DataJsonContext : JsonSerializerContext;
