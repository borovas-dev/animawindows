using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AnimaWindows.Core.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.Web.WebView2.Core;

namespace AnimaWindows.App.Services;

/// <summary>
/// Performs HTTP GET/POST requests from inside a hidden WebView2 (Edge) page with <c>fetch()</c>, so they use the
/// browser's own network stack and cookies. Used for sites that refuse .NET's HTTP client but accept a normal browser
/// (shinden.pl answers .NET with 403 "update your browser").
/// </summary>
public sealed class WebView2Fetcher
{
    private static readonly TimeSpan NavigationTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ChallengeWait = TimeSpan.FromSeconds(20);

    private readonly ShellChrome _chrome;
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Controller? _controller;
    private string? _origin;

    public WebView2Fetcher(ShellChrome chrome, IAppPaths paths)
    {
        _chrome = chrome;
        _userDataFolder = Path.Combine(paths.CacheDirectory, "webview-fetch");
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var contentType = request.Content?.Headers.ContentType?.ToString();
        var referrer = request.Headers.Referrer?.AbsoluteUri;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var raw = await OnUiAsync(() => FetchOnUiAsync(uri, request.Method.Method, body, contentType, referrer, cancellationToken)).ConfigureAwait(false);
            return ToResponse(raw, request);
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- UI thread ----

    private async Task<string> FetchOnUiAsync(Uri uri, string method, string? body, string? contentType, string? referrer, CancellationToken ct)
    {
        var web = await EnsureWebViewAsync().ConfigureAwait(true);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var siteOrigin = SiteOrigin(uri);

        // Stay on the site's main page so cookies and the browser's own CORS rules apply (the site itself calls its API
        // sub-domains from there). If a cross-origin fetch is refused, retry from the target origin.
        if (_origin is null || SiteOrigin(new Uri(_origin)) != siteOrigin)
        {
            await NavigateAsync(web, siteOrigin + "/", ct).ConfigureAwait(true);
        }

        var result = await EvaluateFetchAsync(web, uri, method, body, contentType, referrer).ConfigureAwait(true);
        if (result is null && _origin != origin)
        {
            await NavigateAsync(web, origin + "/", ct).ConfigureAwait(true);
            result = await EvaluateFetchAsync(web, uri, method, body, contentType, referrer).ConfigureAwait(true);
        }

        return result ?? throw new HttpRequestException($"WebView2 fetch failed for {uri}");
    }

    private async Task<CoreWebView2> EnsureWebViewAsync()
    {
        if (_controller is not null)
        {
            return _controller.CoreWebView2;
        }

        Directory.CreateDirectory(_userDataFolder);
        var env = await CoreWebView2Environment.CreateWithOptionsAsync("", _userDataFolder, new CoreWebView2EnvironmentOptions());
        var window = CoreWebView2ControllerWindowReference.CreateFromWindowHandle((ulong)_chrome.WindowHandle);
        _controller = await env.CreateCoreWebView2ControllerAsync(window);
        _controller.IsVisible = false;
        _controller.CoreWebView2.Settings.AreDevToolsEnabled = false;
        _controller.CoreWebView2.Settings.IsScriptEnabled = true;
        _controller.CoreWebView2.IsMuted = true;
        return _controller.CoreWebView2;
    }

    private async Task NavigateAsync(CoreWebView2 web, string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) => done.TrySetResult();
        web.NavigationCompleted += OnCompleted;
        try
        {
            web.Navigate(url);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NavigationTimeout);
            await done.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }

        // A Cloudflare JS challenge solves itself in a real browser; wait for it to hand over to the site.
        var until = DateTime.UtcNow + ChallengeWait;
        while (DateTime.UtcNow < until && IsChallengeTitle(await web.ExecuteScriptAsync("document.title")))
        {
            await Task.Delay(500, ct).ConfigureAwait(true);
        }

