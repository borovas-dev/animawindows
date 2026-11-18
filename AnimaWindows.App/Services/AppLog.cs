namespace AnimaWindows.App.Services;

/// <summary>Small rolling diagnostics log (diagnostics.log in the app's local folder) for bug reports.</summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
        try
        {
            var path = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "diagnostics.log");
            lock (Gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".old", overwrite: true);
                }

                File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the app.
        }
    }
}
