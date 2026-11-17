using System.Collections.Immutable;

namespace AnimaWindows.Core.Models;

/// <summary>
/// All user settings. Mirrors <c>AppSettings</c> in core/model/Contracts.kt with identical defaults; JSON field names are the
/// camelCase Kotlin names, so Android settings JSON is readable. Immutable; change with <c>with</c> via
/// <c>ISettingsStore.UpdateAsync</c>.
/// </summary>
public sealed record AppSettings
{
    /// <summary>A fresh install / reset: nothing to migrate, so <see cref="MigratedV2"/> is already true (Kotlin <c>FreshSettings</c>).</summary>
    public static AppSettings Fresh { get; } = new() { MigratedV2 = true };

    /// <summary>Legacy (pre-0.2) single-provider choice; only read once to migrate to <see cref="ContentLanguage"/>.</summary>
    public string ProviderId { get; init; } = "hianime";

    /// <summary>Language of the content/sources: a code ("en", "pl", "zh", ...) or "auto" = follow <see cref="SubtitleLanguage"/> (fallback "en").</summary>
    public string ContentLanguage { get; init; } = "auto";

    public SourceStrategy SourceStrategy { get; init; } = SourceStrategy.Reliable;

    /// <summary>Manual provider order per content language (language to provider ids); unlisted providers follow in default order.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> ProviderOrder { get; init; } =
        ImmutableDictionary<string, IReadOnlyList<string>>.Empty;

    public ImmutableHashSet<string> DisabledProviders { get; init; } = [];

    /// <summary>Set once the legacy <see cref="ProviderId"/> has been mapped to <see cref="ContentLanguage"/>.</summary>
    public bool MigratedV2 { get; init; }

    public TranslationMode Mode { get; init; } = TranslationMode.Sub;
    public QualityPreference Quality { get; init; } = QualityPreference.Best;
    public bool AniSkipEnabled { get; init; } = true;
    public SkipBehavior SkipBehavior { get; init; } = SkipBehavior.Auto;

    /// <summary>0..5 seconds, like ani-skip's offset.</summary>
    public int SkipOffsetSec { get; init; }

    public bool AutoPlayNext { get; init; } = true;
    public ThemeMode ThemeMode { get; init; } = ThemeMode.System;

    /// <summary>Android "Material You" colors. Kept for backup compatibility; the Windows UI may map it to "use system accent color".</summary>
    public bool DynamicColor { get; init; } = true;

    public bool AiRecommendationsEnabled { get; init; }

    /// <summary>Download only on unmetered networks (see <c>INetworkCostProvider</c>).</summary>
    public bool DownloadWifiOnly { get; init; } = true;

    /// <summary>Seek-bar thumbnail preview while scrubbing.</summary>
    public bool ScrubPreviewEnabled { get; init; } = true;

    /// <summary>Also allow the network-hungry frame-decoding preview fallback on metered data. Sprite previews are always allowed.</summary>
    public bool ScrubPreviewMobileData { get; init; }

    /// <summary>ML-Kit-style language code ("zh", "pl", ...) subtitles should be shown in; empty = off.</summary>
    public string SubtitleLanguage { get; init; } = "";

    /// <summary>Opt in to machine translation when the provider has no track in <see cref="SubtitleLanguage"/>.</summary>
    public bool TranslateSubtitles { get; init; }

    /// <summary>Check GitHub Releases for a newer app version at startup. Default on.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>Also offer GitHub pre-release builds (alpha/beta) as updates.</summary>
    public bool IncludePrereleases { get; init; }

    /// <summary>Seek past the intro automatically (once, with an undo prompt).</summary>
    public bool AutoSkipIntro { get; init; }

    /// <summary>Seek past the outro automatically (once, with an undo prompt).</summary>
    public bool AutoSkipOutro { get; init; }

    /// <summary>Opt in: resolve the next episode's streams near the end of the current one so it starts instantly. Uses extra data.</summary>
    public bool PrefetchNextEpisode { get; init; }

    /// <summary>Allow <see cref="PrefetchNextEpisode"/> on metered networks.</summary>
    public bool PrefetchOnMetered { get; init; }

    /// <summary>Opt in (experimental): speak translated subtitles with system TTS when the show has no dub in the user's language.</summary>
    public bool DubEnabled { get; init; }

    // ---- Windows-port additions (no Kotlin counterpart) ----

    /// <summary>Playback engine selection; <see cref="PlaybackEngineChoice.Auto"/> lets the app decide.</summary>
    public PlaybackEngineChoice Engine { get; init; } = PlaybackEngineChoice.Auto;

    /// <summary>Custom downloads folder; null = <c>IAppPaths.DownloadsDirectory</c>.</summary>
    public string? DownloadDirectory { get; init; }
}
