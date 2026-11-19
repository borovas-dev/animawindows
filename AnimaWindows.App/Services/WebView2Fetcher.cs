using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Microsoft.UI.Xaml.Controls.WebView2? _view;
    private string? _origin;

    public WebView2Fetcher(ShellChrome chrome) => _chrome = chrome;

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri ?? throw new InvalidOperationException("Request has no URI.");
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var contentType = request.Content?.Headers.ContentType?.ToString();
        var referrer = request.Headers.Referrer?.AbsoluteUri;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                var raw = await OnUiAsync(() => FetchOnUiAsync(uri, request.Method.Method, body, contentType, referrer, cancellationToken)).ConfigureAwait(false);
                var response = ToResponse(raw, request);
                AppLog.Write($"WebView2 {request.Method} {uri} -> {(int)response.StatusCode}");
                return response;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                AppLog.Write($"WebView2 {request.Method} {uri} failed: {e.Message}");
                throw;
            }
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

        // Some pages (shinden.pl episode pages) answer fetch() with 403 and are only served to a real page load.
        if (method == "GET" && (result is null || StatusOf(result) == 403))
        {
            result = await LoadPageAsync(web, uri, referrer, ct).ConfigureAwait(true);
        }

        return result ?? throw new HttpRequestException($"WebView2 fetch failed for {uri}");
    }

    /// <summary>Navigates the hidden view to <paramref name="uri"/> as a top-level page load and returns its HTML as an envelope.</summary>
    private async Task<string> LoadPageAsync(CoreWebView2 web, Uri uri, string? referrer, CancellationToken ct)
    {
        var headers = referrer is null ? "" : "Referer: " + referrer + "\r\n";
        var request = web.Environment.CreateWebResourceRequest(uri.AbsoluteUri, "GET", null, headers);
        var status = await NavigateAsync(web, () => web.NavigateWithWebResourceRequest(request), ct).ConfigureAwait(true);
        var html = await web.ExecuteScriptAsync("document.documentElement.outerHTML");
        var text = JsonDocument.Parse(html).RootElement.GetString() ?? "";
        // TEMP diagnostics: where does the page keep its API token?
        var probe = await web.ExecuteScriptAsync(
            "(()=>{const o={};try{o.storage=typeof _Storage!=='undefined'?JSON.stringify(_Storage).slice(0,600):null}catch(e){o.storage=String(e)}" +
            "const h=document.documentElement.outerHTML;const re=/.{0,100}(auth|basic|api4|xhr).{0,140}/gi;o.hits=(h.match(re)||[]).slice(0,12);return JSON.stringify(o)})()");
        AppLog.Write("Page probe " + uri + ": " + probe);
        return "{\"ok\":true,\"s\":" + status + ",\"c\":\"text/html; charset=utf-8\",\"t\":" + Js(text) + "}";
    }

    private static int StatusOf(string envelope)
    {
        using var doc = JsonDocument.Parse(envelope);
        return doc.RootElement.TryGetProperty("s", out var s) ? s.GetInt32() : 0;
    }

    /// <summary>
    /// A regular XAML WebView2, 1x1 px, transparent and not focusable, parked in the window's root grid.
    /// (A raw CoreWebView2Controller on the WinUI window's HWND fail-fasts the process.)
    /// </summary>
    private async Task<CoreWebView2> EnsureWebViewAsync()
    {
        if (_view?.CoreWebView2 is { } existing)
        {
            return existing;
        }

        if (_chrome.Window.Content is not Microsoft.UI.Xaml.Controls.Panel root)
        {
            throw new InvalidOperationException("The window content is not a panel.");
        }

        _view = new Microsoft.UI.Xaml.Controls.WebView2
        {
            Width = 1,
            Height = 1,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
            HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Left,
            VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Top,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAccessibilityView(_view, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(_view, 2);
        root.Children.Add(_view);
        await _view.EnsureCoreWebView2Async();
        var web = _view.CoreWebView2;
        web.IsMuted = true;
        web.Settings.AreDevToolsEnabled = false;
        return web;
    }

    private Task<int> NavigateAsync(CoreWebView2 web, string url, CancellationToken ct) =>
        NavigateAsync(web, () => web.Navigate(url), ct);

    /// <summary>Starts a navigation, waits for it (and any Cloudflare JS challenge) to finish; returns the HTTP status.</summary>
    private async Task<int> NavigateAsync(CoreWebView2 web, Action start, CancellationToken ct)
    {
        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args) =>
            done.TrySetResult(args.HttpStatusCode != 0 ? args.HttpStatusCode : args.IsSuccess ? 200 : 502);
        web.NavigationCompleted += OnCompleted;
        int status;
        try
        {
            start();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(NavigationTimeout);
            status = await done.Task.WaitAsync(timeout.Token).ConfigureAwait(true);
        }
        finally
        {
            web.NavigationCompleted -= OnCompleted;
        }

        // A Cloudflare JS challenge solves itself in a real browser; wait for it to hand over to the site.
        var until = DateTime.UtcNow + ChallengeWait;
        var challenged = false;
        while (DateTime.UtcNow < until && IsChallengeTitle(await web.ExecuteScriptAsync("document.title")))
        {
            challenged = true;
            await Task.Delay(500, ct).ConfigureAwait(true);
        }

        _origin = new Uri(web.Source).GetLeftPart(UriPartial.Authority);
        return challenged ? 200 : status;
    }

    /// <summary>Runs fetch() in the page. Returns the JSON envelope, or null when the browser refused the request (CORS / network).</summary>
    private static async Task<string?> EvaluateFetchAsync(CoreWebView2 web, Uri uri, string method, string? body, string? contentType, string? referrer)
    {
        var init = new StringBuilder("{credentials:'include',redirect:'follow',method:").Append(Js(method));
        // No explicit referrer: the browser sends the page it is on, as the site's own scripts do (api4.shinden.pl
        // rejects player_load with the bare site root as referrer).

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

        // RequestUri cannot change after sending; the redirect target is not needed by Shinden.
        return new HttpResponseMessage((HttpStatusCode)(status == 0 ? 502 : status))
        {
            Content = content,
            RequestMessage = request,
        };
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
