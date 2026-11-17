using System.ComponentModel;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI.Core;
using VirtualKey = Windows.System.VirtualKey;

namespace AnimaWindows.App.Player;

/// <summary>
/// Full-window video player. Hosts the active engine's video surface and, for the native engine, a Fluent transport bar,
/// subtitle overlay and skip buttons. With mpv (native child window that XAML cannot cover) only a slim top bar is shown and
/// mpv's own on-screen controller is used. Navigate with a <see cref="PlayerNavigationArgs"/> parameter.
/// </summary>
public sealed partial class PlayerPage : Page
{
    private static readonly TimeSpan HideDelay = TimeSpan.FromSeconds(3);
    private static readonly double[] Speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0];

    private readonly DispatcherQueueTimer _hideTimer;
    private readonly DispatcherQueueTimer _tapTimer;
    private readonly List<TextBlock> _subtitleBlocks = [];
    private readonly Grid _subtitleInner = new();
    private bool _scrubbing;
    private bool _updatingSeek;
    private bool _updatingVolume;
    private bool _controlsVisible = true;
    private bool _pointerOverControls;
    private bool _pointerInStage;
    private bool _cursorHidden;
    private int _openMenus;
    private Windows.Foundation.Point _lastPointer;
    private bool _subscribed;

    public PlayerPage()
    {
        ViewModel = AppServices.Get<PlayerViewModel>();
        InitializeComponent();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => OnHideTimer();

        _tapTimer = DispatcherQueue.CreateTimer();
        _tapTimer.Interval = TimeSpan.FromMilliseconds(250);
        _tapTimer.IsRepeating = false;
        _tapTimer.Tick += (_, _) => ViewModel.PlayPause();

        BuildSubtitleBlocks();
        LocalizeUi();
        ConfigureMenus();
        Stage.SizeChanged += (_, _) => UpdateSubtitleLayout();
        SkipMarks.SizeChanged += (_, _) => UpdateSkipMarks();
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPointerPressed), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekPointerReleased), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekPointerReleased), true);
    }

    public PlayerViewModel ViewModel { get; }

    // ---- navigation / lifetime ----

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not PlayerNavigationArgs args)
        {
            AppServices.Get<INavigationService>().GoBack();
            return;
        }

        ViewModel.PropertyChanged += OnViewModelChanged;
        ViewModel.EngineChanged += OnEngineChanged;
        _subscribed = true;
        VolumeSlider.Value = ViewModel.Volume * 100;
        ViewModel.Initialize(args);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Teardown();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Focus(FocusState.Programmatic);

    private void OnUnloaded(object sender, RoutedEventArgs e) => Teardown();

    private void Teardown()
    {
        _hideTimer.Stop();
        _tapTimer.Stop();
        ShowCursorIfHidden();
        if (!_subscribed) return;
        _subscribed = false;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        ViewModel.EngineChanged -= OnEngineChanged;
        ViewModel.Dispose();
        VideoHost.Children.Clear();
    }

    private void OnEngineChanged(object? sender, IPlaybackEngine? engine)
    {
        VideoHost.Children.Clear();
        if (engine?.VideoSurface is UIElement surface) VideoHost.Children.Add(surface);
        SetControlsVisible(true);
        RestartHideTimer();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.PositionSeconds):
                if (!_scrubbing)
                {
                    _updatingSeek = true;
                    SeekSlider.Value = Math.Min(ViewModel.PositionSeconds, SeekSlider.Maximum);
                    _updatingSeek = false;
                }

                break;
            case nameof(PlayerViewModel.DurationSeconds):
                _updatingSeek = true;
                SeekSlider.Maximum = Math.Max(1, ViewModel.DurationSeconds);
                _updatingSeek = false;
                UpdateSkipMarks();
                break;
            case nameof(PlayerViewModel.SkipTimes):
                UpdateSkipMarks();
                break;
            case nameof(PlayerViewModel.SubtitleText):
                RenderSubtitle();
                break;
            case nameof(PlayerViewModel.Volume):
                _updatingVolume = true;
                VolumeSlider.Value = ViewModel.Volume * 100;
                _updatingVolume = false;
                break;
            case nameof(PlayerViewModel.IsLoading):
            case nameof(PlayerViewModel.IsBuffering):
                BufferingRing.IsActive = ViewModel.IsLoading || ViewModel.IsBuffering;
                BufferingRing.Visibility = BufferingRing.IsActive && !ViewModel.HasError ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(PlayerViewModel.HasError):
                BufferingRing.Visibility = Visibility.Collapsed;
                SetControlsVisible(true);
                RestartHideTimer();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                if (ViewModel.IsPlaying) RestartHideTimer();
                else RevealControls();
                break;
        }
    }

    // ---- localisation ----

    private void LocalizeUi()
    {
        Label(BackButton, "Player_Back", "Back");
        Label(EmbeddedBackButton, "Player_Back", "Back");
        Label(PreviousButton, "Player_Previous", "Previous episode");
        Label(PlayPauseButton, "Player_PlayPause", "Play or pause");
        Label(NextButton, "Player_Next", "Next episode");
        Label(MuteButton, "Player_Mute", "Mute");
        Label(VolumeSlider, "Player_Volume", "Volume");
        Label(SeekSlider, "Player_Seek", "Seek");
        Label(SpeedButton, "Player_Speed", "Playback speed");
        Label(SubtitlesButton, "Player_Subtitles", "Subtitles");
        Label(QualityButton, "Player_Quality", "Quality");
        Label(EngineButton, "Player_Engine", "Playback engine");
        Label(EmbeddedEngineButton, "Player_Engine", "Playback engine");
        Label(PipButton, "Player_CompactOverlay", "Compact overlay");
        Label(FullscreenButton, "Player_Fullscreen", "Full screen");
        Label(EmbeddedFullscreenButton, "Player_Fullscreen", "Full screen");
        Label(SkipButton, "Player_Skip", "Skip");
        RetryButton.Content = PlayerStrings.Get("Player_Retry", "Retry");
        Label(RetryButton, "Player_Retry", "Retry");
        ErrorBackButton.Content = PlayerStrings.Get("Player_Back", "Back");
        Label(ErrorBackButton, "Player_Back", "Back");
        UndoButton.Content = PlayerStrings.Get("Player_Undo", "Undo");
        Label(UndoButton, "Player_Undo", "Undo");
    }

    private static void Label(DependencyObject control, string key, string fallback)
    {
        var text = PlayerStrings.Get(key, fallback);
        AutomationProperties.SetName(control, text);
        ToolTipService.SetToolTip(control, text);
    }

    // ---- menus ----

    private void ConfigureMenus()
    {
        foreach (var menu in new[] { SpeedMenu, SubtitleMenu, QualityMenu, EngineMenu, EmbeddedEngineMenu })
        {
            // A native child window (mpv) would hide an in-window flyout; a separate popup window stays on top.
            menu.ShouldConstrainToRootBounds = false;
        }
    }

    private void OnMenuOpened(object? sender, object e)
    {
        _openMenus++;
        _hideTimer.Stop();
    }

    private void OnMenuClosed(object? sender, object e)
    {
        _openMenus = Math.Max(0, _openMenus - 1);
        Focus(FocusState.Programmatic);
        RestartHideTimer();
    }

    private void OnSpeedMenuOpening(object? sender, object e)
    {
        SpeedMenu.Items.Clear();
        foreach (var speed in Speeds)
        {
            var value = speed;
            var item = new RadioMenuFlyoutItem
            {
                Text = PlayerFormat.Speed(value),
                GroupName = "player-speed",
                IsChecked = Math.Abs(ViewModel.Speed - value) < 0.001,
            };
            item.Click += (_, _) => ViewModel.SetSpeed(value);
            SpeedMenu.Items.Add(item);
        }
    }

    private void OnSubtitleMenuOpening(object? sender, object e)
    {
        SubtitleMenu.Items.Clear();
        var options = ViewModel.SubtitleOptions;
        for (var i = 0; i < options.Count; i++)
        {
            var index = i;
            var item = new RadioMenuFlyoutItem
            {
                Text = options[i].Label,
                GroupName = "player-subtitles",
                IsChecked = i == ViewModel.SelectedSubtitleIndex,
            };
            item.Click += (_, _) => _ = ViewModel.SelectSubtitleAsync(index);
            SubtitleMenu.Items.Add(item);
        }
    }

    private void OnQualityMenuOpening(object? sender, object e)
    {
        QualityMenu.Items.Clear();
        foreach (var option in ViewModel.QualityOptions)
        {
            var height = option.Height;
            var item = new RadioMenuFlyoutItem
            {
                Text = option.Label,
                GroupName = "player-quality",
                IsChecked = height == ViewModel.SelectedQualityHeight,
            };
            item.Click += (_, _) => _ = ViewModel.SelectQualityAsync(height);
            QualityMenu.Items.Add(item);
        }
    }

    private void OnEngineMenuOpening(object? sender, object e)
    {
        var menu = (MenuFlyout)sender!;
        menu.Items.Clear();
        AddEngineItem(menu, PlaybackEngineChoice.Auto, PlayerStrings.Get("Player_EngineAuto", "Automatic"), true);
        AddEngineItem(menu, PlaybackEngineChoice.Native, PlayerStrings.Get("Player_EngineNative", "Windows media (native)"), true);
        AddEngineItem(menu, PlaybackEngineChoice.Mpv, PlayerStrings.Get("Player_EngineMpv", "mpv"), ViewModel.IsMpvAvailable);
    }

    private void AddEngineItem(MenuFlyout menu, PlaybackEngineChoice choice, string text, bool enabled)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "player-engine",
            IsChecked = ViewModel.EngineChoice == choice,
            IsEnabled = enabled,
        };
        item.Click += (_, _) => _ = ViewModel.SelectEngineAsync(choice);
        menu.Items.Add(item);
    }

    // ---- buttons ----

    private void OnBackClick(object sender, RoutedEventArgs e) => ViewModel.GoBack();

    private void OnRetryClick(object sender, RoutedEventArgs e) => ViewModel.Retry();

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => ViewModel.PlayPause();

    private void OnNextClick(object sender, RoutedEventArgs e) => ViewModel.Next();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ViewModel.Previous();

    private void OnMuteClick(object sender, RoutedEventArgs e) => ViewModel.ToggleMute();

    private void OnPipClick(object sender, RoutedEventArgs e) => ViewModel.ToggleCompactOverlay();

    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ViewModel.ToggleFullscreen();

    private void OnSkipClick(object sender, RoutedEventArgs e) => ViewModel.SkipCurrentSegment();

    private void OnUndoClick(object sender, RoutedEventArgs e) => ViewModel.UndoSkip();

    // ---- seek / volume sliders ----

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e) => _scrubbing = true;

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        ViewModel.SeekTo(SeekSlider.Value);
        RestartHideTimer();
    }

    private void OnSeekValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // While dragging, only the release seeks (seeking on every tick is expensive for HLS); keyboard/other changes seek at once.
        if (_updatingSeek || _scrubbing) return;
        ViewModel.SeekTo(e.NewValue);
    }

    private void OnVolumeValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume) return;
        ViewModel.SetVolume(e.NewValue / 100.0);
    }

    private void UpdateSkipMarks()
    {
        SkipMarks.Children.Clear();
        var duration = ViewModel.DurationSeconds;
        var times = ViewModel.SkipTimes;
        if (times is null || duration <= 0) return;
        AddMark(times.Opening, duration);
        AddMark(times.Ending, duration);
    }

    private void AddMark(SkipInterval? interval, double durationSeconds)
    {
        var width = SkipMarks.ActualWidth;
        if (interval is null || width <= 0) return;
        var start = PlayerFormat.Fraction(interval.StartSec, durationSeconds);
        var end = PlayerFormat.Fraction(interval.EndSec, durationSeconds);
        if (end <= start) return;
        SkipMarks.Children.Add(new Rectangle
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = Math.Max(2, (end - start) * width),
            Margin = new Thickness(start * width, 0, 0, 0),
            RadiusX = 2,
            RadiusY = 2,
            Fill = Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accent) && accent is Brush brush
                ? brush
                : new SolidColorBrush(Colors.White),
            Opacity = 0.8,
        });
    }

    // ---- subtitle overlay ----

    private void BuildSubtitleBlocks()
    {
        var shadow = new SolidColorBrush(Colors.Black);
        var offsets = new (double X, double Y)[] { (-1.5, 0), (1.5, 0), (0, -1.5), (0, 1.5), (-1, -1), (1, 1), (-1, 1), (1, -1) };
        foreach (var (x, y) in offsets)
            _subtitleBlocks.Add(NewSubtitleBlock(shadow, new TranslateTransform { X = x, Y = y }));
        _subtitleBlocks.Add(NewSubtitleBlock(new SolidColorBrush(Colors.White), null));
        foreach (var block in _subtitleBlocks) _subtitleInner.Children.Add(block);
        _subtitleInner.Visibility = Visibility.Collapsed;
        SubtitlePanel.Children.Add(_subtitleInner);
    }

    private static TextBlock NewSubtitleBlock(Brush foreground, Transform? transform) => new()
    {
        Foreground = foreground,
        RenderTransform = transform,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Bottom,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        IsHitTestVisible = false,
    };

    private void RenderSubtitle()
    {
        var runs = SubtitleRuns.Build(ViewModel.SubtitleText);
        foreach (var block in _subtitleBlocks)
        {
            block.Inlines.Clear();
            foreach (var run in runs) AddRun(block, run);
        }

        _subtitleInner.Visibility = runs.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static void AddRun(TextBlock block, SubtitleRun run)
    {
        var lines = run.Text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) block.Inlines.Add(new LineBreak());
            if (lines[i].Length == 0) continue;
            var text = new Run { Text = lines[i] };
            if (run.Italic) text.FontStyle = Windows.UI.Text.FontStyle.Italic;
            if (run.Bold) text.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            if (run.Underline)
            {
                var underline = new Underline();
                underline.Inlines.Add(text);
                block.Inlines.Add(underline);
            }
            else
            {
                block.Inlines.Add(text);
            }
        }
    }

    private void UpdateSubtitleLayout()
    {
        var height = Stage.ActualHeight;
        var size = PlayerFormat.SubtitleFontSize(height);
        foreach (var block in _subtitleBlocks) block.FontSize = size;
        SubtitlePanel.MaxWidth = Math.Max(200, Stage.ActualWidth * 0.9);
        SubtitlePanel.Margin = new Thickness(0, 0, 0, height * 0.06 + (_controlsVisible ? 96 : 0));
    }

    // ---- auto-hide controls and cursor ----

    private bool ShouldAutoHide() =>
        ViewModel.IsPlaying && !ViewModel.HasError && ViewModel.IsNativeUi && !_pointerOverControls && _openMenus == 0 && !_scrubbing;

    private void RevealControls()
    {
        SetControlsVisible(true);
        ShowCursorIfHidden();
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (ShouldAutoHide()) _hideTimer.Start();
    }

    private void OnHideTimer()
    {
        if (!ShouldAutoHide()) return;
        SetControlsVisible(false);
        if (_pointerInStage) HideCursor();
    }

    private void SetControlsVisible(bool visible)
    {
        _controlsVisible = visible;
        Controls.Opacity = visible ? 1 : 0;
        Controls.IsHitTestVisible = visible;
        SkipPanel.Margin = new Thickness(0, 0, 24, visible ? 112 : 32);
        UpdateSubtitleLayout();
    }

    private void HideCursor()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        Win32Interop.ShowCursor(false);
    }

    private void ShowCursorIfHidden()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        Win32Interop.ShowCursor(true);
    }

    private void OnStagePointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Stage).Position;
        if (_pointerInStage && Math.Abs(point.X - _lastPointer.X) < 1 && Math.Abs(point.Y - _lastPointer.Y) < 1) return;
        _lastPointer = point;
        _pointerInStage = true;
        RevealControls();
    }

    private void OnStagePointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerInStage = false;
        ShowCursorIfHidden();
    }

    private void OnControlsPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverControls = true;
        _hideTimer.Stop();
    }

    private void OnControlsPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _pointerOverControls = false;
        RestartHideTimer();
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        // Wait out a possible double tap before toggling playback.
        _tapTimer.Stop();
        _tapTimer.Start();
    }

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _tapTimer.Stop();
        ViewModel.ToggleFullscreen();
        e.Handled = true;
    }

    private void OnStageWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(Stage).Properties.MouseWheelDelta;
        if (delta == 0) return;
        ViewModel.AdjustVolume(delta > 0 ? PlayerKeyMap.VolumeStep : -PlayerKeyMap.VolumeStep);
        e.Handled = true;
    }

    // ---- keyboard ----

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var modified = IsDown(VirtualKey.Control) || IsDown(VirtualKey.Menu) || IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows);
        if (PlayerKeyMap.Map(e.Key, modified, ViewModel.CanExitFullscreenOrOverlay) is not { } action) return;
        e.Handled = true;
        switch (action)
        {
            case PlayerKeyAction.PlayPause:
                ViewModel.PlayPause();
                break;
            case PlayerKeyAction.SeekBack:
                ViewModel.SeekBy(-PlayerKeyMap.SeekStep);
                break;
            case PlayerKeyAction.SeekForward:
                ViewModel.SeekBy(PlayerKeyMap.SeekStep);
                break;
            case PlayerKeyAction.VolumeUp:
                ViewModel.AdjustVolume(PlayerKeyMap.VolumeStep);
                break;
            case PlayerKeyAction.VolumeDown:
                ViewModel.AdjustVolume(-PlayerKeyMap.VolumeStep);
                break;
            case PlayerKeyAction.ToggleFullscreen:
                ViewModel.ToggleFullscreen();
                break;
            case PlayerKeyAction.ToggleMute:
                ViewModel.ToggleMute();
                break;
            case PlayerKeyAction.ExitFullscreen:
                ViewModel.ExitFullscreenOrOverlay();
                break;
            case PlayerKeyAction.Next:
                ViewModel.Next();
                break;
            case PlayerKeyAction.Previous:
                ViewModel.Previous();
                break;
            case PlayerKeyAction.SkipSegment:
                ViewModel.SkipCurrentSegment();
                break;
            case PlayerKeyAction.CycleSubtitles:
                _ = ViewModel.CycleSubtitlesAsync();
                break;
        }

        RevealControls();
    }
}
