#pragma warning disable xUnit1051
using System.Net;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Tests.Tracking;

namespace AnimaWindows.Core.Tests.Downloads;

public sealed class HlsPlaylistTests
{
    private static readonly Uri Base = new("https://cdn.test/a/b/index.m3u8");

    [Fact]
    public void Parse_FindsSegmentsKeysAndMaps_AndRendersLocalPaths()
    {
        const string text =
            "#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXT-X-MEDIA-SEQUENCE:7\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"../key.bin\",IV=0xAB\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n" +
            "#EXTINF:4,\nseg0.m4s?x=1\n#EXTINF:4,\nhttps://other.test/seg1.m4s\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"../key.bin\",IV=0xAC\n#EXTINF:4,\nseg2.m4s\n#EXT-X-ENDLIST\n";
        var pl = HlsPlaylist.Parse(text, Base);

        Assert.Equal(3, pl.SegmentCount);
        Assert.Equal(5, pl.Resources.Count); // 3 segments + 1 shared key + 1 map
        Assert.Contains(pl.Resources, r => r.Kind == HlsResourceKind.Key && r.Url.AbsoluteUri == "https://cdn.test/a/key.bin");
        Assert.Contains(pl.Resources, r => r.Url.AbsoluteUri == "https://other.test/seg1.m4s");

        var local = pl.Render("main");
        Assert.DoesNotContain("http", local);
        Assert.Contains("#EXT-X-MEDIA-SEQUENCE:7", local);
        Assert.Contains("#EXT-X-KEY:METHOD=AES-128,URI=\"main/key0.key\",IV=0xAB", local);
        Assert.Contains("#EXT-X-KEY:METHOD=AES-128,URI=\"main/key0.key\",IV=0xAC", local);
        Assert.Contains("#EXT-X-MAP:URI=\"main/init0.mp4\"", local);
        Assert.Contains("main/seg00000.m4s\n", local);
        Assert.Contains("main/seg00001.m4s\n", local);
        Assert.Contains("main/seg00002.m4s\n", local);
    }

    [Fact]
    public void Parse_ByteRanges_BecomeSeparateFilesWithoutRangeTags()
    {
        const string text =
            "#EXTM3U\n#EXT-X-MAP:URI=\"media.mp4\",BYTERANGE=\"10@0\"\n" +
            "#EXTINF:4,\n#EXT-X-BYTERANGE:20@10\nmedia.mp4\n#EXTINF:4,\n#EXT-X-BYTERANGE:30\nmedia.mp4\n#EXT-X-ENDLIST\n";
        var pl = HlsPlaylist.Parse(text, Base);

        var map = pl.Resources.Single(r => r.Kind == HlsResourceKind.Map);
        Assert.Equal((0L, 10L), (map.Offset!.Value, map.Length!.Value));
        var segs = pl.Resources.Where(r => r.Kind == HlsResourceKind.Segment).ToList();
        Assert.Equal((10L, 20L), (segs[0].Offset!.Value, segs[0].Length!.Value));
        Assert.Equal((30L, 30L), (segs[1].Offset!.Value, segs[1].Length!.Value)); // implicit offset follows the previous range

        var local = pl.Render("v");
        Assert.DoesNotContain("BYTERANGE", local);
        Assert.Contains("#EXT-X-MAP:URI=\"v/init0.mp4\"\n", local);
    }

    [Fact]
    public void Parse_KeyMethodNone_AndUnsupportedSchemes_AreLeftAlone()
    {
        const string text = "#EXTM3U\n#EXT-X-KEY:METHOD=NONE\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"skd://abc\"\n#EXTINF:4,\ns.ts\n";
        var pl = HlsPlaylist.Parse(text, Base);
        Assert.Single(pl.Resources);
        Assert.Contains("skd://abc", pl.Render("x"));
    }

    [Fact]
    public void GetAttribute_HandlesQuotedCommas()
    {
        const string line = "#EXT-X-KEY:METHOD=AES-128,URI=\"https://k/x?a=1,2\",IV=0x01";
        Assert.Equal("AES-128", HlsPlaylist.GetAttribute(line, "METHOD"));
        Assert.Equal("https://k/x?a=1,2", HlsPlaylist.GetAttribute(line, "URI"));
        Assert.Equal("0x01", HlsPlaylist.GetAttribute(line, "IV"));
        Assert.Null(HlsPlaylist.GetAttribute(line, "KEYFORMAT"));
    }

    [Fact]
    public void Master_IsDetected_AndVariantsParsed()
    {
        const string master = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000,RESOLUTION=1280x720,CODECS=\"a,b\"\nv/720.m3u8\n";
        Assert.True(HlsPlaylist.IsMaster(master));
        Assert.False(HlsPlaylist.IsMaster("#EXTM3U\n#EXTINF:4,\ns.ts\n"));
        var v = Assert.Single(HlsPlaylist.ParseVariants(master, Base));
        Assert.Equal(720, v.Height);
        Assert.Equal(1000, v.Bandwidth);
        Assert.Equal("https://cdn.test/a/b/v/720.m3u8", v.Url.AbsoluteUri);
    }
}

