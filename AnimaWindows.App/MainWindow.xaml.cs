using AnimaWindows.App.Pages.Downloads;
using AnimaWindows.App.Pages.Health;
using AnimaWindows.App.Pages.Home;
using AnimaWindows.App.Pages.Search;
using AnimaWindows.App.Pages.Settings;
using AnimaWindows.App.Services;
using AnimaWindows.App.Shell;
using AnimaWindows.App.ViewModels;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;
using ThemeMode = AnimaWindows.Core.Models.ThemeMode;

namespace AnimaWindows.App;

/// <summary>The shell: Mica window, title bar with global search, navigation, accelerators and chrome control.</summary>
public sealed partial class MainWindow : Window
{
    private const string PlayerNamespace = "AnimaWindows.App.Player";

    private readonly ShellChrome _chrome;
    private readonly NavigationService _navigation;
    private readonly ISettingsStore _settings;
    private readonly IDispatcher _dispatcher;
    private readonly WindowPlacement _placement;
    private bool _syncingSelection;
    private bool _wasPlayer;

    public MainWindow()
    {
        ViewModel = AppServices.Get<ShellViewModel>();
        _chrome = AppServices.Get<ShellChrome>();
        _navigation = AppServices.Get<NavigationService>();
        _settings = AppServices.Get<ISettingsStore>();
        _dispatcher = AppServices.Get<IDispatcher>();

        InitializeComponent();

        GlobalSearch.ItemsSource = ViewModel.Suggestions;
        ViewModel.PropertyChanged += (_, _) => ApplyUpdateBar();
        ApplyUpdateBar();

        _chrome.Attach(this);
        _navigation.Attach(ContentFrame);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        GlobalSearch.LayoutUpdated += (_, _) => CenterSearchBox();
        AppWindow.Title = "animawindows";

        _placement = new WindowPlacement(AppServices.Get<IAppPaths>(), AppWindow, DispatcherQueue);
        _placement.Restore();

        ContentFrame.Navigated += OnNavigated;
        _chrome.StateChanged += (_, _) => ApplyChrome();
        _settings.Changed += (_, s) => _dispatcher.TryEnqueue(() => ApplyTheme(s.ThemeMode));
        ApplyTheme(_settings.Current.ThemeMode);

        RegisterAccelerators();
        RootGrid.SizeChanged += (_, e) => SizeSearchBox(e.NewSize.Width);
        _chrome.SearchRequested += (_, _) => GoToSearch();
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerPressed), true);

        _ = AppServices.Get<SearchViewModel>().RunRecentsAsync(CancellationToken.None);

        _navigation.Navigate(typeof(HomePage));
    }

    public ShellViewModel ViewModel { get; }

    /// <summary>Restores a minimised window and brings it forward (second launch, protocol or toast activation).</summary>
    public void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        Activate();
    }

    private void ApplyUpdateBar()
    {
        UpdateBar.Message = ViewModel.UpdateMessage;
        ChangelogLink.NavigateUri = ViewModel.UpdatePageUri;
        UpdateBar.IsOpen = ViewModel.IsUpdateAvailable;
    }

    // ---- theme and chrome ----

    private void ApplyTheme(ThemeMode mode)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        root.RequestedTheme = mode switch
        {
            ThemeMode.Light => ElementTheme.Light,
            ThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        // Never override the caption-button colours in high contrast: the system supplies accessible ones.
        if (AppWindowTitleBar.IsCustomizationSupported() && !new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
        {
            var bar = AppWindow.TitleBar;
            bar.ButtonForegroundColor = mode switch
            {
                ThemeMode.Light => Colors.Black,
                ThemeMode.Dark => Colors.White,
                _ => null,
            };
        }
    }

    private void ApplyChrome()
    {
        var immersive = _chrome.IsImmersive;
        var overlay = _chrome.IsCompactOverlay;
        var hideTitleBar = !overlay && (immersive || _chrome.IsFullScreen);
        AppTitleBar.Visibility = hideTitleBar ? Visibility.Collapsed : Visibility.Visible;
        GlobalSearch.Visibility = overlay || immersive ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.IsPaneToggleButtonVisible = !overlay && !immersive;
        AppTitleBar.IsBackButtonVisible = !overlay && !immersive && ContentFrame.CanGoBack;
        NavView.IsPaneVisible = !immersive && !_chrome.IsCompactOverlay;
    }

    // ---- navigation ----

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_syncingSelection)
        {
            return;
        }

        Type? target = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItemContainer?.Tag as string) switch
            {
                "home" => typeof(HomePage),
                "search" => typeof(SearchPage),
                "downloads" => typeof(DownloadsPage),
                _ => null,
            };

        if (target is not null && _navigation.CurrentPage != target)
        {
            _navigation.Navigate(target, null, args.RecommendedNavigationTransitionInfo);
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack && !_chrome.IsImmersive && !_chrome.IsCompactOverlay;

        var page = e.SourcePageType;
        var isPlayer = page.Namespace == PlayerNamespace;
        if (_wasPlayer && !isPlayer)
        {
            // Safety net: the player resets its own chrome, but never leave the shell hidden.
            _chrome.IsImmersive = false;
            _chrome.ExitCompactOverlay();
            if (_chrome.IsFullScreen)
            {
                _chrome.IsFullScreen = false;
            }
        }

        _wasPlayer = isPlayer;

        object? item = null;
        if (page == typeof(HomePage))
        {
            item = HomeItem;
        }
        else if (page == typeof(SearchPage))
        {
            item = SearchItem;
        }
        else if (page == typeof(DownloadsPage))
        {
            item = DownloadsItem;
        }
        else if (page == typeof(SettingsPage) || page == typeof(HealthPage))
        {
            item = NavView.SettingsItem;
        }

        if (item is not null && !ReferenceEquals(NavView.SelectedItem, item))
        {
            _syncingSelection = true;
            NavView.SelectedItem = item;
            _syncingSelection = false;
        }
    }

    private void OnBackRequested(TitleBar sender, object args) => _navigation.GoBack();

    private void OnPaneToggleRequested(TitleBar sender, object args) => NavView.IsPaneOpen = !NavView.IsPaneOpen;

    private void OnUpdateBarClosed(InfoBar sender, InfoBarClosedEventArgs args) => ViewModel.DismissUpdate();

    // ---- global search ----

    /// <summary>Search box takes about a third of the window, within Fluent's 320-560 px range for a title-bar search.</summary>
    private void SizeSearchBox(double windowWidth)
    {
        // CenterSearchBox refines this to the title bar's content slot once it is laid out.
        if (double.IsNaN(GlobalSearch.Width))
        {
            GlobalSearch.Width = Math.Clamp(windowWidth * 0.36, 200, 560);
        }
    }

    /// <summary>
    /// The title bar places its content between the title and the caption buttons; shift the box (render transform only,
    /// no relayout) so it sits on the window's centre line, as long as it does not run into the title or the buttons.
    /// </summary>
    private void CenterSearchBox()
    {
        if (GlobalSearch.Visibility != Visibility.Visible || GlobalSearch.ActualWidth <= 0 || RootGrid.ActualWidth <= 0)
        {
            return;
        }

        var shift = GlobalSearch.RenderTransform as Microsoft.UI.Xaml.Media.TranslateTransform;
        if (shift is null)
        {
            shift = new Microsoft.UI.Xaml.Media.TranslateTransform();
            GlobalSearch.RenderTransform = shift;
        }

        var left = GlobalSearch.TransformToVisual(RootGrid).TransformPoint(default).X - shift.X;

        // The title bar clips its content to the slot between the title and the caption buttons: never be wider than
        // that slot, and never move outside it, or the box gets cut off ("rch anime" in narrow windows).
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(GlobalSearch) is FrameworkElement slot && slot.ActualWidth > 0)
        {
            var slotLeft = slot.TransformToVisual(RootGrid).TransformPoint(default).X;
            var slotRight = slotLeft + slot.ActualWidth;
            var fit = Math.Max(120, Math.Min(Math.Clamp(RootGrid.ActualWidth * 0.36, 200, 560), slot.ActualWidth - 8));
            if (fit > 0 && Math.Abs(GlobalSearch.Width - fit) >= 1)
            {
                GlobalSearch.Width = fit; // relayout; SizeChanged brings us back here
                return;
            }

            var centred = (RootGrid.ActualWidth - GlobalSearch.ActualWidth) / 2;
            var slotTarget = Math.Clamp(centred, slotLeft + 4, Math.Max(slotLeft + 4, slotRight - GlobalSearch.ActualWidth - 4));
            var move = Math.Round(slotTarget - left);
            if (Math.Abs(move - shift.X) >= 1)
            {
                shift.X = move;
            }

            return;
        }

        const double TitleAreaWidth = 200; // icon, pane toggle and "animawindows"
        var wanted = (RootGrid.ActualWidth - GlobalSearch.ActualWidth) / 2;
        var rightLimit = RootGrid.ActualWidth - AppWindow.TitleBar.RightInset / (RootGrid.XamlRoot?.RasterizationScale ?? 1) - GlobalSearch.ActualWidth - 8;
        var target = Math.Min(Math.Max(wanted, TitleAreaWidth), Math.Max(rightLimit, TitleAreaWidth));
        var delta = Math.Round(target - left);
        if (Math.Abs(delta - shift.X) >= 1)
        {
            shift.X = delta;
        }
    }

    /// <summary>Opens Search through the navigation pane (same path as the menu item) and focuses the box.</summary>
    private void GoToSearch()
    {
        if (!ReferenceEquals(NavView.SelectedItem, SearchItem))
        {
            NavView.SelectedItem = SearchItem;
        }

        GlobalSearch.Focus(FocusState.Programmatic);
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            ViewModel.RefreshSuggestions(sender.Text);
        }
    }

    private void OnSearchGotFocus(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshSuggestions(GlobalSearch.Text);
        if (ViewModel.Suggestions.Count > 0)
        {
            GlobalSearch.IsSuggestionListOpen = true;
        }
    }

    private void OnSearchSuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is string text)
        {
            sender.Text = text;
        }
    }

    private void OnSearchQuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var query = args.ChosenSuggestion as string ?? args.QueryText;
        ViewModel.Submit(query);
    }

    // ---- keyboard and mouse ----

    private void RegisterAccelerators()
    {
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => GlobalSearch.Focus(FocusState.Programmatic));
        AddAccelerator((VirtualKey)188, VirtualKeyModifiers.Control, () =>
        {
            if (_navigation.CurrentPage != typeof(SettingsPage))
            {
                _navigation.Navigate(typeof(SettingsPage));
            }
        });
        AddAccelerator(VirtualKey.Left, VirtualKeyModifiers.Menu, () => _navigation.GoBack());
        AddAccelerator(VirtualKey.F11, VirtualKeyModifiers.None, () => _chrome.IsFullScreen = !_chrome.IsFullScreen);
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            action();
            args.Handled = true;
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var properties = e.GetCurrentPoint(RootGrid).Properties;
        if (properties.IsXButton1Pressed)
        {
            _navigation.GoBack();
            e.Handled = true;
        }
    }
}
