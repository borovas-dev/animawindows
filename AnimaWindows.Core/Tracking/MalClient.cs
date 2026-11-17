using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaWindows.Core.Tracking;

/// <summary>Token endpoint answer. <paramref name="ExpiresIn"/> is in seconds.</summary>
public sealed record MalToken(string AccessToken, string? RefreshToken, long ExpiresIn);

/// <summary>MyAnimeList API v2 (https://myanimelist.net/apiconfig/references/api/v2). Mirrors core/sync/MalClient.kt.</summary>
public sealed class MalClient
{
    public const string DefaultApiBase = "https://api.myanimelist.net/v2";
    public const string DefaultAuthBase = "https://myanimelist.net/v1/oauth2";

    private readonly HttpClient _http;
    private readonly string _apiBase;
    private readonly string _authBase;

    public MalClient(HttpClient http, string apiBase = DefaultApiBase, string authBase = DefaultAuthBase)
    {
        _http = http;
        _apiBase = apiBase;
        _authBase = authBase;
    }

    /// <summary>Total episodes (null when unknown) and the user's watched count (null when not on the list).</summary>
    public sealed record Entry(int? Episodes, int? Watched);

    private static string Enc(string s) => Uri.EscapeDataString(s);

    public string AuthorizeUrl(string clientId, string challenge, string state, string redirectUri) =>
        $"{_authBase}/authorize?response_type=code&client_id={Enc(clientId)}&code_challenge={Enc(challenge)}" +
        $"&code_challenge_method=plain&state={Enc(state)}&redirect_uri={Enc(redirectUri)}";

    public Task<MalToken> ExchangeAsync(string clientId, string code, string verifier, string redirectUri, CancellationToken cancellationToken = default) =>
        TokenAsync(
            [
                new("client_id", clientId),
                new("grant_type", "authorization_code"),
                new("code", code),
                new("code_verifier", verifier),
                new("redirect_uri", redirectUri),
            ],
            cancellationToken);

    public Task<MalToken> RefreshAsync(string clientId, string refreshToken, CancellationToken cancellationToken = default) =>
        TokenAsync(
            [
                new("client_id", clientId),
                new("grant_type", "refresh_token"),
                new("refresh_token", refreshToken),
            ],
            cancellationToken);

    private async Task<MalToken> TokenAsync(List<KeyValuePair<string?, string?>> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_authBase}/token") { Content = new FormUrlEncodedContent(form) };
        var text = await SyncHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
        var root = SyncHttp.ParseJson(text) as JsonObject ?? throw new JsonException("Unexpected MAL token response");
        var access = SyncHttp.AsString(root["access_token"]) ?? throw new JsonException("MAL token response has no access_token");
        return new MalToken(access, SyncHttp.AsString(root["refresh_token"]), SyncHttp.AsLong(root["expires_in"]) ?? 3600);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    public async Task<string?> UserNameAsync(string token, CancellationToken cancellationToken = default)
    {
        using var request = Authed(HttpMethod.Get, $"{_apiBase}/users/@me", token);
        var text = await SyncHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
        return SyncHttp.AsString((SyncHttp.ParseJson(text) as JsonObject)?["name"]);
    }

    public async Task<Entry?> EntryAsync(string token, int malId, CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            using var request = Authed(HttpMethod.Get, $"{_apiBase}/anime/{malId}?fields=num_episodes,my_list_status", token);
            text = await SyncHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
        }
        catch (SyncHttpException e) when (e.NotFound)
        {
            return null;
        }

        var root = SyncHttp.ParseJson(text) as JsonObject ?? throw new JsonException("Unexpected MAL anime response");
        var total = SyncHttp.AsInt(root["num_episodes"]);
        if (total is <= 0)
        {
            total = null;
        }

        var watched = SyncHttp.AsInt((root["my_list_status"] as JsonObject)?["num_episodes_watched"]);
        return new Entry(total, watched);
    }

    public async Task UpdateAsync(string token, int malId, int watched, bool completed, CancellationToken cancellationToken = default)
    {
        using var request = Authed(HttpMethod.Patch, $"{_apiBase}/anime/{malId}/my_list_status", token);
        request.Content = new FormUrlEncodedContent(
        [
            new("status", completed ? "completed" : "watching"),
            new("num_watched_episodes", watched.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        ]);
        await SyncHttp.SendAsync(_http, request, cancellationToken).ConfigureAwait(false);
    }
}
