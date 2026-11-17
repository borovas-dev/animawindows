using System.Text.Json;
using System.Text.Json.Serialization;
using AnimaWindows.Core.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace AnimaWindows.App.Shell;

public sealed class WindowPlacementData
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WindowPlacementData))]
internal sealed partial class WindowPlacementJsonContext : JsonSerializerContext;

/// <summary>Persists and restores the main window's size, position and maximised state (<c>window.json</c>).</summary>
public sealed class WindowPlacement
{
    private const int DefaultWidth = 1280;
    private const int DefaultHeight = 800;

    /// <summary>Smaller saved bounds are treated as accidental (e.g. left over from picture-in-picture).</summary>
    private const int MinRestoreWidth = 800;
    private const int MinRestoreHeight = 560;

    private readonly string _path;
    private readonly AppWindow _window;
    private readonly DispatcherQueueTimer _saveTimer;
    private WindowPlacementData _data = new();
    private bool _hasBounds;

    public WindowPlacement(IAppPaths paths, AppWindow window, DispatcherQueue queue)
    {
        _path = Path.Combine(paths.DataDirectory, "window.json");
        _window = window;
        _saveTimer = queue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(600);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
    }

    public void Restore()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize(File.ReadAllText(_path), WindowPlacementJsonContext.Default.WindowPlacementData);
                if (loaded is not null && loaded.Width >= MinRestoreWidth && loaded.Height >= MinRestoreHeight)
                {
                    _data = loaded;
                    _hasBounds = true;
                }
            }
        }
        catch (Exception)
        {
            // Corrupt file: use defaults.
        }

        if (_hasBounds)
        {
            var rect = new RectInt32(_data.X, _data.Y, _data.Width, _data.Height);
            if (DisplayArea.GetFromRect(rect, DisplayAreaFallback.None) is not null)
            {
                _window.MoveAndResize(rect);
            }
            else
            {
                _window.Resize(new SizeInt32(_data.Width, _data.Height));
            }

            if (_data.Maximized && _window.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.Maximize();
            }
        }
        else
        {
            _window.Resize(new SizeInt32(DefaultWidth, DefaultHeight));
        }

        _window.Changed += OnChanged;
    }

    private void OnChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidSizeChange && !args.DidPositionChange && !args.DidPresenterChange)
        {
            return;
        }

        if (sender.Presenter is not OverlappedPresenter presenter)
        {
            return; // fullscreen / compact overlay: keep the last normal bounds
        }

        switch (presenter.State)
        {
            case OverlappedPresenterState.Maximized:
                _data.Maximized = true;
                break;
            case OverlappedPresenterState.Restored:
                if (sender.Size.Width < MinRestoreWidth || sender.Size.Height < MinRestoreHeight)
                {
                    return; // keep the last normal-size bounds for the next launch
                }

                _data.Maximized = false;
                _data.X = sender.Position.X;
                _data.Y = sender.Position.Y;
                _data.Width = sender.Size.Width;
                _data.Height = sender.Size.Height;
                _hasBounds = true;
                break;
            default:
                return;
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Save()
    {
        if (!_hasBounds)
        {
            return;
        }

        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_data, WindowPlacementJsonContext.Default.WindowPlacementData));
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