        _origin = new Uri(web.Source).GetLeftPart(UriPartial.Authority);
    }

    /// <summary>Runs fetch() in the page. Returns the JSON envelope, or null when the browser refused the request (CORS / network).</summary>
    private static async Task<string?> EvaluateFetchAsync(CoreWebView2 web, Uri uri, string method, string? body, string? contentType, string? referrer)
    {
        var init = new StringBuilder("{credentials:'include',redirect:'follow',method:").Append(Js(method));
        if (referrer is not null)
        {
            init.Append(",referrer:").Append(Js(referrer));
        }

        if (body is not null)
        {
            init.Append(",body:").Append(Js(body));
            if (contentType is not null)
            {
                init.Append(",headers:{'Content-Type':").Append(Js(contentType)).Append('}');
            }
        }

        init.Append('}');
        var script =
            "(async()=>{try{const r=await fetch(" + Js(uri.AbsoluteUri) + "," + init + ");" +
            "return JSON.stringify({ok:true,s:r.status,u:r.url,c:r.headers.get('content-type'),t:await r.text()});}" +
            "catch(e){return JSON.stringify({ok:false,e:String(e)});}})()";

        var args = "{\"expression\":" + Js(script) + ",\"awaitPromise\":true,\"returnByValue\":true}";
        var reply = await web.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args);
        using var doc = JsonDocument.Parse(reply);
        if (!doc.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("value", out var value))
        {
            return null;
        }

        var envelope = value.GetString();
        if (envelope is null)
        {
            return null;
        }

        using var inner = JsonDocument.Parse(envelope);
        return inner.RootElement.GetProperty("ok").GetBoolean() ? envelope : null;
    }

    // ---- helpers ----

    private static HttpResponseMessage ToResponse(string envelope, HttpRequestMessage request)
    {
        using var doc = JsonDocument.Parse(envelope);
        var root = doc.RootElement;
        var status = root.GetProperty("s").GetInt32();
        var text = root.GetProperty("t").GetString() ?? "";
        var content = new StringContent(text, Encoding.UTF8);
        if (root.TryGetProperty("c", out var c) && c.GetString() is { } type && MediaTypeHeaderValue.TryParse(type, out var parsed))
        {
            content.Headers.ContentType = parsed;
        }

        var response = new HttpResponseMessage((HttpStatusCode)(status == 0 ? 502 : status))
        {
            Content = content,
            RequestMessage = request,
        };
        if (root.TryGetProperty("u", out var u) && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var finalUri))
        {
            request.RequestUri = finalUri;
        }

        return response;
    }

    private static bool IsChallengeTitle(string titleJson) =>
        titleJson.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
        || titleJson.Contains("Chwileczk", StringComparison.OrdinalIgnoreCase);

    /// <summary>https://api4.shinden.pl → https://shinden.pl (last two labels).</summary>
    private static string SiteOrigin(Uri uri)
    {
        var labels = uri.Host.Split('.');
        var site = labels.Length > 2 ? string.Join('.', labels[^2..]) : uri.Host;
        return $"{uri.Scheme}://{site}";
    }

    private static string Js(string value) => "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

    private Task<T> OnUiAsync<T>(Func<Task<T>> work)
    {
        var queue = _chrome.Window.DispatcherQueue;
        if (queue.HasThreadAccess)
        {
            return work();
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.TryEnqueue(DispatcherQueuePriority.Normal, async () =>
        {
            try
            {
                tcs.TrySetResult(await work().ConfigureAwait(true));
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });
        if (!queued)
        {
            tcs.TrySetException(new InvalidOperationException("UI thread is not available."));
        }

        return tcs.Task;
    }
}

/// <summary>Sends requests for the listed site domains through <see cref="WebView2Fetcher"/>, everything else through <paramref name="fallback"/>.</summary>
public sealed class BrowserRoutingHandler(WebView2Fetcher fetcher, HttpClient fallback, params string[] domains) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = request.RequestUri?.Host ?? "";
        var viaBrowser = domains.Any(d => host.Equals(d, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
        return viaBrowser ? fetcher.SendAsync(request, cancellationToken) : fallback.SendAsync(request, cancellationToken);
    }
}
