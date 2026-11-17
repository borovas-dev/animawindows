using Microsoft.Windows.ApplicationModel.Resources;

namespace AnimaWindows.App.Services.Notifications;

/// <summary>
/// Localised strings for code (view models, notifications) in the downloads / health / tracking areas. Keys are plain resource
/// names (no dots) from the shell's <c>Resources.resw</c>; the English fallback keeps the UI readable if a key is missing.
/// </summary>
internal static class UiText
{
    private static ResourceLoader? _loader;

    public static string Get(string key, string fallback)
    {
        try
        {
            _loader ??= new ResourceLoader();
            var value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    public static string Format(string key, string fallbackFormat, params object[] args) =>
        string.Format(System.Globalization.CultureInfo.CurrentCulture, Get(key, fallbackFormat), args);

    /// <summary>Human readable size with one decimal (B, KB, MB, GB).</summary>
    public static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        do
        {
            value /= 1024;
            i++;
        }
        while (value >= 1024 && i < units.Length - 1);
        return string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.#} {1}", value, units[i]);
    }
}
