using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Net;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace AnimaWindows.App.Services;

/// <summary>
/// Solves Cloudflare browser checks in a <see cref="ContentDialog"/> hosting a WebView2 that uses the same user agent as the
/// HTTP client. Polls the WebView2 cookie jar for <c>cf_clearance</c> and copies every cookie of the site into the shared
/// <see cref="CookieContainer"/>. Calls are serialised; concurrent callers for the same host share the result.
/// </summary>
public sealed class WebView2CloudflareSolver : ICloudflareSolver
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReuseWindow = TimeSpan.FromSeconds(30);

    private readonly CookieContainer _cookies;
    private readonly IDispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private string? _lastSolvedHost;
    private DateTimeOffset _lastSolvedAt;

    public WebView2CloudflareSolver(CookieContainer cookies, IDispatcher dispatcher, IDialogService dialogs)
    {
        _cookies = cookies;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
    }

    public event EventHandler? Solved;

    public async Task<bool> SolveAsync(Uri url, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastSolvedHost == url.Host && DateTimeOffset.UtcNow - _lastSolvedAt < ReuseWindow)
            {
                return true;
            }

            var solved = await _dispatcher
                .InvokeAsync(() => RunDialogAsync(url, timeout ?? DefaultTimeout, cancellationToken))
                .ConfigureAwait(false);
            if (solved)
            {
                _lastSolvedHost = url.Host;
                _lastSolvedAt = DateTimeOffset.UtcNow;
                Solved?.Invoke(this, EventArgs.Empty);
            }

            return solved;
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<bool> RunDialogAsync(Uri url, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var webView = new WebView2
        {
            Width = 760,
            Height = 520,
        };
        var dialog = new ContentDialog
        {
            Title = Strings.Get("Cloudflare_VerifyTitle"),
            Content = webView,
            CloseButtonText = Strings.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        var loaded = new TaskCompletionSource();
        webView.Loaded += (_, _) => loaded.TrySetResult();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var show = _dialogs.ShowAsync(dialog);
        var solved = false;
        try
        {
            var first = await Task.WhenAny(loaded.Task, show, Task.Delay(timeout, cts.Token));
            if (first == loaded.Task && !show.IsCompleted)
            {
                await webView.EnsureCoreWebView2Async();
                var core = webView.CoreWebView2;
                core.Settings.UserAgent = NetworkConstants.AniCliUserAgent;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;

                // Restrict the embedded browser to the site being verified: no popups, no top-level navigation elsewhere.
                core.NewWindowRequested += (_, args) => args.Handled = true;
                core.NavigationStarting += (_, args) =>
                {
                    if (!IsSameSite(url, args.Uri))
                    {
                        args.Cancel = true;
                    }
                };
                core.Navigate(url.AbsoluteUri);

                while (!show.IsCompleted && !cts.IsCancellationRequested)
                {
                    if (await TryCopyClearanceAsync(webView.CoreWebView2, url))
                    {
                        solved = true;
                        break;
                    }

                    try
                    {
                        await Task.Delay(PollInterval, cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception)
        {
            // WebView2 runtime missing or navigation failure: report "not solved".
        }
        finally
        {
            if (!show.IsCompleted)
            {
                dialog.Hide();
                try
                {
                    await show;
                }
                catch (Exception)
                {
                    // Dialog already closed.
                }
            }

            try
            {
                webView.Close();
            }
            catch (Exception)
            {
                // Already disposed.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return solved;
    }

    /// <summary>
    /// True for http(s) targets on the verified host or a parent/sub domain of it (mirrors redirect between www and apex);
    /// about:blank is allowed because the challenge page uses it internally.
    /// </summary>
    private static bool IsSameSite(Uri site, string target)
    {
        if (string.Equals(target, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        var a = site.IdnHost;
        var b = uri.IdnHost;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
               || b.EndsWith("." + a, StringComparison.OrdinalIgnoreCase)
               || a.EndsWith("." + b, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<bool> TryCopyClearanceAsync(CoreWebView2 core, Uri url)
    {
        var origin = url.GetLeftPart(UriPartial.Authority);
        var cookies = await core.CookieManager.GetCookiesAsync(origin);
        if (!cookies.Any(c => c.Name == "cf_clearance"))
        {
            return false;
        }

        foreach (var c in cookies)
        {
            try
            {
                var cookie = new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Domain)
                {
                    HttpOnly = c.IsHttpOnly,
                    Secure = c.IsSecure,
                };
                if (!c.IsSession)
                {
                    cookie.Expires = DateTimeOffset.FromUnixTimeMilliseconds((long)(c.Expires * 1000)).UtcDateTime;
                }

                _cookies.Add(cookie);
            }
            catch (Exception)
            {
                // A malformed cookie must not break the others.
            }
        }

        return true;
    }
}
