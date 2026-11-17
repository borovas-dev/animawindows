using System.Text.Json.Serialization;

namespace AnimaWindows.Core.Models;

/// <summary>One playable rendition. Mirrors <c>StreamVariant</c> in core/model/Models.kt.</summary>
/// <param name="Quality">Height in px (1080), or null when unknown.</param>
/// <param name="Url">Absolute media URL (HLS media playlist or direct file).</param>
public sealed record StreamVariant(int? Quality, string Url);

/// <summary>An external subtitle file. Mirrors <c>SubtitleTrack</c> in core/model/Models.kt.</summary>
public sealed record SubtitleTrack(string Url, string? Language, string? Label, bool IsDefault);

/// <summary>An intro/outro interval in seconds. Mirrors <c>SkipInterval</c> in core/model/Models.kt.</summary>
public sealed record SkipInterval(double StartSec, double EndSec);

/// <summary>Opening/ending of an episode (either may be unknown). Mirrors <c>SkipTimes</c> in core/model/Models.kt.</summary>
public sealed record SkipTimes(SkipInterval? Opening, SkipInterval? Ending);

/// <summary>Everything the player needs to play one episode. Mirrors <c>StreamSet</c> in core/model/Models.kt.</summary>
public sealed record StreamSet
{
    /// <summary>Master playlist (if HLS) so the engine can do adaptive bitrate.</summary>
    public string? MasterUrl { get; init; }

    /// <summary>Sorted best to worst.</summary>
    public IReadOnlyList<StreamVariant> Variants { get; init; } = [];

    /// <summary>HTTP headers (Referer, User-Agent, ...) that must accompany every media request.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    public string? ExternalAudioUrl { get; init; }
    public IReadOnlyList<SubtitleTrack> Subtitles { get; init; } = [];
    public int? MalId { get; init; }

    /// <summary>Provider-supplied intro (hianime embed <c>skip.intro</c>); fallback when AniSkip has no data.</summary>
    public SkipInterval? Intro { get; init; }

    /// <summary>Provider-supplied outro (hianime embed <c>skip.outro</c>); fallback when AniSkip has no data.</summary>
    public SkipInterval? Outro { get; init; }

    /// <summary>
    /// Thumbnail-sprite WebVTT (cues point at <c>sprite.jpg#xywh=x,y,w,h</c>) for the seek-bar scrub preview. Null when the
    /// provider has none; the player then falls back to decoding frames from the stream.
    /// </summary>
    public string? SpriteVttUrl { get; init; }

    /// <summary>Human-readable audio/subtitle language of this source (e.g. "Polish dub/lektor"); null when the provider has one fixed language.</summary>
    public string? LanguageLabel { get; init; }

    /// <summary>
    /// ML-Kit-style language code ("zh", "pl", ...) of subtitles burned into the video, or null when none. When it equals
    /// the user's subtitle language no translation is needed.
    /// </summary>
    public string? HardsubLanguage { get; init; }

    /// <summary>True when there is something to play (a master playlist or at least one variant).</summary>
    [JsonIgnore]
    public bool IsPlayable => MasterUrl is not null || Variants.Count > 0;
}

/// <summary>
/// Streams resolved by <c>IProviderChain.StreamsAsync</c>. Mirrors <c>ResolvedStreams</c> in provider/api/ProviderChain.kt.
/// </summary>
/// <param name="Set">What the player needs.</param>
/// <param name="ProviderId">Provider that actually served <paramref name="Set"/>.</param>
/// <param name="IsFallback">True when <paramref name="ProviderId"/> is not the provider of the requested anime.</param>
/// <param name="Anime">The anime as known to <paramref name="ProviderId"/> (differs from the requested one on fallback).</param>
/// <param name="Episode">The episode as known to <paramref name="ProviderId"/> (its Id is provider-specific; use it for downloads).</param>
public sealed record ResolvedStreams(StreamSet Set, string ProviderId, bool IsFallback, Anime Anime, Episode Episode);
