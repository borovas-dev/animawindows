using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace AnimaWindows.App.Player;

/// <summary>
/// A registered proxy route. Disposing it removes the route (in-flight transfers finish or fail on their own).
/// </summary>
public sealed class ProxyLease : IDisposable
{
    private readonly Action _release;
    private int _released;

    internal ProxyLease(string url, Action release)
    {
        Url = url;
        _release = release;
    }

    /// <summary>Loopback URL to hand to the player.</summary>
    public string Url { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _release();
    }
}

/// <summary>
/// Minimal loopback HTTP/1.1 server (plain <see cref="TcpListener"/>, so no URL ACL is needed in a packaged app) that lets
/// <c>MediaPlayer</c> play sources it cannot fetch itself: (a) remote progressive URLs with injected headers and <c>Range</c>
/// pass-through, (b) local download folders (playlist, segments, keys) or single files with correct content types.
/// Routes use unguessable ids; only requests addressed to the loopback host name are served.
/// </summary>
public sealed class LocalStreamProxy : IDisposable
{
    private sealed record RemoteRoute(string Url, IReadOnlyDictionary<string, string> Headers);

    private sealed record LocalRoute(string RootDirectory);

    private const int MaxHeaderBytes = 16 * 1024;

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, RemoteRoute> _remote = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, LocalRoute> _local = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private TcpListener? _listener;
    private HttpClient? _client;
    private int _port;
    private bool _disposed;

    /// <summary>Port the server listens on (0 until the first route is registered).</summary>
    public int Port => _port;

