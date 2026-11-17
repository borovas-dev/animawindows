#pragma warning disable xUnit1051 // cancellation tokens are irrelevant for these in-memory tests
using System.Net;
using System.Text;

namespace AnimaWindows.Core.Tests.Net;

public sealed record RecordedRequest(Uri Uri, IReadOnlyDictionary<string, string> Headers);

/// <summary>Scripted HTTP handler: each request dequeues the next responder; no responders left = 500.</summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responders = new();
    private readonly List<RecordedRequest> _requests = [];

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests) return [.. _requests];
        }
    }

    public FakeHttpMessageHandler Enqueue(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        _responders.Enqueue(responder);
        return this;
    }

    public FakeHttpMessageHandler Enqueue(HttpStatusCode code, string body = "", params (string Name, string Value)[] headers) =>
        Enqueue(_ => Response(code, body, headers));

    public static HttpResponseMessage Response(HttpStatusCode code, string body = "", params (string Name, string Value)[] headers)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8) };
        foreach (var (n, v) in headers) r.Headers.TryAddWithoutValidation(n, v);
        return r;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_requests) _requests.Add(new RecordedRequest(request.RequestUri!, headers));
        Func<HttpRequestMessage, HttpResponseMessage>? responder = null;
        lock (_responders)
        {
            if (_responders.Count > 0) responder = _responders.Dequeue();
        }

        return Task.FromResult(responder is null ? Response(HttpStatusCode.InternalServerError) : responder(request));
    }
}
