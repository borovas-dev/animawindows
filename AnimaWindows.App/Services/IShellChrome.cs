using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AnimaWindows.App.Services;

/// <summary>Window chrome control for pages (notably the player): immersive mode, fullscreen, compact overlay.</summary>
public interface IShellChrome
{
    /// <summary>Hides the title bar and navigation pane (player page).</summary>
    bool IsImmersive { get; set; }

    bool IsFullScreen { get; set; }

    void EnterCompactOverlay();

    void ExitCompactOverlay();

    nint WindowHandle { get; }

    AppWindow AppWindow { get; }
}

public sealed class ShellChrome : IShellChrome
{
    private Window? _window;
    private AppWindow? _appWindow;
    private bool _isImmersive;

    /// <summary>Raised (UI thread) when immersive, fullscreen or compact-overlay state changed.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when a page asks the shell to open Search and focus the title-bar box.</summary>
    public event EventHandler? SearchRequested;

    public void RequestSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    public Window Window => _window ?? throw new InvalidOperationException("ShellChrome is not attached to a window yet.");

    public XamlRoot? XamlRoot => _window?.Content?.XamlRoot;

    public nint WindowHandle { get; private set; }

    public AppWindow AppWindow => _appWindow ?? throw new InvalidOperationException("ShellChrome is not attached to a window yet.");

    public bool IsAttached => _window is not null;

    public bool IsCompactOverlay => _appWindow?.Presenter.Kind == AppWindowPresenterKind.CompactOverlay;

    public void Attach(Window window)
    {
        _window = window;
        _appWindow = window.AppWindow;
        WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _appWindow.Changed += OnAppWindowChanged;
    }

    public bool IsImmersive
    {
        get => _isImmersive;
        set
        {
            if (_isImmersive == value)
            {
                return;
            }

            _isImmersive = value;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsFullScreen
    {
        get => _appWindow?.Presenter.Kind == AppWindowPresenterKind.FullScreen;
        set
        {
            if (_appWindow is null || IsFullScreen == value)
            {
                return;
            }

            _appWindow.SetPresenter(value ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Default);
        }
    }

    private Windows.Graphics.RectInt32? _boundsBeforeOverlay;

    public void EnterCompactOverlay()
    {
        if (_appWindow is null || IsCompactOverlay)
        {
            return;
        }

        if (_appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Restored })
        {
            _boundsBeforeOverlay = new Windows.Graphics.RectInt32(_appWindow.Position.X, _appWindow.Position.Y, _appWindow.Size.Width, _appWindow.Size.Height);
        }

        var overlay = CompactOverlayPresenter.Create();
        overlay.InitialSize = CompactOverlaySize.Large;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        _appWindow.SetPresenter(overlay);

        // "Large" is still only about 500 px wide; ask for a third of the screen at 16:9 (Windows may cap it).
        var work = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var width = Math.Max(_appWindow.Size.Width, work.Width / 3);
        var height = width * 9 / 16 + (_appWindow.Size.Height - _appWindow.ClientSize.Height) + _appWindow.TitleBar.Height;
        _appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
    }

    public void ExitCompactOverlay()
    {
        if (_appWindow is null || !IsCompactOverlay)
        {
            return;
        }

        _appWindow.SetPresenter(AppWindowPresenterKind.Default);
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        if (_boundsBeforeOverlay is { } bounds)
        {
            _appWindow.MoveAndResize(bounds);
            _boundsBeforeOverlay = null;
        }
    }

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidPresenterChange)
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
