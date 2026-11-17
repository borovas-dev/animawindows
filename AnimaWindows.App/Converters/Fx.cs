using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimaWindows.App.Converters;

/// <summary>Static helpers for <c>x:Bind</c> function bindings (no value converters needed).</summary>
public static class Fx
{
    /// <summary>Decoded-size-limited bitmap for a cover URL; null for blank or invalid URLs.</summary>
    public static ImageSource? Image(string? url, int decodeWidth)
    {
        // Cover URLs come from scraped providers: only http(s), never file:/UNC (would leak NTLM credentials or read local files).
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        return new BitmapImage { UriSource = uri, DecodePixelWidth = decodeWidth };
    }

    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Collapsed(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNonNegative(double value) => value >= 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;
}
