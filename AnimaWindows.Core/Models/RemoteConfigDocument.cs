using System.Text.Json.Serialization;

namespace AnimaWindows.Core.Models;

/// <summary>
/// Whole <c>providers.json</c> document. <see cref="Revision"/> only ever increases; older documents are ignored (anti-rollback).
/// Mirrors <c>RemoteConfig</c> (renamed so it cannot collide with the AnimaWindows.Core.RemoteConfig namespace) in core/remoteconfig/Models.kt.
/// </summary>
public sealed record RemoteConfigDocument
{
    public int Revision { get; init; }
    public IReadOnlyDictionary<string, ProviderRemoteConfig> Providers { get; init; } = new Dictionary<string, ProviderRemoteConfig>();
}

/// <summary>
/// Per-provider overrides. Every field has a safe default, so a provider missing from the JSON (or a config that could not be
/// loaded at all) behaves as "enabled, no overrides". Mirrors <c>ProviderRemoteConfig</c> in core/remoteconfig/Models.kt.
/// </summary>
public sealed record ProviderRemoteConfig
{
    public static ProviderRemoteConfig Default { get; } = new();

    public bool Enabled { get; init; } = true;

    /// <summary>Primary first, then mirrors.</summary>
    public IReadOnlyList<string> BaseUrls { get; init; } = [];

    /// <summary>Informational: the site is known to be unreachable from mainland China.</summary>
    public bool GfwBlocked { get; init; }

    /// <summary>Providers needing a newer app than this are treated as disabled (see <c>IProviderConfig.ForProvider</c>).</summary>
    public string? MinAppVersion { get; init; }

    /// <summary>Free-form string constants (server name, XOR key, referer, regex ...).</summary>
    public IReadOnlyDictionary<string, string> Params { get; init; } = new Dictionary<string, string>();

    /// <summary>Primary base URL without a trailing slash, or null.</summary>
    [JsonIgnore]
    public string? BaseUrl => BaseUrls.Count > 0 ? BaseUrls[0].TrimEnd('/') : null;

    public string BaseUrlOr(string defaultUrl) => BaseUrl ?? defaultUrl;

    /// <summary>All base URLs (trailing slashes trimmed), or <paramref name="defaults"/> when none are configured.</summary>
    public IReadOnlyList<string> MirrorsOr(IReadOnlyList<string> defaults) =>
        BaseUrls.Count == 0 ? defaults : BaseUrls.Select(u => u.TrimEnd('/')).ToList();

    public string? Param(string key) => Params.TryGetValue(key, out var v) ? v : null;

    public string Param(string key, string defaultValue) => Param(key) ?? defaultValue;
}
