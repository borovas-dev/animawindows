using System.Net;
using System.Text;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Providers.Tests.AnimeFire;

/// <summary>What the fake handler saw for one request.</summary>
internal sealed record RecordedRequest(string Url, string Path, string? Referer, string? AcceptLanguage, string? UserAgent);

/// <summary>HttpMessageHandler that answers through <see cref="Route"/> and records every request.</summary>
internal sealed class RoutingHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, HttpResponseMessage> Route { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    public List<RecordedRequest> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        Requests.Add(new RecordedRequest(
            uri.AbsoluteUri,
            uri.AbsolutePath,
            Header(request, "Referer"),
            Header(request, "Accept-Language"),
            Header(request, "User-Agent")));
        // A synchronous throw from Route surfaces to HostHttp as a connection failure, like a refused socket.
        return Task.FromResult(Route(request));
    }

    private static string? Header(HttpRequestMessage request, string name) =>
        request.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;
}

/// <summary>Reads captured fixtures copied to the test output folder.</summary>
internal static class Fixture
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "AnimeFire", name));

    public static HttpResponseMessage Text(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
}

/// <summary>Remote config stub; tests swap <see cref="Config"/> to simulate base URL changes.</summary>
internal sealed class StubConfig : IProviderConfig
{
    public ProviderRemoteConfig Config { get; set; } = ProviderRemoteConfig.Default;

    public ProviderRemoteConfig ForProvider(string id) => Config;

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
