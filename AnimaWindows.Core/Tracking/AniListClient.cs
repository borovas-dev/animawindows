using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaWindows.Core.Tracking;

/// <summary>AniList GraphQL (https://docs.anilist.co). Implicit grant tokens last a year and cannot be refreshed. Mirrors core/sync/AniListClient.kt.</summary>
public sealed class AniListClient
{
    public const string AuthBase = "https://anilist.co/api/v2/oauth/authorize";
    public const string DefaultEndpoint = "https://graphql.anilist.co";

    internal const string ViewerQuery = "query { Viewer { name } }";

    internal const string LookupQuery =
        "query ($idMal: Int) { Media(idMal: $idMal, type: ANIME) { id episodes mediaListEntry { progress status } } }";

    internal const string SaveMutation =
        "mutation ($mediaId: Int, $progress: Int, $status: MediaListStatus) { " +
        "SaveMediaListEntry(mediaId: $mediaId, progress: $progress, status: $status) { id progress status } }";

    private readonly HttpClient _http;
    private readonly string _endpoint;

    public AniListClient(HttpClient http, string endpoint = DefaultEndpoint)
    {
        _http = http;
        _endpoint = endpoint;
    }

    /// <summary>AniList media id, episode count and the user's current progress (null when not on the list).</summary>
    public sealed record Entry(int MediaId, int? Episodes, int? Progress);

    /// <summary>Implicit grant: AniList redirects with the token in the URL fragment.</summary>
    public static string AuthorizeUrl(string clientId) => $"{AuthBase}?client_id={clientId}&response_type=token";

    private async Task<JsonObject> GqlAsync(string token, string query, JsonObject? variables, CancellationToken cancellationToken)
    {
        var payload = new JsonObject { ["query"] = query, ["variables"] = variables ?? new JsonObject() };
        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var text = await SyncHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
        var root = SyncHttp.ParseJson(text) as JsonObject ?? throw new JsonException("Unexpected AniList response");
        if (root["errors"] is { } errors)
        {
            throw new SyncHttpException(400, SyncHttp.Take(errors.ToJsonString(), 200));
        }

        return root["data"] as JsonObject ?? new JsonObject();
    }

    public async Task<string?> ViewerNameAsync(string token, CancellationToken cancellationToken = default)
    {
        var data = await GqlAsync(token, ViewerQuery, null, cancellationToken).ConfigureAwait(false);
        return SyncHttp.AsString((data["Viewer"] as JsonObject)?["name"]);
    }

    /// <summary>Maps a MAL id to the AniList media and the current list progress; null when AniList has no such show.</summary>
    public async Task<Entry?> LookupAsync(string token, int malId, CancellationToken cancellationToken = default)
    {
        JsonObject data;
        try
        {
            data = await GqlAsync(token, LookupQuery, new JsonObject { ["idMal"] = malId }, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncHttpException e) when (e.NotFound)
        {
            return null;
        }

        if (data["Media"] is not JsonObject media)
        {
            return null;
        }

        var id = SyncHttp.AsInt(media["id"]);
        if (id is null)
        {
            return null;
        }

        var progress = SyncHttp.AsInt((media["mediaListEntry"] as JsonObject)?["progress"]);
        return new Entry(id.Value, SyncHttp.AsInt(media["episodes"]), progress);
    }

    public async Task SaveAsync(string token, int mediaId, int progress, bool completed, CancellationToken cancellationToken = default)
    {
        var variables = new JsonObject
        {
            ["mediaId"] = mediaId,
            ["progress"] = progress,
            ["status"] = completed ? "COMPLETED" : "CURRENT",
        };
        await GqlAsync(token, SaveMutation, variables, cancellationToken).ConfigureAwait(false);
    }
}
