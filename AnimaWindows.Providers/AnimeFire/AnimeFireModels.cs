using System.Text.Json.Serialization;

namespace AnimaWindows.Providers.AnimeFire;

/// <summary>Source-generated JSON metadata for the AnimeFire DTOs in this folder.</summary>
[JsonSerializable(typeof(AfSearchResponse))]
[JsonSerializable(typeof(AfDetailsResponse))]
[JsonSerializable(typeof(AfPlaybackResponse))]
internal sealed partial class AnimeFireJsonContext : JsonSerializerContext;

internal sealed class AfSearchResponse
{
    [JsonPropertyName("data")] public List<AfAnime>? Data { get; init; }
}

internal sealed class AfDetailsResponse
{
    [JsonPropertyName("data")] public AfDetails? Data { get; init; }
}

internal sealed class AfPlaybackResponse
{
    [JsonPropertyName("data")] public AfPlayback? Data { get; init; }
}

internal sealed class AfAnime
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("titles")] public Dictionary<string, string>? Titles { get; init; }
    [JsonPropertyName("poster_src")] public string? PosterSrc { get; init; }
    [JsonPropertyName("synopsis")] public string? Synopsis { get; init; }
    [JsonPropertyName("audio")] public string? Audio { get; init; }
}

internal sealed class AfSeason
{
    [JsonPropertyName("number")] public int? Number { get; init; }
    [JsonPropertyName("first_episode_number")] public int? FirstEpisodeNumber { get; init; }
}

internal sealed class AfEpisode
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("number")] public double? Number { get; init; }
    [JsonPropertyName("season")] public int? Season { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
}

internal sealed class AfDetails
{
    [JsonPropertyName("hero")] public AfAnime? Hero { get; init; }
    [JsonPropertyName("seasons")] public List<AfSeason>? Seasons { get; init; }
    [JsonPropertyName("episodes")] public List<AfEpisode>? Episodes { get; init; }
}

internal sealed class AfPlayback
{
    [JsonPropertyName("streams")] public List<AfStream>? Streams { get; init; }
}

internal sealed class AfStream
{
    [JsonPropertyName("audio")] public string? Audio { get; init; }
    [JsonPropertyName("url")] public string? Url { get; init; }
    [JsonPropertyName("is_offline")] public bool? IsOffline { get; init; }
    [JsonPropertyName("is_mtl")] public bool? IsMtl { get; init; }
}
