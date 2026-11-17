using System.Net;
using System.Net.Http.Headers;

namespace AnimaWindows.Core.Downloads;

/// <summary>Result of <see cref="HlsDownloader.DownloadAsync"/>.</summary>
/// <param name="PlaylistPath">Local media playlist (relative URIs into the resource directory).</param>
/// <param name="Bytes">Total size of everything stored for this playlist.</param>
public sealed record HlsDownloadResult(string PlaylistPath, long Bytes);

/// <summary>
/// Downloads one HLS media playlist: all segments, AES-128 keys and fMP4 init sections, with the stream headers, into
/// <c>&lt;directory&gt;/&lt;name&gt;/</c>, then writes the rewritten local playlist <c>&lt;directory&gt;/&lt;name&gt;.m3u8</c>.
/// Resumable: files already stored completely are skipped (every file is written under a <c>.part</c> name and renamed when
/// whole). Cancellation (pause) leaves the stored files in place. Replaces Media3's <c>HlsDownloader</c>; master playlists are
/// rejected (the planner picks one rendition first).
/// </summary>
public sealed class HlsDownloader
{
    private readonly HttpClient _http;
    private readonly int _parallelism;

    public HlsDownloader(HttpClient http, int parallelism = 4)
    {
        _http = http;
        _parallelism = Math.Max(1, parallelism);
    }

    private sealed class Counters
    {
        public long Bytes;
        public int Done;
        public int Total;
    }

    public async Task<HlsDownloadResult> DownloadAsync(
        Uri playlistUrl,
        string directory,
        string name,
        IReadOnlyDictionary<string, string> headers,
        Action<DownloadProgress>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(directory);
        var text = await DownloadHttp.GetStringAsync(_http, playlistUrl, headers, cancellationToken).ConfigureAwait(false);
        if (!text.TrimStart('﻿', ' ', '\r', '\n').StartsWith("#EXTM3U", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Not an M3U8 playlist: {playlistUrl.Host}{playlistUrl.AbsolutePath}");
        }

        if (HlsPlaylist.IsMaster(text))
        {
            throw new InvalidDataException("Master playlist given; choose a rendition first");
        }

        var playlist = HlsPlaylist.Parse(text, playlistUrl);
        var resourceDir = Path.Combine(directory, name);
        Directory.CreateDirectory(resourceDir);

        var counters = new Counters { Total = playlist.Resources.Count };
        var todo = new List<HlsResource>();
        foreach (var resource in playlist.Resources)
        {
            var path = Path.Combine(resourceDir, resource.LocalName);
            if (File.Exists(path))
            {
                counters.Bytes += new FileInfo(path).Length;
                counters.Done++;
            }
            else
            {
                todo.Add(resource);
            }
        }

        Report(counters, onProgress);
        var options = new ParallelOptions { MaxDegreeOfParallelism = _parallelism, CancellationToken = cancellationToken };
        await Parallel.ForEachAsync(todo, options, async (resource, ct) =>
        {
            var written = await DownloadResourceAsync(resource, Path.Combine(resourceDir, resource.LocalName), headers, ct).ConfigureAwait(false);
            Interlocked.Add(ref counters.Bytes, written);
            Interlocked.Increment(ref counters.Done);
            Report(counters, onProgress);
        }).ConfigureAwait(false);

        var playlistPath = Path.Combine(directory, name + ".m3u8");
        var tmp = playlistPath + ".part";
        await File.WriteAllTextAsync(tmp, playlist.Render(name), cancellationToken).ConfigureAwait(false);
        DownloadHttp.ReplaceFile(tmp, playlistPath);
        return new HlsDownloadResult(playlistPath, Interlocked.Read(ref counters.Bytes));
    }

    private static void Report(Counters c, Action<DownloadProgress>? onProgress)
    {
        if (onProgress is null)
        {
            return;
        }

        var done = Volatile.Read(ref c.Done);
        var percent = c.Total == 0 ? 1f : (float)done / c.Total;
        onProgress(new DownloadProgress(Interlocked.Read(ref c.Bytes), percent));
    }

    private async Task<long> DownloadResourceAsync(
        HlsResource resource, string path, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        using var request = DownloadHttp.CreateRequest(resource.Url, headers);
        var ranged = resource.Length is not null;
        var offset = resource.Offset ?? 0;
        if (ranged)
        {
            request.Headers.Range = new RangeHeaderValue(offset, offset + resource.Length!.Value - 1);
        }

        using var response = await DownloadHttp.SendAsync(_http, request, ct).ConfigureAwait(false);
        await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        if (ranged && response.StatusCode == HttpStatusCode.OK && offset > 0)
        {
            await DownloadHttp.SkipAsync(body, offset, ct).ConfigureAwait(false); // server ignored the Range header
        }

        var tmp = path + ".part";
        long written;
        await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, DownloadHttp.BufferSize, useAsync: true))
        {
            written = await DownloadHttp.CopyAsync(body, file, resource.Length, null, ct).ConfigureAwait(false);
        }

        if (ranged && written != resource.Length)
        {
            throw new IOException($"Short read for {resource.LocalName}: {written} of {resource.Length} bytes");
        }

        DownloadHttp.ReplaceFile(tmp, path);
        return written;
    }
}
