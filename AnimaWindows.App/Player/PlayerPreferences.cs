using Windows.Storage;

namespace AnimaWindows.App.Player;

/// <summary>Remembers the player volume/mute between sessions (no counterpart in <c>AppSettings</c>); best effort.</summary>
internal static class PlayerPreferences
{
    private const string VolumeKey = "Player.Volume";
    private const string MutedKey = "Player.Muted";

    public static (double Volume, bool Muted) Load()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var volume = values.TryGetValue(VolumeKey, out var v) && v is double d ? Math.Clamp(d, 0, 1) : 1.0;
            var muted = values.TryGetValue(MutedKey, out var m) && m is bool b && b;
            return (volume, muted);
        }
        catch (Exception)
        {
            return (1.0, false);
        }
    }

    public static void Save(double volume, bool muted)
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[VolumeKey] = volume;
            values[MutedKey] = muted;
        }
        catch (Exception)
        {
            // Unpackaged or storage unavailable: volume simply is not remembered.
        }
    }
}
