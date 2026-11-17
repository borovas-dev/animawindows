using System.Collections.Immutable;

namespace AnimaWindows.Core.Models;

/// <summary>
/// A show as returned by a provider search. <see cref="Id"/> is provider-specific (e.g. a hianime slug).
/// Mirrors <c>Anime</c> in core/model/Models.kt.
/// </summary>
public sealed record Anime
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public required string Title { get; init; }
    public string? CoverUrl { get; init; }
    public string? Synopsis { get; init; }
    public int? SubEpisodes { get; init; }
    public int? DubEpisodes { get; init; }
    public int? Year { get; init; }
    public int? MalId { get; init; }

    /// <summary>
    /// The same show on other providers (providerId to animeId), filled by <c>IProviderChain.SearchAsync</c> when results are
    /// merged. Used as failover when the primary provider (<see cref="ProviderId"/>) fails. Never contains this anime itself.
    /// </summary>
    public IReadOnlyDictionary<string, string> Alternates { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>One episode of a show. Mirrors <c>Episode</c> in core/model/Models.kt.</summary>
/// <param name="Id">Provider episode id used to fetch streams.</param>
/// <param name="Number">Human episode number as a string ("1", "12.5"), like ani-cli ep_no. Used as the key in progress/downloads.</param>
/// <param name="Title">Optional episode title.</param>
public sealed record Episode(string Id, string Number, string? Title = null);
