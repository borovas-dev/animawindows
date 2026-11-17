using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimaWindows.Providers.AnimeParadise;

// Private DTOs of the api.animeparadise.moe JSON API (mirrors AnimeParadiseDto.kt). Fields are nullable so null/missing
// members coerce to defaults like kotlinx-serialization's coerceInputValues.

internal sealed class ApSearchResponse
{
    [JsonPropertyName("data")] public List<ApAnimeDto>? Data { get; init; }
}

internal sealed class ApDetailsResponse
{
    [JsonPropertyName("data")] public ApAnimeDto? Data { get; init; }
}

internal sealed class ApEpisodesResponse
{
    [JsonPropertyName("data")] public List<ApEpisodeDto>? Data { get; init; }
}

internal sealed class ApEpisodeResponse
{
    [JsonPropertyName("data")] public ApEpisodeData? Data { get; init; }
}

internal sealed class ApEpisodeData
{
    [JsonPropertyName("episode")] public ApEpisodeDto? Episode { get; init; }
}

internal sealed class ApAnimeDto
{
    [JsonPropertyName("_id")] public string? Id { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("link")] public string? Link { get; init; }
    [JsonPropertyName("episodes")] public int? Episodes { get; init; }
    [JsonPropertyName("synopsys")] public string? Synopsys { get; init; }
    [JsonPropertyName("alternativeTitle")] public ApAltTitle? AlternativeTitle { get; init; }
    [JsonPropertyName("posterImage")] public ApPoster? PosterImage { get; init; }
    [JsonPropertyName("animeSeason")] public ApSeason? AnimeSeason { get; init; }
    [JsonPropertyName("mappings")] public ApMappings? Mappings { get; init; }
}

internal sealed class ApAltTitle
{
    [JsonPropertyName("english")] public string? English { get; init; }
    [JsonPropertyName("native")] public string? Native { get; init; }
    [JsonPropertyName("romaji")] public string? Romaji { get; init; }
}

internal sealed class ApSeason
{
    [JsonPropertyName("year")] public int? Year { get; init; }
}

internal sealed class ApMappings
{
    [JsonPropertyName("myanimelist")] public int? Myanimelist { get; init; }
}

internal sealed class ApPoster
{
    [JsonPropertyName("large")] public string? Large { get; init; }
    [JsonPropertyName("medium")] public string? Medium { get; init; }
    [JsonPropertyName("original")] public string? Original { get; init; }

    public string? Best() => Large ?? Medium ?? Original;
}

internal sealed class ApEpisodeDto
{
    [JsonPropertyName("uid")] public string? Uid { get; init; }
    [JsonPropertyName("number")] public string? Number { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("origin")] public string? Origin { get; init; }
    [JsonPropertyName("streamLink")] public string? StreamLink { get; init; }
    [JsonPropertyName("subData")] public List<ApSubDto>? SubData { get; init; }
    [JsonPropertyName("skipData")] public ApSkipData? SkipData { get; init; }
}

internal sealed class ApSubDto
{
    [JsonPropertyName("src")] public string? Src { get; init; }
    [JsonPropertyName("label")] public string? Label { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
}

internal sealed class ApSkipData
{
    [JsonPropertyName("intro")] public ApSkipDto? Intro { get; init; }
    [JsonPropertyName("outro")] public ApSkipDto? Outro { get; init; }
}

internal sealed class ApSkipDto
{
    [JsonPropertyName("start")] public double? Start { get; init; }
    [JsonPropertyName("end")] public double? End { get; init; }
}

[JsonSerializable(typeof(ApSearchResponse))]
[JsonSerializable(typeof(ApDetailsResponse))]
[JsonSerializable(typeof(ApEpisodesResponse))]
[JsonSerializable(typeof(ApEpisodeResponse))]
internal sealed partial class AnimeParadiseJsonContext : JsonSerializerContext
{
    /// <summary>Lenient reading (numbers in strings, trailing commas), like the Kotlin <c>Json { isLenient }</c>.</summary>
    public static AnimeParadiseJsonContext Instance { get; } = new(new JsonSerializerOptions
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    });
}
