using System.Net;

namespace AnimaWindows.Core.Downloads;

/// <summary>
/// Single-file HTTP download with <c>Range</c> resume (subtitle files, progressive MP4, external audio). Data goes to
/// <c>&lt;file&gt;.part</c> and is renamed when complete, so an existing target file always means "done". Replaces Media3's
/// <c>ProgressiveDownloader</c>.
/// </summary>
public sealed class ProgressiveDownloader
{
    private readonly HttpClient _http;

    public ProgressiveDownloader(HttpClient http) => _http = http;

    /// <returns>Size of the finished file in bytes.</returns>
    public async Task<long> DownloadAsync(
        Uri url,
        string filePath,
        IReadOnlyDictionary<string, string> headers,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (File.Exists(filePath))
        {
            var size = new FileInfo(filePath).Length;
            onProgress?.Invoke(new DownloadProgress(size, 1f));
            return size;
        }

        var tmp = filePath + ".part";
        var have = File.Exists(tmp) ? new FileInfo(tmp).Length : 0L;
        var response = await SendAsync(url, headers, have, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Stale partial data (file changed or already whole): start over.
            response.Dispose();
            File.Delete(tmp);
            have = 0;
            response = await SendAsync(url, headers, 0, cancellationToken).ConfigureAwait(false);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw DownloadHttp.StatusError(response, url);
            }

            var resumed = have > 0 && response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed)
            {
                have = 0; // server ignored the range (200) or nothing was stored yet
            }

            long? total = response.Content.Headers.ContentLength is { } len ? have + len : null;
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (var file = new FileStream(
                tmp, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, DownloadHttp.BufferSize, useAsync: true))
            {
                var stored = have;
                onProgress?.Invoke(new DownloadProgress(stored, total is > 0 ? (float)stored / total.Value : 0f));
                await DownloadHttp.CopyAsync(
                    body,
                    file,
                    null,
                    n =>
                    {
                        stored += n;
                        onProgress?.Invoke(new DownloadProgress(stored, total is > 0 ? Math.Min(1f, (float)stored / total.Value) : 0f));
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            var finalSize = new FileInfo(tmp).Length;
            if (total is { } expected && finalSize != expected)
            {
                throw new IOException($"Incomplete download: {finalSize} of {expected} bytes");
            }

            DownloadHttp.ReplaceFile(tmp, filePath);
            onProgress?.Invoke(new DownloadProgress(finalSize, 1f));
            return finalSize;
        }
    }

    private Task<HttpResponseMessage> SendAsync(Uri url, IReadOnlyDictionary<string, string> headers, long from, CancellationToken ct)
    {
        var request = DownloadHttp.CreateRequest(url, headers);
        if (from > 0)
        {
            request.Headers.Range = DownloadHttp.RangeOf(from, null);
        }

        return SendAndDisposeAsync(request, ct);
    }

    private async Task<HttpResponseMessage> SendAndDisposeAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
    }
}
