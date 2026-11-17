using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Providers.AniLibria;

/// <summary>
/// Pure JSON-to-model mapping for AniLibria responses. No I/O. Parsing and mapping of AnilibriaProvider.kt.
/// </summary>
internal static class AniLibriaParsers
{
    /// <summary>Parses <c>/app/search/releases</c>. Search hits carry no episodes.</summary>
    public static IReadOnlyList<Anime> ParseSearch(string json, string origin)
    {
        var releases = Deserialize(json, AniLibriaJsonContext.Default.ListAniLibriaRelease);
        return releases.Select(release => ToAnime(release, origin)).ToList();
    }

    /// <summary>Parses <c>/anime/releases/{id}</c>.</summary>
    public static AniLibriaRelease ParseRelease(string json)
    {
        var release = Deserialize(json, AniLibriaJsonContext.Default.AniLibriaRelease);
        if (release.Episodes?.Any(episode => episode.Id is null) == true) throw Unexpected(null);
        return release;
    }

    public static Anime ToAnime(AniLibriaRelease release, string origin)
    {
        var id = release.Id.ToString(CultureInfo.InvariantCulture);
        var episodeCount = release.Episodes?.Count ?? 0;
        return new Anime
        {
            Id = id,
            ProviderId = AniLibriaProvider.ProviderId,
            Title = NotBlank(release.Name?.Main) ?? release.Name?.English ?? release.Alias ?? id,
            CoverUrl = Absolute(origin, release.Poster?.Src ?? release.Poster?.Preview ?? release.Poster?.Thumbnail),
            Synopsis = NotBlank(release.Description?.Trim()),
            SubEpisodes = release.EpisodesTotal ?? (episodeCount > 0 ? (int?)episodeCount : null),
            Year = release.Year,
            MalId = release.Mal?.Id,
        };
    }

    /// <summary>Episodes sorted by ordinal; numbers are strings like "12" or "12.5".</summary>
    public static IReadOnlyList<Episode> ToEpisodes(AniLibriaRelease release) =>
        (release.Episodes ?? new List<AniLibriaEpisode>())
            .OrderBy(episode => episode.Ordinal ?? 0.0)
            .Select(episode => new Episode(
                episode.Id ?? throw Unexpected(null),
                NumberOf(episode.Ordinal ?? 0.0),
                NotBlank(episode.Name)))
            .ToList();

    /// <summary>HLS renditions of one episode, best first; blank URLs are skipped.</summary>
    public static IReadOnlyList<StreamVariant> HlsVariants(AniLibriaEpisode episode)
    {
        var candidates = new (int Quality, string? Url)[]
        {
            (1080, episode.Hls1080),
            (720, episode.Hls720),
            (480, episode.Hls480),
        };
        return candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Url))
            .Select(candidate => new StreamVariant(candidate.Quality, candidate.Url!))
            .ToList();
    }

    /// <summary>Opening/ending interval; null unless both bounds are present and stop is after start.</summary>
    public static SkipInterval? ToInterval(AniLibriaSkip? skip) =>
        skip is { Start: { } start, Stop: { } stop } && stop > start ? new SkipInterval(start, stop) : null;

    /// <summary>Mirrors <c>numberOf</c>: whole numbers without a decimal point.</summary>
    public static string NumberOf(double ordinal) =>
        ordinal == Math.Truncate(ordinal)
            ? ((long)ordinal).ToString(CultureInfo.InvariantCulture)
            : ordinal.ToString(CultureInfo.InvariantCulture);

    private static string? NotBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Absolute(string origin, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (path.StartsWith("http", StringComparison.Ordinal)) return path;
        return origin + (path.StartsWith('/') ? path : "/" + path);
    }

    private static T Deserialize<T>(string json, JsonTypeInfo<T> typeInfo) where T : class
    {
        T? value;
        try
        {
            value = JsonSerializer.Deserialize(json, typeInfo);
        }
        catch (JsonException e)
        {
            throw Unexpected(e);
        }
        return value ?? throw Unexpected(null);
    }

    private static ProviderException Unexpected(Exception? inner) => new("Unexpected AniLiberty response", inner);
}
