using System.Net.Http.Headers;
using System.Text;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;

namespace AnimaWindows.Providers.Common;

/// <summary>HTTP error that keeps the status code so callers can fall back (e.g. /series vs /titles). Mirrors <c>HttpStatusException</c> in HostHttp.kt.</summary>
public sealed class HttpStatusException : Exception
{
    public HttpStatusException(int code, string url) : base($"Request failed: HTTP {code} from {url}")
    {
        Code = code;
        Url = url;
    }

    public int Code { get; }
    public string Url { get; }
}

/// <summary>Port of <c>CloudflareDetector</c> in core/network/CloudflareInterceptor.kt.</summary>
public static class CloudflareDetector
{
    /// <summary>403/503 with "Just a moment" body or <c>cf-mitigated: challenge</c> header.</summary>
    public static bool IsChallenge(int code, string? cfMitigatedHeader, Func<string> bodyPrefix)
    {
        if (code != 403 && code != 503) return false;
        if (string.Equals(cfMitigatedHeader, "challenge", StringComparison.OrdinalIgnoreCase)) return true;
        return bodyPrefix().Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsChallenge(HttpResponseMessage response, string body)
    {
        string? mitigated = response.Headers.TryGetValues("cf-mitigated", out var v) ? v.FirstOrDefault() : null;
        return IsChallenge((int)response.StatusCode, mitigated, () => body);
    }
}

/// <summary>
/// Thin <see cref="HttpClient"/> wrapper shared by providers and the host extractors. Mirrors <c>HostHttp</c> in
/// provider/common/HostHttp.kt. Cloudflare walls surface as <see cref="CloudflareChallengeException"/>; connection failures
/// and timeouts as <see cref="ProviderException"/> (inner exception kept so <see cref="Mirrors"/> can classify them).
/// </summary>
public sealed class HostHttp
{
    private readonly HttpClient _client;
    private readonly HttpClient? _noRedirectClient;
    private readonly string _acceptLanguage;

    /// <param name="client">Injected client (shares cookies with the Cloudflare solver).</param>
    /// <param name="acceptLanguage">Value of the Accept-Language header sent on every request.</param>
    /// <param name="noRedirectClient">Optional client with auto-redirect disabled, used by <see cref="RedirectTargetAsync"/>.</param>
    public HostHttp(HttpClient client, string acceptLanguage = "en-US,en;q=0.9", HttpClient? noRedirectClient = null)
    {
        _client = client;
        _acceptLanguage = acceptLanguage;
        _noRedirectClient = noRedirectClient;
    }

    /// <summary>
    /// GET returning the body text. Non-2xx surfaces as <see cref="ProviderException"/> (inner <see cref="HttpStatusException"/>).
    /// <paramref name="headers"/> override the defaults (User-Agent, Accept-Language, Referer, Origin) when they use the same name.
    /// </summary>
    public async Task<string> GetStringAsync(
        string url, string? referer = null, string? userAgent = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await GetCheckedAsync(url, referer, userAgent, headers, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpStatusException e)
        {
            throw new ProviderException(e.Message, e);
        }
    }

