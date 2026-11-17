using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnimaWindows.Core.Tracking;

/// <summary>Non-2xx answer from a tracker. Network failures stay <see cref="HttpRequestException"/>. Mirrors <c>SyncHttpException</c> in core/sync/Http.kt.</summary>
public sealed class SyncHttpException : Exception
{
    public SyncHttpException(int code, string message) : base($"HTTP {code}: {message}") => Code = code;

    public int Code { get; }

    public bool Unauthorized => Code == 401;

    public bool NotFound => Code == 404;

    /// <summary>Worth retrying later (rate limit / server trouble).</summary>
    public bool Transient => Code == 429 || Code >= 500;
}

internal static class SyncHttp
{
    public static async Task<string> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new SyncHttpException((int)response.StatusCode, Take(body, 200));
        }

        return body;
    }

    public static string Take(string s, int max) => s.Length > max ? s[..max] : s;

    public static int? AsInt(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    public static long? AsLong(JsonNode? node) => node is JsonValue v && v.TryGetValue<long>(out var i) ? i : null;

    public static string? AsString(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static JsonNode ParseJson(string text) =>
        JsonNode.Parse(text) ?? throw new JsonException("Empty JSON document");
}