public sealed class HlsDownloaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeCdn _cdn = new();
    private readonly Dictionary<string, string> _headers = new() { ["Referer"] = "https://site.test/" };

    public void Dispose() => _dir.Dispose();

    private HlsDownloader Downloader(int parallelism = 1) => new(new HttpClient(_cdn), parallelism);

    private static Uri PlaylistUrl => new("https://cdn.test/v/index.m3u8");

    [Fact]
    public async Task Downloads_SegmentsKeyAndInit_WithHeaders_AndWritesLocalPlaylist()
    {
        _cdn.Put("/v/index.m3u8", Media.Playlist(3, key: "../key.bin", map: "init.mp4"));
        _cdn.Files["/key.bin"] = Media.Bytes(9, 16);
        _cdn.Files["/v/init.mp4"] = Media.Bytes(8, 40);
        for (var i = 0; i < 3; i++)
        {
            _cdn.Files[$"/v/seg{i}.ts"] = Media.Bytes(i, 100);
        }

        var progress = new List<DownloadProgress>();
        var result = await Downloader(1).DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers, progress.Add);

        Assert.Equal(Path.Combine(_dir.Path, "main.m3u8"), result.PlaylistPath);
        Assert.Equal((3 * 100) + 16 + 40, result.Bytes);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(Media.Bytes(i, 100), await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", $"seg{i:D5}.ts")));
        }

        Assert.Equal(Media.Bytes(9, 16), await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "key0.key")));
        Assert.Equal(Media.Bytes(8, 40), await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "init0.mp4")));
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir.Path, "main"), "*.part"));

        var local = await File.ReadAllTextAsync(result.PlaylistPath);
        Assert.DoesNotContain("http", local);
        Assert.Contains("main/seg00000.ts", local);
        Assert.Contains("URI=\"main/key0.key\"", local);
        Assert.Contains("URI=\"main/init0.mp4\"", local);
        Assert.Contains("IV=0x00000000000000000000000000000001", local);

        Assert.All(_cdn.Requests, r => Assert.Equal("https://site.test/", r.Headers["Referer"]));
        Assert.Equal(1f, progress[^1].Percent);
        Assert.Equal(result.Bytes, progress[^1].Bytes);
        Assert.True(progress.Zip(progress.Skip(1)).All(p => p.Second.Percent >= p.First.Percent));
    }

    [Fact]
    public async Task Resume_SkipsCompleteSegments_AndFinishesTheRest()
    {
        _cdn.Put("/v/index.m3u8", Media.Playlist(4));
        for (var i = 0; i < 4; i++)
        {
            _cdn.Files[$"/v/seg{i}.ts"] = Media.Bytes(i, 100);
        }

        var fail = true;
        _cdn.Fault = r => fail && r.Path.EndsWith("/seg2.ts", StringComparison.Ordinal) ? HttpStatusCode.ServiceUnavailable : null;
        var downloader = Downloader(parallelism: 1);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => downloader.DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.StatusCode);
        Assert.False(File.Exists(Path.Combine(_dir.Path, "main.m3u8"))); // playlist only when everything is there
        Assert.True(File.Exists(Path.Combine(_dir.Path, "main", "seg00001.ts")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "main", "seg00002.ts")));

        fail = false;
        while (_cdn.Requests.TryDequeue(out _))
        {
        }

        var progress = new List<DownloadProgress>();
        var result = await downloader.DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers, progress.Add);

        Assert.True(File.Exists(result.PlaylistPath));
        Assert.Equal(400, result.Bytes);
        var fetched = _cdn.Requests.Select(r => r.Path).Where(p => p.EndsWith(".ts", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "/v/seg2.ts", "/v/seg3.ts" }, fetched); // seg0/seg1 were not downloaded again
        Assert.Equal(0.5f, progress[0].Percent); // resumed state is reported first
        Assert.Equal(1f, progress[^1].Percent);
    }

    [Fact]
    public async Task Cancellation_PausesAndKeepsStoredSegments()
    {
        _cdn.Put("/v/index.m3u8", Media.Playlist(3));
        for (var i = 0; i < 3; i++)
        {
            _cdn.Files[$"/v/seg{i}.ts"] = Media.Bytes(i, 100);
        }

        using var cts = new CancellationTokenSource();
        _cdn.Before = r =>
        {
            if (r.Path.EndsWith("/seg1.ts", StringComparison.Ordinal))
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers, null, cts.Token));
        Assert.True(File.Exists(Path.Combine(_dir.Path, "main", "seg00000.ts")));
        Assert.False(File.Exists(Path.Combine(_dir.Path, "main.m3u8")));
    }

    [Fact]
    public async Task ByteRangeSegments_AreFetchedWithRange_AndStoredAsOwnFiles()
    {
        var media = Media.Bytes(1, 60);
        _cdn.Files["/v/media.mp4"] = media;
        _cdn.Put(
            "/v/index.m3u8",
            "#EXTM3U\n#EXT-X-MAP:URI=\"media.mp4\",BYTERANGE=\"10@0\"\n" +
            "#EXTINF:4,\n#EXT-X-BYTERANGE:20@10\nmedia.mp4\n#EXTINF:4,\n#EXT-X-BYTERANGE:30\nmedia.mp4\n#EXT-X-ENDLIST\n");

        var result = await Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers);

        Assert.Equal(60, result.Bytes);
        Assert.Equal(media[..10], await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "init0.mp4")));
        Assert.Equal(media[10..30], await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "seg00000.ts")));
        Assert.Equal(media[30..60], await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "seg00001.ts")));
        Assert.Contains(_cdn.Requests, r => r.Range == "bytes=30-59");
        Assert.DoesNotContain("BYTERANGE", await File.ReadAllTextAsync(result.PlaylistPath));
    }

    [Fact]
    public async Task ByteRange_WorksWhenServerIgnoresRangeHeader()
    {
        var media = Media.Bytes(2, 60);
        _cdn.IgnoreRange = true;
        _cdn.Files["/v/media.mp4"] = media;
        _cdn.Put("/v/index.m3u8", "#EXTM3U\n#EXTINF:4,\n#EXT-X-BYTERANGE:20@10\nmedia.mp4\n#EXT-X-ENDLIST\n");

        await Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers);

        Assert.Equal(media[10..30], await File.ReadAllBytesAsync(Path.Combine(_dir.Path, "main", "seg00000.ts")));
    }

    [Fact]
    public async Task MasterPlaylist_AndGarbage_AreRejected()
    {
        _cdn.Put("/v/index.m3u8", "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nx.m3u8\n");
        await Assert.ThrowsAsync<InvalidDataException>(() => Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers));
        _cdn.Put("/v/index.m3u8", "<html>blocked</html>");
        await Assert.ThrowsAsync<InvalidDataException>(() => Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers));
    }

    [Fact]
    public async Task HttpErrors_CarryTheStatusCode()
    {
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => Downloader().DownloadAsync(PlaylistUrl, _dir.Path, "main", _headers));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
    }
}