    /// <summary>Like <see cref="GetStringAsync"/> but a non-2xx status surfaces as <see cref="HttpStatusException"/> so callers can branch on the code.</summary>
    public Task<string> GetCheckedAsync(
        string url, string? referer = null, string? userAgent = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        ExecuteAsync(_client, () => BuildRequest(HttpMethod.Get, url, referer, null, userAgent, headers), url, (resp, text) =>
        {
            if (!resp.IsSuccessStatusCode) throw new HttpStatusException((int)resp.StatusCode, url);
            return text;
        }, cancellationToken);

    /// <summary>POSTs <paramref name="jsonBody"/> as application/json and returns the response text.</summary>
    public Task<string> PostJsonAsync(
        string url, string jsonBody, string? referer = null, string? origin = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default) =>
        PostAsync(url, () =>
        {
            var content = new StringContent(jsonBody, Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return content;
        }, referer, origin, headers, cancellationToken);

    /// <summary>POSTs <paramref name="form"/> as application/x-www-form-urlencoded and returns the response text.</summary>
    public Task<string> PostFormAsync(
        string url, IEnumerable<KeyValuePair<string, string>> form, string? referer = null, string? origin = null,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        var pairs = form.ToList();
        return PostAsync(url, () => new FormUrlEncodedContent(pairs), referer, origin, headers, cancellationToken);
    }

    /// <summary>Resolves a single redirect hop without following it; returns the absolute <c>Location</c> or null when not a redirect.</summary>
    public Task<string?> RedirectTargetAsync(string url, string? referer = null, CancellationToken cancellationToken = default)
    {
        if (_noRedirectClient is null)
        {
            // Fallback: the shared client follows redirects, so report where it ended up.
            return ExecuteAsync<string?>(_client, () => BuildRequest(HttpMethod.Get, url, referer, null, null, null), url, (resp, _) =>
            {
                var final = resp.RequestMessage?.RequestUri?.AbsoluteUri;
                return final is not null && !string.Equals(final, new Uri(url).AbsoluteUri, StringComparison.Ordinal) ? final : null;
            }, cancellationToken);
        }
        return ExecuteAsync<string?>(_noRedirectClient, () => BuildRequest(HttpMethod.Get, url, referer, null, null, null), url, (resp, _) =>
        {
            var location = resp.Headers.Location;
            if (location is null) return null;
            return location.IsAbsoluteUri ? location.AbsoluteUri : new Uri(new Uri(url), location).AbsoluteUri;
        }, cancellationToken);
    }

    private Task<string> PostAsync(
        string url, Func<HttpContent> content, string? referer, string? origin,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
        ExecuteAsync(_client, () =>
        {
            var req = BuildRequest(HttpMethod.Post, url, referer, origin, null, headers);
            req.Content = content();
            return req;
        }, url, (resp, text) =>
        {
            if (!resp.IsSuccessStatusCode)
            {
                var status = new HttpStatusException((int)resp.StatusCode, url);
                throw new ProviderException(status.Message, status);
            }
            return text;
        }, cancellationToken);

    private HttpRequestMessage BuildRequest(
        HttpMethod method, string url, string? referer, string? origin, string? userAgent,
        IReadOnlyDictionary<string, string>? headers)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? NetworkConstants.AniCliUserAgent);
        req.Headers.TryAddWithoutValidation("Accept-Language", _acceptLanguage);
        if (referer is not null) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin is not null) req.Headers.TryAddWithoutValidation("Origin", origin);
        if (headers is not null)
        {
            foreach (var (k, v) in headers)
            {
                req.Headers.Remove(k);
                req.Headers.TryAddWithoutValidation(k, v);
            }
        }
        return req;
    }

    private static async Task<T> ExecuteAsync<T>(
        HttpClient client, Func<HttpRequestMessage> request, string url,
        Func<HttpResponseMessage, string, T> handle, CancellationToken cancellationToken)
    {
        try
        {
            using var req = request();
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var code = (int)resp.StatusCode;
            var isRedirect = code is >= 300 and <= 399;
            var text = isRedirect ? "" : await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (CloudflareDetector.IsChallenge(resp, text)) throw new CloudflareChallengeException(url);
            return handle(resp, text);
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException($"Connection error: could not fetch {url} ({e.Message})", e);
        }
        catch (IOException e)
        {
            throw new ProviderException($"Connection error: could not fetch {url} ({e.Message})", e);
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            // HttpClient.Timeout surfaces as a cancellation that the caller did not request.
            throw new ProviderException($"Connection error: could not fetch {url} (timed out)", new TimeoutException("Request timed out", e));
        }
    }
}

/// <summary>Mirror failover. Mirrors <c>withMirrors</c> / <c>isMirrorRetryable</c> in provider/common/Mirrors.kt.</summary>
public static class Mirrors
{
    /// <summary>True for connection failures, timeouts and 5xx responses (as produced by <see cref="HostHttp"/>); other failures would repeat on a mirror.</summary>
    public static bool IsMirrorRetryable(this ProviderException exception)
    {
        Exception? c = exception.InnerException;
        for (var i = 0; i < 8 && c is not null; i++)
        {
            switch (c)
            {
                case HttpRequestException or IOException or TimeoutException:
                    return true;
                case HttpStatusException s:
                    return s.Code >= 500;
            }
            c = c.InnerException;
        }
        return false;
    }

    /// <summary>
    /// Runs <paramref name="block"/> against each base URL in order, moving on to the next mirror on a connection failure or
    /// HTTP 5xx. The last failure is rethrown when every mirror fails; other errors surface immediately.
    /// </summary>
    public static async Task<T> WithMirrorsAsync<T>(IReadOnlyList<string> bases, Func<string, Task<T>> block)
    {
        ProviderException? last = null;
        foreach (var b in bases)
        {
            try
            {
                return await block(b).ConfigureAwait(false);
            }
            catch (ProviderException e)
            {
                if (!e.IsMirrorRetryable()) throw;
                last = e;
            }
        }
        throw last ?? new ProviderException("No base URL configured");
    }
}
