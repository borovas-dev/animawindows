using AnimaWindows.Core.Models;

namespace AnimaWindows.App.Player;

/// <summary>Navigation parameter of <see cref="PlayerPage"/>. Mirrors <c>PlayerRoute</c> in feature/player.</summary>
/// <param name="ProviderId">Provider the show came from.</param>
/// <param name="Anime">The show.</param>
/// <param name="Episodes">Episode list in play order.</param>
/// <param name="StartIndex">Index into <paramref name="Episodes"/> to start with.</param>
/// <param name="Offline">Play only downloaded media; never calls a provider.</param>
public sealed record PlayerNavigationArgs(
    string ProviderId,
    Anime Anime,
    IReadOnlyList<Episode> Episodes,
    int StartIndex,
    bool Offline);