public sealed class ProgressiveDownloaderTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeCdn _cdn = new();
    private readonly Dictionary<string, string> _headers = new() { ["Referer"] = "https://site.test/" };

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Downloads_WholeFile_WithHeaders()
    {
        var data = Media.Bytes(3, 1000);
        _cdn.Files["/f.mp4"] = data;
        var progress = new List<DownloadProgress>();
        var path = Path.Combine(_dir.Path, "sub", "main.mp4");

        var size = await new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/f.mp4"), path, _headers, progress.Add);

        Assert.Equal(1000, size);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".part"));
        Assert.Equal("https://site.test/", _cdn.Requests.Single().Headers["Referer"]);
        Assert.Equal(new DownloadProgress(1000, 1f), progress[^1]);
    }

    [Fact]
    public async Task Resumes_FromPartialFile_WithRange()
    {
        var data = Media.Bytes(4, 1000);
        _cdn.Files["/f.mp4"] = data;
        var path = Path.Combine(_dir.Path, "main.mp4");
        await File.WriteAllBytesAsync(path + ".part", data[..400]);

        var size = await new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/f.mp4"), path, _headers);

        Assert.Equal(1000, size);
        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Equal("bytes=400-", _cdn.Requests.Single().Range);
    }

    [Fact]
    public async Task ServerIgnoringRange_RestartsFromScratch()
    {
        var data = Media.Bytes(5, 500);
        _cdn.Files["/f.mp4"] = data;
        _cdn.IgnoreRange = true;
        var path = Path.Combine(_dir.Path, "main.mp4");
        await File.WriteAllBytesAsync(path + ".part", data[..100]);

        await new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/f.mp4"), path, _headers);

        Assert.Equal(data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task StalePartial_416_RestartsOnce()
    {
        var data = Media.Bytes(6, 300);
        _cdn.Files["/f.mp4"] = data;
        var path = Path.Combine(_dir.Path, "main.mp4");
        await File.WriteAllBytesAsync(path + ".part", new byte[900]); // longer than the file: range not satisfiable

        await new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/f.mp4"), path, _headers);

        Assert.Equal(data, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, _cdn.Requests.Count);
    }

    [Fact]
    public async Task ExistingFinalFile_IsNotDownloadedAgain()
    {
        var path = Path.Combine(_dir.Path, "main.mp4");
        await File.WriteAllBytesAsync(path, new byte[10]);
        var size = await new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/f.mp4"), path, _headers);
        Assert.Equal(10, size);
        Assert.Empty(_cdn.Requests);
    }

    [Fact]
    public async Task HttpError_Throws_WithStatus()
    {
        var path = Path.Combine(_dir.Path, "x.vtt");
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => new ProgressiveDownloader(new HttpClient(_cdn)).DownloadAsync(new Uri("https://cdn.test/missing.vtt"), path, _headers));
        Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
        Assert.False(File.Exists(path));
    }
}
