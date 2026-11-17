using System.Net.Http.Headers;
using System.Net;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;

namespace AnimaWindows.Core.Net;

/// <summary>Port of <c>CloudflareDetector</c> in core/network/CloudflareInterceptor.kt.</summary>
public static class CloudflareDetector
{
    /// <summary>403/503 with a "Just a moment" body or a <c>cf-mitigated: challenge</c> header.</summary>
    public static bool IsChallenge(HttpStatusCode code, HttpResponseHeaders headers, Func<string> bodyPrefix)
    {
        if (code != HttpStatusCode.Forbidden && code != HttpStatusCode.ServiceUnavailable) return false;
        if (headers.TryGetValues("cf-mitigated", out var values) &&
            values.Any(v => string.Equals(v, "challenge", StringComparison.OrdinalIgnoreCase)))
            return true;
        return bodyPrefix().Contains("Just a moment", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Detects Cloudflare challenges, asks <see cref="ICloudflareSolver"/> to solve it (concurrent solves are serialised: later
/// callers wait and reuse the clearance), retries the request once, and otherwise throws
/// <see cref="CloudflareChallengeException"/>. Port of <c>CloudflareInterceptor</c>; the Kotlin app throws immediately and lets
/// the UI launch the solver, the Windows port solves inline when a solver is supplied.
/// </summary>
public sealed class CloudflareHandler : DelegatingHandler
{
    private const int PeekBytes = 64 * 1024;

    private readonly ICloudflareSolver? _solver;
    private readonly SemaphoreSlim _gate;

    /// <param name="solver">Null = never solve, just throw.</param>
    /// <param name="gate">Shared lock so several handlers/clients serialise solves; created per handler when null.</param>
    public CloudflareHandler(ICloudflareSolver? solver, HttpMessageHandler? inner = null, SemaphoreSlim? gate = null)
    {
        _solver = solver;
        _gate = gate ?? new SemaphoreSlim(1, 1);
        if (inner is not null) InnerHandler = inner;
    }

    /// <summary>Clearance generation: bumped after each successful solve so queued waiters skip a redundant solve.</summary>
    private int _generation;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var generationAtStart = Volatile.Read(ref _generation);
        // Buffer content so the request can be resent.
        if (request.Content is not null) await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!await IsBlockedAsync(response, cancellationToken).ConfigureAwait(false)) return response;

        var url = response.RequestMessage?.RequestUri?.ToString() ?? request.RequestUri?.ToString() ?? "";
        response.Dispose();
        if (_solver is null || request.RequestUri is null) throw new CloudflareChallengeException(url);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have solved while we waited; then retry without solving again.
            if (Volatile.Read(ref _generation) == generationAtStart)
            {
                var solved = await _solver.SolveAsync(request.RequestUri, null, cancellationToken).ConfigureAwait(false);
                if (!solved) throw new CloudflareChallengeException(url);
                Interlocked.Increment(ref _generation);
            }
        }
        finally
        {
            _gate.Release();
        }

        using var retry = await CloneAsync(request, cancellationToken).ConfigureAwait(false);
        var second = await base.SendAsync(retry, cancellationToken).ConfigureAwait(false);
        if (await IsBlockedAsync(second, cancellationToken).ConfigureAwait(false))
        {
            second.Dispose();
            throw new CloudflareChallengeException(url);
        }

        return second;
    }

    private static async Task<bool> IsBlockedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden && response.StatusCode != HttpStatusCode.ServiceUnavailable) return false;
        string body = "";
        if (!(response.Headers.TryGetValues("cf-mitigated", out var v) &&
              v.Any(x => string.Equals(x, "challenge", StringComparison.OrdinalIgnoreCase))))
        {
            try
            {
                await response.Content.LoadIntoBufferAsync(ct).ConfigureAwait(false);
                var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                body = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, PeekBytes));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                body = "";
            }
        }

        return CloudflareDetector.IsChallenge(response.StatusCode, response.Headers, () => body);
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage src, CancellationToken ct)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version, VersionPolicy = src.VersionPolicy };
        foreach (var h in src.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
        if (src.Content is not null)
        {
            var bytes = await src.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var h in src.Content.Headers) content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = content;
        }

        return clone;
    }
}
