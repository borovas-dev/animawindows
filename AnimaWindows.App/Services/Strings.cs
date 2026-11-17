using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace AnimaWindows.App.Services;

/// <summary>Code-side access to the .resw strings (XAML uses <c>x:Uid</c>). Keys here never contain dots.</summary>
public static class Strings
{
    private static readonly Lazy<ResourceLoader?> Loader = new(() =>
    {
        try
        {
            return new ResourceLoader();
        }
        catch (Exception)
        {
            return null;
        }
    });

    public static string Get(string key)
    {
        try
        {
            var value = Loader.Value?.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch (Exception)
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
