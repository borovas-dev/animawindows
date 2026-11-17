using System.Text.Json.Serialization;

namespace AnimaWindows.Providers.AniLibria;

/// <summary>Source-generated JSON metadata for the AniLibria DTOs in this folder.</summary>
[JsonSerializable(typeof(List<AniLibriaRelease>))]
[JsonSerializable(typeof(AniLibriaRelease))]
internal sealed partial class AniLibriaJsonContext : JsonSerializerContext;

/// <summary>Release as returned by <c>/app/search/releases</c> (no episodes) and <c>/anime/releases/{id}</c>.</summary>
internal sealed class AniLibriaRelease
{
    [JsonPropertyName("id")] public int Id { get; init; }
    [JsonPropertyName("year")] public int? Year { get; init; }
    [JsonPropertyName("name")] public AniLibriaName? Name { get; init; }
    [JsonPropertyName("alias")] public string? Alias { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("episodes_total")] public int? EpisodesTotal { get; init; }
    [JsonPropertyName("poster")] public AniLibriaPoster? Poster { get; init; }
    [JsonPropertyName("mal")] public AniLibriaMal? Mal { get; init; }
    [JsonPropertyName("episodes")] public List<AniLibriaEpisode>? Episodes { get; init; }
}

internal sealed class AniLibriaName
{
    [JsonPropertyName("main")] public string? Main { get; init; }
    [JsonPropertyName("english")] public string? English { get; init; }
}

internal sealed class AniLibriaPoster
{
    [JsonPropertyName("src")] public string? Src { get; init; }
    [JsonPropertyName("preview")] public string? Preview { get; init; }
    [JsonPropertyName("thumbnail")] public string? Thumbnail { get; init; }
}

internal sealed class AniLibriaMal
{
    [JsonPropertyName("id")] public int? Id { get; init; }
}

/// <summary>Opening/ending window in whole seconds; either bound may be null.</summary>
internal sealed class AniLibriaSkip
{
    [JsonPropertyName("start")] public int? Start { get; init; }
    [JsonPropertyName("stop")] public int? Stop { get; init; }
}

internal sealed class AniLibriaEpisode
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("ordinal")] public double? Ordinal { get; init; }
    [JsonPropertyName("opening")] public AniLibriaSkip? Opening { get; init; }
    [JsonPropertyName("ending")] public AniLibriaSkip? Ending { get; init; }
    [JsonPropertyName("hls_480")] public string? Hls480 { get; init; }
    [JsonPropertyName("hls_720")] public string? Hls720 { get; init; }
    [JsonPropertyName("hls_1080")] public string? Hls1080 { get; init; }
}
