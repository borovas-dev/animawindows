using System.Net;
using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.Core.Net;

/// <summary>
/// Builds the app's shared <see cref="HttpClient"/> stack (Kotlin: OkHttp client with cookie jar + CloudflareInterceptor).
/// Not a DI abstraction: call once at composition and share the client and <see cref="CookieContainer"/> with the
/// <see cref="ICloudflareSolver"/> implementation so clearance cookies are visible to requests.
/// </summary>
public static class AnimaHttpClientFactory
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Handler chain: <see cref="CloudflareHandler"/> over a <see cref="SocketsHttpHandler"/> using <paramref name="cookies"/>.</summary>
    public static HttpMessageHandler CreateHandler(CookieContainer cookies, ICloudflareSolver? solver = null)
    {
        var sockets = new SocketsHttpHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = DefaultTimeout,
            AllowAutoRedirect = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        return new CloudflareHandler(solver, sockets);
    }

    /// <summary>Creates a client with the default user agent (<see cref="NetworkConstants.AniCliUserAgent"/>) and 15 s timeout.</summary>
    public static HttpClient Create(CookieContainer cookies, ICloudflareSolver? solver = null)
    {
        var client = new HttpClient(CreateHandler(cookies, solver)) { Timeout = DefaultTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(NetworkConstants.AniCliUserAgent);
        return client;
    }
}
