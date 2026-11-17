using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Abstractions;

/// <summary>
/// AniSkip lookups. Mirrors <c>SkipTimesRepository</c> in core/model/Contracts.kt and <c>AniSkipRepository</c> in
/// feature/aniskip. Implemented in <c>AnimaWindows.Core/AniSkip</c> (HTTP + in-memory LRU of 64 entries, negative results cached too).
/// </summary>
public interface ISkipTimesRepository
{
    /// <summary>
    /// Opening/ending for MAL id <paramref name="malId"/> episode <paramref name="episode"/>, or null when not found, disabled, or on any
    /// failure (HTTP 404 and network errors are swallowed; only cancellation propagates).
    /// </summary>
    Task<SkipTimes?> SkipTimesAsync(int malId, int episode, CancellationToken cancellationToken = default);
}

/// <summary>Resolves a MAL id for a title when the provider did not supply one. Mirrors <c>MalIdResolver</c> in feature/aniskip.</summary>
public interface IMalIdResolver
{
    /// <summary>MAL id for <paramref name="anime"/> (uses <see cref="Anime.MalId"/> if present), or null. Never throws except on cancellation.</summary>
    Task<int?> ResolveAsync(Anime anime, CancellationToken cancellationToken = default);
}
