using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Providers.AnimeFire;

/// <summary>Pure JSON-to-model mapping for AnimeFire API responses. No I/O. Port of AnimefireParsers.kt.</summary>
internal static class AnimeFireParsers
{
    private static readonly string[] TitlePriority = ["BR", "US", "JP"];

    public static IReadOnlyList<Anime> ParseSearch(string json, string providerId)
    {
        var data = Deserialize(json, AnimeFireJsonContext.Default.AfSearchResponse).Data ?? throw Unexpected(null);
        return data.Select(anime => ToAnime(anime, providerId)).ToList();
    }

    public static Anime ParseDetails(string json, string providerId)
    {
        var hero = Deserialize(json, AnimeFireJsonContext.Default.AfDetailsResponse).Data?.Hero ?? throw Unexpected(null);
        return ToAnime(hero, providerId);
    }

    /// <summary>Episodes with a global number across seasons (season first-episode offset), ascending.</summary>
    public static IReadOnlyList<Episode> ParseEpisodes(string json)
    {
        var details = Deserialize(json, AnimeFireJsonContext.Default.AfDetailsResponse).Data ?? throw Unexpected(null);

        // Last entry wins for duplicate season numbers, like Kotlin's associate.
        var seasonStart = new Dictionary<int, int>();
        foreach (var season in details.Seasons ?? new List<AfSeason>())
        {
            if (season.Number is { } seasonNumber) seasonStart[seasonNumber] = season.FirstEpisodeNumber ?? 1;
        }

        var numbered = new List<(AfEpisode Episode, double Number)>();
        foreach (var episode in details.Episodes ?? new List<AfEpisode>())
        {
            var localNumber = episode.Number ?? throw Unexpected(null);
            var offset = 1;
            if (episode.Season is { } episodeSeason && seasonStart.TryGetValue(episodeSeason, out var first)) offset = first;
            numbered.Add((episode, localNumber + offset - 1));
        }

        return numbered
            .OrderBy(entry => entry.Number)
            .Select(entry => new Episode(
                entry.Episode.Id ?? throw Unexpected(null),
                NumberOf(entry.Number),
                NotBlank(entry.Episode.Title)))
            .ToList();
    }

    /// <summary>
    /// URL of the playable master for the requested audio, or null. Skips offline and blank entries, then prefers non-MTL
    /// sources (stable order otherwise).
    /// </summary>
    public static string? ParseStreamUrl(string json, bool dub)
    {
        var streams = Deserialize(json, AnimeFireJsonContext.Default.AfPlaybackResponse).Data?.Streams ?? throw Unexpected(null);
        return streams
            .Where(stream => stream.IsOffline != true
                && NotBlank(stream.Url) is not null
                && ((stream.Audio ?? "") == "dublado") == dub)
            .OrderBy(stream => stream.IsMtl == true)
            .FirstOrDefault()?.Url;
    }

    /// <summary>Mirrors <c>numberOf</c>-style formatting: whole numbers without a decimal point.</summary>
    public static string NumberOf(double number) =>
        number % 1.0 == 0.0
            ? ((int)number).ToString(CultureInfo.InvariantCulture)
            : number.ToString(CultureInfo.InvariantCulture);

    private static Anime ToAnime(AfAnime anime, string providerId)
    {
        var id = anime.Id ?? throw Unexpected(null);
        return new Anime
        {
            Id = id,
            ProviderId = providerId,
            Title = TitleOf(anime, id),
            CoverUrl = NotBlank(anime.PosterSrc),
            Synopsis = NotBlank(anime.Synopsis),
        };
    }

    /// <summary>BR, then US, then JP title; otherwise any non-blank title; otherwise the id.</summary>
    private static string TitleOf(AfAnime anime, string id)
    {
        var titles = anime.Titles ?? new Dictionary<string, string>();
        foreach (var key in TitlePriority)
        {
            if (titles.TryGetValue(key, out var title) && NotBlank(title) is { } found) return found;
        }
        foreach (var value in titles.Values)
        {
            if (NotBlank(value) is { } any) return any;
        }
        return id;
    }

    private static string? NotBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

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

    private static ProviderException Unexpected(Exception? inner) => new("Unexpected AnimeFire response", inner);
}
