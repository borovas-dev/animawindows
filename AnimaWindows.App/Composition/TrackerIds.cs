using System.Reflection;

namespace AnimaWindows.App.Composition;

/// <summary>
/// AniList / MyAnimeList OAuth client ids, injected at build time with
/// <c>-p:AniListClientId=... -p:MalClientId=...</c> (emitted as <see cref="AssemblyMetadataAttribute"/>).
/// Blank means the tracker is disabled in this build.
/// </summary>
internal static class TrackerIds
{
    public static string AniListClientId { get; } = Read("AniListClientId");

    public static string MalClientId { get; } = Read("MalClientId");

    private static string Read(string key) =>
        typeof(TrackerIds).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? string.Empty;
}
