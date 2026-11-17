using System.Net.Http.Headers;
using AnimaWindows.Core.Net;

namespace AnimaWindows.Core.Downloads;

/// <summary>Bytes stored so far and fraction (0..1) of the part that is done.</summary>
public readonly record struct DownloadProgress(long Bytes, float Percent);

internal static class DownloadHttp
{
    public const int BufferSize = 81920;

    /// <summary>GET with the stream headers (Referer, User-Agent, ...); falls back to the shared ani-cli user agent.</summary>
    public static HttpRequestMessage CreateRequest(Uri url, IReadOnlyDictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        var hasUserAgent = false;
        foreach (var (name, value) in headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
            if (string.Equals(name, "User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                hasUserAgent = true;
            }
        }

        if (!hasUserAgent)
        {
            request.Headers.TryAddWithoutValidation("User-Agent", NetworkConstants.AniCliUserAgent);
        }

        return request;
    }

    public static HttpRequestException StatusError(HttpResponseMessage response, Uri url) =>
        new($"HTTP {(int)response.StatusCode} for {url.Host}", null, response.StatusCode);

    /// <summary>Sends <paramref name="request"/> and throws <see cref="HttpRequestException"/> (with status) unless the answer is 2xx.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, CancellationToken ct)
    {
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = StatusError(response, request.RequestUri!);
            response.Dispose();
            throw error;
        }

        return response;
    }

    public static async Task<string> GetStringAsync(HttpClient http, Uri url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = CreateRequest(url, headers);
        using var response = await SendAsync(http, request, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes (all when null); returns bytes written.</summary>
    public static async Task<long> CopyAsync(Stream source, Stream destination, long? limit, Action<int>? onChunk, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        long total = 0;
        while (limit is null || total < limit.Value)
        {
            var want = limit is null ? buffer.Length : (int)Math.Min(buffer.Length, limit.Value - total);
            var read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            total += read;
            onChunk?.Invoke(read);
        }

        return total;
    }

    /// <summary>Reads and discards <paramref name="count"/> bytes.</summary>
    public static async Task SkipAsync(Stream source, long count, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        while (count > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, count)), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            count -= read;
        }
    }

    public static void ReplaceFile(string tempPath, string finalPath) => File.Move(tempPath, finalPath, overwrite: true);

    public static RangeHeaderValue RangeOf(long from, long? toInclusive) => new(from, toInclusive);
}
