using Windows.ApplicationModel;

namespace AnimaWindows.App.Services;

/// <summary>Current app version as "major.minor.patch".</summary>
public static class AppVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        try
        {
            var v = Package.Current.Id.Version;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
        catch (Exception)
        {
            var v = typeof(AppVersion).Assembly.GetName().Version;
            return v is null ? "0.1.0" : $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        }
    }
}
