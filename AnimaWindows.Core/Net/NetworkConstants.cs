namespace AnimaWindows.Core.Net;

/// <summary>Mirrors core/network/NetworkConstants.kt.</summary>
public static class NetworkConstants
{
    /// <summary>
    /// Same agent ani-cli uses (Kotlin <c>ANI_CLI_USER_AGENT</c>). Cloudflare clearance cookies are bound to it, so the
    /// WebView2 solver and every HTTP client must send exactly this string.
    /// </summary>
    public const string AniCliUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
}
