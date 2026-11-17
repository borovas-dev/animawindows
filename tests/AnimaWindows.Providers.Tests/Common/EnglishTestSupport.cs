using System.Collections.Specialized;
using System.Net;
using System.Web;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Providers.Tests.Common;

/// <summary>Loads fixture files copied next to the test assembly (<c>Fixtures/&lt;dir&gt;/&lt;name&gt;</c>).</summary>
internal static class EnglishFixtures
{
    public static string Read(string dir, string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", dir, name));
}

/// <summary>A request as seen by <see cref="EnglishFakeHandler"/>.</summary>
internal sealed record EnglishRecordedRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string Path => Uri.AbsolutePath;
    public NameValueCollection Query => HttpUtility.ParseQueryString(Uri.Query);
    public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
}

/// <summary>Fake <see cref="HttpMessageHandler"/>: records every request and answers through a delegate (which may throw to simulate connection failures).</summary>
internal sealed class EnglishFakeHandler : HttpMessageHandler
{
    private readonly Func<EnglishRecordedRequest, HttpResponseMessage> _respond;
    private readonly object _gate = new();
    private readonly List<EnglishRecordedRequest> _requests = [];

    public EnglishFakeHandler(Func<EnglishRecordedRequest, HttpResponseMessage> respond) => _respond = respond;

    public IReadOnlyList<EnglishRecordedRequest> Requests
    {
        get
        {
            lock (_gate) return _requests.ToList();
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.NonValidated.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        var recorded = new EnglishRecordedRequest(request.Method.Method, request.RequestUri!, headers, body);
        lock (_gate) _requests.Add(recorded);
        return _respond(recorded);
    }

    public static HttpResponseMessage Ok(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    public static HttpResponseMessage Status(HttpStatusCode code, string body = "") => new(code) { Content = new StringContent(body) };
}

/// <summary>Minimal <see cref="IProviderConfig"/> backed by a delegate, so tests can change the config between calls.</summary>
internal sealed class EnglishTestConfig : IProviderConfig
{
    private readonly Func<ProviderRemoteConfig> _config;

    public EnglishTestConfig(Func<ProviderRemoteConfig> config) => _config = config;

    public ProviderRemoteConfig ForProvider(string id) => _config();

    public event EventHandler? Changed
    {
        add { }
        remove { }
    }
}
