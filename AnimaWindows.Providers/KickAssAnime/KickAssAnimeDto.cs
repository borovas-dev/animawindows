using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaWindows.Providers.KickAssAnime;

// Private DTOs of the kaa.lt JSON API. Every field is nullable/defaulted so a missing or null member behaves like
// kotlinx-serialization's coerceInputValues + defaults.

internal sealed class KaaSearchRequest
{
    [JsonPropertyName("query")] public string Query { get; init; } = "";
}

internal sealed class KaaSearchResponse
{
    [JsonPropertyName("result")] public List<KaaHit>? Result { get; init; }
}

internal sealed class KaaImage
{
    [JsonPropertyName("hq")] public string? Hq { get; init; }
    [JsonPropertyName("sm")] public string? Sm { get; init; }
}

internal sealed class KaaHit
{
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("title_en")] public string? TitleEn { get; init; }
    [JsonPropertyName("synopsis")] public string? Synopsis { get; init; }
    [JsonPropertyName("year")] public int? Year { get; init; }
    [JsonPropertyName("locales")] public List<string>? Locales { get; init; }
    [JsonPropertyName("episode_count")] public int? EpisodeCount { get; init; }
    [JsonPropertyName("poster")] public KaaImage? Poster { get; init; }
}

internal sealed class KaaEpisodesResponse
{
    [JsonPropertyName("pages")] public List<KaaPageInfo>? Pages { get; init; }
    [JsonPropertyName("result")] public List<KaaEp>? Result { get; init; }
}

internal sealed class KaaPageInfo
{
    [JsonPropertyName("number")] public int? Number { get; init; }
}

internal sealed class KaaEp
{
    [JsonPropertyName("slug")] public string? Slug { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("episode_number")] public double? EpisodeNumber { get; init; }
    [JsonPropertyName("episode_string")] public string? EpisodeString { get; init; }
}

internal sealed class KaaEpisodeResponse
{
    [JsonPropertyName("language")] public string? Language { get; init; }
    [JsonPropertyName("servers")] public List<KaaServer>? Servers { get; init; }
}

internal sealed class KaaServer
{
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("src")] public string? Src { get; init; }
}

[JsonSerializable(typeof(KaaSearchRequest))]
[JsonSerializable(typeof(KaaSearchResponse))]
[JsonSerializable(typeof(KaaEpisodesResponse))]
[JsonSerializable(typeof(KaaEpisodeResponse))]
internal sealed partial class KickAssJsonContext : JsonSerializerContext
{
    /// <summary>Lenient reading (numbers in strings, trailing commas) and unescaped writing, like the Kotlin <c>Json { isLenient }</c>.</summary>
    public static KickAssJsonContext Instance { get; } = new(new JsonSerializerOptions
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
