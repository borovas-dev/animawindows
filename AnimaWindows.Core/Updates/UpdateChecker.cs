using System.Net;
using System.Text.Json;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Updates;

/// <summary>
/// Checks GitHub Releases for a newer app version and exposes <see cref="UpdateState"/>. Port of <c>UpdateChecker</c>
/// (check only: downloading/installing is done by the app; <see cref="UpdateState.Available.PackageUrl"/> is the MSIX asset URL and
/// <see cref="UpdateState.Available.PageUrl"/> the release page).
/// </summary>
public sealed class UpdateChecker
{
    public const string DefaultRepo = "borovas-dev/animawindows";
    public const string DefaultApiBase = "https://api.github.com";

    private static readonly string[] PackageExtensions = [".msix", ".msixbundle", ".appx", ".appxbundle"];

    private readonly HttpClient _client;
    private readonly string _currentVersion;
    private readonly string _repo;
    private readonly string _apiBase;
    private UpdateState _state = new UpdateState.Idle();

    public UpdateChecker(HttpClient client, string currentVersion, string repo = DefaultRepo, string apiBase = DefaultApiBase)
    {
        _client = client;
        _currentVersion = currentVersion;
        _repo = repo;
        _apiBase = apiBase.TrimEnd('/');
    }

    public UpdateState State => Volatile.Read(ref _state);

    /// <summary>Raised on every state change (any thread).</summary>
    public event EventHandler<UpdateState>? StateChanged;

    private void Set(UpdateState s)
    {
        Volatile.Write(ref _state, s);
        StateChanged?.Invoke(this, s);
    }

    /// <summary>Hides the banner for this process (e.g. user chose "Later").</summary>
    public void Dismiss() => Set(new UpdateState.Idle());

    /// <summary>
    /// Queries GitHub and updates <see cref="State"/>. <paramref name="includePrereleases"/> also offers pre-release builds.
    /// Never throws except for cancellation (failures become <see cref="UpdateState.Failed"/>). Returns the final state.
    /// </summary>
    public async Task<UpdateState> CheckAsync(bool includePrereleases = false, CancellationToken cancellationToken = default)
    {
        if (State is UpdateState.Checking or UpdateState.Downloading) return State;
        Set(new UpdateState.Checking());
        UpdateState result;
        try
        {
            var path = includePrereleases ? "releases?per_page=30" : "releases/latest";
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_apiBase}/repos/{_repo}/{path}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("User-Agent", "animawindows");
            using var resp = await _client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.NotFound) result = new UpdateState.UpToDate(); // no releases yet
            else if (!resp.IsSuccessStatusCode) result = new UpdateState.Failed($"HTTP {(int)resp.StatusCode}");
            else
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                result = includePrereleases
                    ? ParseReleaseList(body, _currentVersion, true)
                    : ParseRelease(body, _currentVersion);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Set(new UpdateState.Idle());
            throw;
        }
        catch (Exception e)
        {
            result = new UpdateState.Failed(string.IsNullOrEmpty(e.Message) ? "error" : e.Message);
        }

        Set(result);
        return result;
    }

    private sealed record GitHubAsset(string Name, string Url);

    private sealed record GitHubRelease(string Tag, string? Body, string HtmlUrl, bool Draft, bool Prerelease, IReadOnlyList<GitHubAsset> Assets);

    private static GitHubRelease ReadRelease(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new JsonException("release must be an object");
        string Str(JsonElement o, string name) =>
            o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        bool Bool(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

        var assets = new List<GitHubAsset>();
        if (e.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var a in arr.EnumerateArray())
                if (a.ValueKind == JsonValueKind.Object) assets.Add(new GitHubAsset(Str(a, "name"), Str(a, "browser_download_url")));
        string? body = e.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        return new GitHubRelease(Str(e, "tag_name"), body, Str(e, "html_url"), Bool(e, "draft"), Bool(e, "prerelease"), assets);
    }

    private static UpdateState.Available? ToAvailable(GitHubRelease r, string currentVersion, bool includePrereleases)
    {
        if (r.Draft || (r.Prerelease && !includePrereleases) || !SemVer.IsNewer(r.Tag, currentVersion)) return null;
        var pkg = r.Assets.FirstOrDefault(a => PackageExtensions.Any(x => a.Name.EndsWith(x, StringComparison.OrdinalIgnoreCase)));
        if (pkg is null) return null;
        var version = r.Tag.Trim();
        if (version.StartsWith('v')) version = version[1..];
        return new UpdateState.Available(version, (r.Body ?? "").Trim(), pkg.Url, r.HtmlUrl);
    }

    /// <summary>Parses a <c>releases/latest</c> response; Available only for a newer release that ships an MSIX package. Throws <see cref="JsonException"/> on garbage.</summary>
    internal static UpdateState ParseRelease(string body, string currentVersion, bool includePrereleases = false)
    {
        using var doc = JsonDocument.Parse(body);
        return ToAvailable(ReadRelease(doc.RootElement), currentVersion, includePrereleases) ?? (UpdateState)new UpdateState.UpToDate();
    }

    /// <summary>Parses a <c>releases</c> list: the highest version (by semver, not publish order) newer than ours that ships a package.</summary>
    internal static UpdateState ParseReleaseList(string body, string currentVersion, bool includePrereleases)
    {
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) throw new JsonException("expected array");
        UpdateState.Available? best = null;
        SemVer? bestVer = null;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var a = ToAvailable(ReadRelease(el), currentVersion, includePrereleases);
            if (a is null) continue;
            var v = SemVer.Parse(a.Version) ?? new SemVer([0]);
            if (best is null || v.CompareTo(bestVer) > 0)
            {
                best = a;
                bestVer = v;
            }
        }

        return best ?? (UpdateState)new UpdateState.UpToDate();
    }
}
