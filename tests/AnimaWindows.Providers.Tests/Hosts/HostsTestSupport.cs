using System.Net;

namespace AnimaWindows.Providers.Tests.Hosts;

/// <summary>Fake <see cref="HttpMessageHandler"/>: records every request (path and query) and answers via a delegate. Does not follow redirects.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Referer, string? Body);

internal sealed class HostsFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly object _gate = new();
    private readonly List<string> _seen = [];
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<string> Seen
    {
        get { lock (_gate) return _seen.ToList(); }
    }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get { lock (_gate) return _requests.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        string? body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_gate)
        {
            _seen.Add(request.RequestUri!.PathAndQuery);
            _requests.Add(new RecordedRequest(
                request.Method, request.RequestUri, request.Headers.TryGetValues("Referer", out var r) ? r.FirstOrDefault() : null, body));
        }
        return respond(request);
    }

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode code, string? location = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent("") };
        if (location is not null) r.Headers.TryAddWithoutValidation("Location", location);
        return r;
    }
}

internal static class HostFixtures
{
    public static string Read(string folder, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", folder, name));
}
