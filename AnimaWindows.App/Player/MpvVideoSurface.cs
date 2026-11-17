using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AnimaWindows.App.Player;

/// <summary>
/// Placeholder element laid out where the video goes. mpv renders into a native child window of the main window; this element
/// only reports where that window must be (physical pixels, client coordinates of the top-level window).
/// </summary>
public sealed class MpvVideoSurface : Grid
{
    private readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private (int X, int Y, int Width, int Height) _last;

    public MpvVideoSurface()
    {
        Loaded += (_, _) =>
        {
            _loaded.TrySetResult();
            Publish(force: true);
        };
        Unloaded += (_, _) => BoundsChanged?.Invoke(this, null);
        SizeChanged += (_, _) => Publish(force: false);
        LayoutUpdated += (_, _) => Publish(force: false);
    }

    /// <summary>Completes once the element is in the visual tree.</summary>
    public Task WhenLoaded => _loaded.Task;

    /// <summary>Raised with the new bounds in physical pixels, or null when the surface left the visual tree.</summary>
    public event EventHandler<(int X, int Y, int Width, int Height)?>? BoundsChanged;

    /// <summary>Current bounds in physical client pixels, or null when not in the tree.</summary>
    public (int X, int Y, int Width, int Height)? CurrentBounds()
    {
        if (!IsLoaded || XamlRoot?.Content is not UIElement root) return null;
        try
        {
            var origin = TransformToVisual(root).TransformPoint(new Point(0, 0));
            var scale = XamlRoot.RasterizationScale;
            return (
                (int)Math.Round(origin.X * scale),
                (int)Math.Round(origin.Y * scale),
                Math.Max(1, (int)Math.Round(ActualWidth * scale)),
                Math.Max(1, (int)Math.Round(ActualHeight * scale)));
        }
        catch (Exception)
        {
            return null; // Not connected to the root yet.
        }
    }

    private void Publish(bool force)
    {
        if (CurrentBounds() is not { } bounds) return;
        if (!force && bounds == _last) return;
        _last = bounds;
        BoundsChanged?.Invoke(this, bounds);
    }
}
