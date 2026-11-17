using AnimaWindows.Core.Models;

namespace AnimaWindows.App.Services;

/// <summary>User-facing description of a failed provider call.</summary>
/// <param name="Message">Localised message (never empty).</param>
/// <param name="CloudflareUrl">Set when a browser check is needed; the UI offers "Verify in browser".</param>
public sealed record ErrorInfo(string Message, string? CloudflareUrl = null)
{
    public bool NeedsVerification => CloudflareUrl is not null;

    public static ErrorInfo From(Exception exception, string? fallbackKey = null)
    {
        switch (exception)
        {
            case CloudflareChallengeException cf:
                return new ErrorInfo(Strings.Get("Error_Cloudflare"), cf.Url);
            case NoProviderAvailableException:
                return new ErrorInfo(Strings.Get("Error_NoProvider"));
            default:
                var message = string.IsNullOrWhiteSpace(exception.Message)
                    ? Strings.Get(fallbackKey ?? "Error_Default")
                    : exception.Message;
                return new ErrorInfo(message);
        }
    }
}
