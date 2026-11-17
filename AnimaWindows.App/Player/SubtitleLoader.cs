using System.Net;
using System.Net.Http;
using System.Text;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Subtitles;

namespace AnimaWindows.App.Player;

/// <summary>Downloads (with the stream headers) or reads a subtitle track and parses it into a <see cref="CueTimeline"/>.</summary>
public sealed class SubtitleLoader : IDisposable
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public async Task<CueTimeline> LoadAsync(SubtitleTrack track, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var text = await ReadAsync(track.Url, headers, cancellationToken);
        return new CueTimeline(SubtitleParser.Parse(text));
    }

    private async Task<string> ReadAsync(string url, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var local) && local.IsFile)
        {
            var bytes = await File.ReadAllBytesAsync(local.LocalPath, ct);
            return Encoding.UTF8.GetString(bytes);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        foreach (var (name, value) in headers)
        {
            if (!string.IsNullOrWhiteSpace(name)) request.Headers.TryAddWithoutValidation(name, value);
        }

        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(data);
    }

    public void Dispose() => _http.Dispose();
}
