using AnimaWindows.Core.Abstractions;
using Windows.Storage;

namespace AnimaWindows.App.Services;

/// <summary>Packaged app folders (LocalFolder / LocalCacheFolder); downloads default to %USERPROFILE%\Videos\animawindows.</summary>
public sealed class WinAppPaths : IAppPaths
{
    public WinAppPaths()
    {
        string data;
        string cache;
        try
        {
            data = ApplicationData.Current.LocalFolder.Path;
            cache = ApplicationData.Current.LocalCacheFolder.Path;
        }
        catch (Exception)
        {
            // Not running with package identity (e.g. plain "dotnet run"): fall back to %LOCALAPPDATA%.
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "animawindows");
            data = Path.Combine(root, "data");
            cache = Path.Combine(root, "cache");
        }

        DataDirectory = data;
        CacheDirectory = cache;
        DownloadsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "animawindows");

        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(CacheDirectory);
        Directory.CreateDirectory(DownloadsDirectory);
    }

    public string DataDirectory { get; }

    public string CacheDirectory { get; }

    public string DownloadsDirectory { get; }
}
