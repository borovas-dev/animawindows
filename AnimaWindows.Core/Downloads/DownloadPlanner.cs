using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Downloads;

/// <summary>The stream set to download (single rendition, sidecars kept) and the chosen height. Mirrors <c>DownloadPlanner.Plan</c>.</summary>
public sealed record DownloadPlan(StreamSet Snapshot, int? Quality);

/// <summary>
/// Turns a resolved <see cref="StreamSet"/> into a download plan. Mirrors <c>DownloadPlanner</c> in
/// feature/downloads/DownloadPlanning.kt. Offline storage always keeps a single rendition (no ABR): the variant matching the
/// quality preference. Sidecars (external audio, subtitles) are kept.
/// </summary>
public sealed class DownloadPlanner
{
    private readonly IProviderChain _chain;
    private readonly HttpClient? _http;

    /// <param name="chain">Resolves streams (fails over to alternate providers).</param>
    /// <param name="http">Only used to read a master playlist when the provider offered no variant list.</param>
    public DownloadPlanner(IProviderChain chain, HttpClient? http = null)
    {
        _chain = chain;
        _http = http;
    }

    private static int? TargetHeight(QualityPreference pref) => pref switch
    {
        QualityPreference.P1080 => 1080,
        QualityPreference.P720 => 720,
        QualityPreference.P480 => 480,
        QualityPreference.P360 => 360,
        _ => null,
    };

    /// <summary>Same rule as the player's quality selector: exact match, else nearest lower, else best.</summary>
    public static StreamVariant? SelectVariant(IReadOnlyList<StreamVariant> variants, QualityPreference pref)
    {
        if (variants.Count == 0)
        {
            return null;
        }

        var known = variants.Where(v => v.Quality is not null).ToList();
        var height = TargetHeight(pref);
        var best = known.Count > 0 ? known.MaxBy(v => v.Quality!.Value)! : variants[0];
        if (height is { } h)
        {
            return known.FirstOrDefault(v => v.Quality == h)
                ?? known.Where(v => v.Quality!.Value < h).MaxBy(v => v.Quality!.Value)
                ?? best;
        }

        if (pref == QualityPreference.Worst)
        {
            return known.Count > 0 ? known.MinBy(v => v.Quality!.Value)! : variants[^1];
        }

        return best;
    }

    /// <summary>
    /// The variant matching <paramref name="pref"/> as a single-rendition snapshot. When the provider offers no variant list the
    /// master playlist is kept (a rendition is chosen later by <see cref="PlanAsync"/>). Null when nothing is downloadable.
    /// </summary>
    public static DownloadPlan? CreatePlan(StreamSet set, QualityPreference pref)
    {
        var variant = SelectVariant(set.Variants, pref);
        if (variant is not null)
        {
            return new DownloadPlan(set with { MasterUrl = null, Variants = [variant] }, variant.Quality);
        }

        if (set.MasterUrl is not null)
        {
            return new DownloadPlan(set with { Variants = [] }, null);
        }

        return null;
    }

    public static bool IsHls(string url)
    {
        var cut = url;
        var q = cut.IndexOf('?');
        if (q >= 0)
        {
            cut = cut[..q];
        }

        var f = cut.IndexOf('#');
        if (f >= 0)
        {
            cut = cut[..f];
        }

        return cut.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Main part first, then sidecars. <paramref name="snapshot"/> is the (already planned) stream set.</summary>
    public static IReadOnlyList<PartRequest> Parts(string key, StreamSet snapshot)
    {
        var main = snapshot.Variants.Count > 0 ? snapshot.Variants[0].Url : snapshot.MasterUrl;
        if (main is null)
        {
            return [];
        }

        var parts = new List<PartRequest> { new(DownloadIds.MainId(key), main, IsHls(main)) };
        if (snapshot.ExternalAudioUrl is { } audio)
        {
            parts.Add(new PartRequest(DownloadIds.AudioId(key), audio, IsHls(audio)));
        }

        var i = 0;
        foreach (var s in snapshot.Subtitles.DistinctBy(track => track.Url))
        {
            parts.Add(new PartRequest(DownloadIds.SubtitleId(key, i++), s.Url, false));
        }

        return parts;
    }

    /// <summary>
    /// Resolves streams through the provider chain (the download stays filed under the original provider/episode even when an
    /// alternate served it) and plans them. Throws when no stream is downloadable.
    /// </summary>
    public async Task<DownloadPlan> PlanAsync(
        Anime anime, Episode episode, TranslationMode mode, QualityPreference pref, CancellationToken cancellationToken = default)
    {
        var resolved = await _chain.StreamsAsync(anime, episode, mode, cancellationToken).ConfigureAwait(false);
        var plan = CreatePlan(resolved.Set, pref) ?? throw new InvalidOperationException("No downloadable stream found");
        if (plan.Snapshot.MasterUrl is { } master && plan.Snapshot.Variants.Count == 0 && _http is not null)
        {
            plan = await ResolveMasterAsync(plan, master, pref, cancellationToken).ConfigureAwait(false);
        }

        return plan;
    }

    private async Task<DownloadPlan> ResolveMasterAsync(DownloadPlan plan, string master, QualityPreference pref, CancellationToken ct)
    {
        string text;
        try
        {
            var url = new Uri(master);
            using var request = DownloadHttp.CreateRequest(url, plan.Snapshot.Headers);
            using var response = await _http!.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!HlsPlaylist.IsMaster(text))
            {
                return plan; // already a media playlist
            }

            var found = HlsPlaylist.ParseVariants(text, url)
                .Select(v => new StreamVariant(v.Height, v.Url.AbsoluteUri))
                .OrderByDescending(v => v.Quality ?? 0)
                .ToList();
            var chosen = SelectVariant(found, pref);
            return chosen is null
                ? plan
                : new DownloadPlan(plan.Snapshot with { MasterUrl = null, Variants = [chosen] }, chosen.Quality);
        }
        catch (Exception e) when (e is HttpRequestException or UriFormatException)
        {
            // Keep the master; the downloader reports a clear error if it cannot be used.
            return plan;
        }
    }
}