    /// <summary>Serves <paramref name="url"/> through the proxy, adding <paramref name="headers"/> to every upstream request.</summary>
    public ProxyLease RegisterRemote(string url, IReadOnlyDictionary<string, string> headers)
    {
        EnsureStarted();
        var id = NewId();
        _remote[id] = new RemoteRoute(url, headers);
        return new ProxyLease($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/r/{id}", () => _remote.TryRemove(id, out _));
    }

    /// <summary>
    /// Serves the directory of <paramref name="localPath"/> (a playlist or media file) and returns the URL of that file;
    /// relative segment/key paths in the playlist resolve against the same route.
    /// </summary>
    public ProxyLease RegisterLocal(string localPath)
    {
        EnsureStarted();
        var full = Path.GetFullPath(localPath);
        var root = Path.GetDirectoryName(full) ?? throw new ArgumentException("Path has no directory", nameof(localPath));
        var id = NewId();
        _local[id] = new LocalRoute(root);
        var name = Uri.EscapeDataString(Path.GetFileName(full));
        return new ProxyLease($"http://127.0.0.1:{_port.ToString(CultureInfo.InvariantCulture)}/l/{id}/{name}", () => _local.TryRemove(id, out _));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void EnsureStarted()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_listener is not null) return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(32);
            _listener = listener;
            _port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _client = new HttpClient(new SocketsHttpHandler
            {
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.None,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(20),
            })
            {
                Timeout = Timeout.InfiniteTimeSpan,
            };
            _ = Task.Run(() => AcceptLoopAsync(listener, _lifetime.Token));
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested || _disposed)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken lifetime)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cts.Token).ConfigureAwait(false);
                if (request is null) return;
                await RouteAsync(stream, request, cts.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or OperationCanceledException
                                          or HttpRequestException or InvalidOperationException)
            {
                // Client went away, upstream failed mid-transfer, or the proxy is shutting down: nothing to report.
            }
        }
    }

    private sealed record ProxyRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers);

    private static async Task<ProxyRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
            if (read == 0) return null;
            length += read;
            if (EndOfHeaders(buffer, length) >= 0) break;
        }

        var end = EndOfHeaders(buffer, length);
        if (end < 0) return null;
        var text = Encoding.ASCII.GetString(buffer, 0, end);
        var lines = text.Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0) continue;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }

        return new ProxyRequest(first[0].ToUpperInvariant(), first[1], headers);
    }

    private static int EndOfHeaders(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') return i - 3;
        }

        return -1;
    }

    private async Task RouteAsync(NetworkStream stream, ProxyRequest request, CancellationToken ct)
    {
        // DNS-rebinding guard: only serve requests that name the loopback host.
        // Exact match: a prefix test would also accept names such as "127.0.0.1.evil.example".
        if (!request.Headers.TryGetValue("Host", out var host)
            || !(host.Equals("127.0.0.1", StringComparison.Ordinal)
                 || host.Equals("127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
        {
            await WriteSimpleAsync(stream, 403, "Forbidden", ct).ConfigureAwait(false);
            return;
        }

        if (request.Method is not ("GET" or "HEAD"))
        {
            await WriteSimpleAsync(stream, 405, "Method Not Allowed", ct).ConfigureAwait(false);
            return;
        }

        var path = request.Path;
        var query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && parts[0] == "r" && _remote.TryGetValue(parts[1], out var remote))
        {
            await ServeRemoteAsync(stream, request, remote, ct).ConfigureAwait(false);
            return;
        }

        if (parts.Length >= 3 && parts[0] == "l" && _local.TryGetValue(parts[1], out var local))
        {
            var relative = string.Join('/', parts.Skip(2).Select(Uri.UnescapeDataString));
            await ServeLocalAsync(stream, request, local, relative, ct).ConfigureAwait(false);
            return;
        }

        await WriteSimpleAsync(stream, 404, "Not Found", ct).ConfigureAwait(false);
    }

    private async Task ServeRemoteAsync(NetworkStream stream, ProxyRequest request, RemoteRoute route, CancellationToken ct)
    {
        var client = _client;
        if (client is null) return;
        using var upstream = new HttpRequestMessage(request.Method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, route.Url);
        foreach (var (name, value) in route.Headers)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            upstream.Headers.TryAddWithoutValidation(name, value);
        }

        if (request.Headers.TryGetValue("Range", out var range)) upstream.Headers.TryAddWithoutValidation("Range", range);
        if (request.Headers.TryGetValue("If-Range", out var ifRange)) upstream.Headers.TryAddWithoutValidation("If-Range", ifRange);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            await WriteSimpleAsync(stream, 502, "Bad Gateway", ct).ConfigureAwait(false);
            return;
        }

        using (response)
        {
            var head = new StringBuilder();
            var code = (int)response.StatusCode;
            head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {code} {response.ReasonPhrase ?? "OK"}\r\n");
            var contentType = response.Content.Headers.ContentType?.ToString();
            if (contentType is not null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
            if (response.Content.Headers.ContentLength is { } len)
                head.Append(CultureInfo.InvariantCulture, $"Content-Length: {len}\r\n");
            if (response.Content.Headers.ContentRange is { } cr) head.Append("Content-Range: ").Append(cr).Append("\r\n");
            head.Append("Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
            if (request.Method == "HEAD") return;
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await body.CopyToAsync(stream, 64 * 1024, ct).ConfigureAwait(false);
        }
    }

    private static async Task ServeLocalAsync(NetworkStream stream, ProxyRequest request, LocalRoute route, string relative, CancellationToken ct)
    {
        var root = Path.GetFullPath(route.RootDirectory);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
        {
            await WriteSimpleAsync(stream, 404, "Not Found", ct).ConfigureAwait(false);
            return;
        }

        await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, true);
        var total = file.Length;
        long start = 0;
        var end = total - 1;
        var partial = false;
        if (request.Headers.TryGetValue("Range", out var range) && TryParseRange(range, total, out var s, out var e))
        {
            start = s;
            end = e;
            partial = true;
        }
        else if (request.Headers.ContainsKey("Range"))
        {
            var bad = $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{total.ToString(CultureInfo.InvariantCulture)}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(bad), ct).ConfigureAwait(false);
            return;
        }

        var length = total == 0 ? 0 : end - start + 1;
        var head = new StringBuilder();
        head.Append(partial ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
        head.Append("Content-Type: ").Append(ContentTypes.ForPath(full)).Append("\r\n");
        head.Append(CultureInfo.InvariantCulture, $"Content-Length: {length}\r\n");
        if (partial) head.Append(CultureInfo.InvariantCulture, $"Content-Range: bytes {start}-{end}/{total}\r\n");
        head.Append("Accept-Ranges: bytes\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()), ct).ConfigureAwait(false);
        if (request.Method == "HEAD" || length == 0) return;

        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct).ConfigureAwait(false);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            remaining -= read;
        }
    }

    /// <summary>Parses a single <c>bytes=a-b</c> / <c>bytes=a-</c> / <c>bytes=-n</c> range against <paramref name="total"/>.</summary>
    internal static bool TryParseRange(string header, long total, out long start, out long end)
    {
        start = 0;
        end = total - 1;
        const string prefix = "bytes=";
        if (total <= 0 || !header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var spec = header[prefix.Length..].Split(',')[0].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0) return false;
        var a = spec[..dash];
        var b = spec[(dash + 1)..];
        if (a.Length == 0)
        {
            if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, total - suffix);
            end = total - 1;
            return true;
        }

        if (!long.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start >= total) return false;
        if (b.Length == 0)
        {
            end = total - 1;
            return true;
        }

        if (!long.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start) return false;
        end = Math.Min(end, total - 1);
        return true;
    }

    private static async Task WriteSimpleAsync(NetworkStream stream, int code, string reason, CancellationToken ct)
    {
        var text = $"HTTP/1.1 {code.ToString(CultureInfo.InvariantCulture)} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text), ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _lifetime.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch (SocketException)
        {
        }

        _client?.Dispose();
        _lifetime.Dispose();
    }
}

/// <summary>Content types of the files a local download folder contains.</summary>
public static class ContentTypes
{
    public static string ForPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".m3u8" or ".m3u" => "application/vnd.apple.mpegurl",
        ".ts" => "video/mp2t",
        ".m4s" or ".mp4" or ".m4v" => "video/mp4",
        ".m4a" => "audio/mp4",
        ".aac" => "audio/aac",
        ".mp3" => "audio/mpeg",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".vtt" => "text/vtt",
        ".srt" => "application/x-subrip",
        ".key" => "application/octet-stream",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        _ => "application/octet-stream",
    };
}
