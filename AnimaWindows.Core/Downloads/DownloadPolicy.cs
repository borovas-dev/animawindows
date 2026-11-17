using System.Net;
using System.Net.Sockets;

namespace AnimaWindows.Core.Downloads;

/// <summary>
/// Retry policy and failure texts. Mirrors <c>DownloadPolicy</c> in feature/downloads/DownloadPolicy.kt (the Android-only
/// foreground-service / Requirements parts are replaced by <c>INetworkCostProvider</c> in <see cref="DownloadManager"/>).
/// </summary>
public static class DownloadPolicy
{
    /// <summary>Automatic retries of a failed episode before it is left Failed for the user to retry.</summary>
    public const int MaxAutoRetries = 3;

    private const long BaseBackoffMs = 30_000L;
    private const long MaxBackoffMs = 10 * 60_000L;

    /// <summary>Delay before automatic retry number <paramref name="attempt"/> (1-based): 30 s, 2 min, 8 min, capped at 10 min.</summary>
    public static TimeSpan Backoff(int attempt)
    {
        var shift = Math.Clamp(attempt - 1, 0, 10) * 2;
        var ms = Math.Min(BaseBackoffMs << shift, MaxBackoffMs);
        return TimeSpan.FromMilliseconds(ms);
    }

    /// <summary><paramref name="attemptsSoFar"/> automatic retries have already been made.</summary>
    public static bool ShouldRetry(int attemptsSoFar) => attemptsSoFar < MaxAutoRetries;

    /// <summary>True when the failure looks like an expired / invalid signed URL (401/403/404/410).</summary>
    public static bool IsExpiredLink(Exception? error)
    {
        for (var t = error; t is not null; t = t.InnerException)
        {
            if (t is HttpRequestException { StatusCode: { } code })
            {
                return code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone;
            }
        }

        return false;
    }

    /// <summary>Turns a download failure into one short line for the UI.</summary>
    public static string DescribeFailure(Exception? error)
    {
        var depth = 0;
        for (var t = error; t is not null && depth++ < 8; t = t.InnerException)
        {
            switch (t)
            {
                case HttpRequestException { StatusCode: { } status }:
                    var code = (int)status;
                    return code switch
                    {
                        401 or 403 => $"Server refused access (HTTP {code}); the link may have expired",
                        404 or 410 => $"Video not found on server (HTTP {code})",
                        >= 500 and <= 599 => $"Server error (HTTP {code})",
                        _ => $"Server returned HTTP {code}",
                    };
                case SocketException:
                case HttpRequestException:
                    return "Could not reach the server";
                case TimeoutException:
                case TaskCanceledException:
                    return "Connection timed out";
            }
        }

        var msg = error?.Message;
        return string.IsNullOrWhiteSpace(msg) ? "Download failed" : $"Download failed: {msg}";
    }

    /// <summary>Row note while an automatic retry is pending.</summary>
    public static string RetryingText(int attempt) => $"Retrying ({attempt}/{MaxAutoRetries})";
}
