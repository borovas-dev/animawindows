namespace AnimaWindows.App.Composition;

/// <summary>Second HTTP client on the shared cookies with auto-redirect disabled (Shinden/Sibnet needs it).</summary>
public sealed class NoRedirectHttpClient
{
    public NoRedirectHttpClient(HttpClient client) => Client = client;

    public HttpClient Client { get; }
}
