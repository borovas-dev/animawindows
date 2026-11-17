using Microsoft.Windows.ApplicationModel.Resources;

namespace AnimaWindows.App.Player;

/// <summary>
/// Localised UI strings of the player. Keys live in the app's <c>Strings/*/Resources.resw</c> (see the player report for the list);
/// every lookup carries an English fallback so the player stays usable until the resources are merged.
/// </summary>
internal static class PlayerStrings
{
    private static readonly Lazy<ResourceLoader?> Loader = new(CreateLoader);

    private static ResourceLoader? CreateLoader()
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Get(string key, string fallback)
    {
        try
        {
            var value = Loader.Value?.GetString(key);
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch (Exception)
        {
            return fallback;
        }
    }
}
