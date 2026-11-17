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
        AppWindow.Title = "animawindows";

        _placement = new WindowPlacement(AppServices.Get<IAppPaths>(), AppWindow, DispatcherQueue);
        _placement.Restore();

        ContentFrame.Navigated += OnNavigated;
        _chrome.StateChanged += (_, _) => ApplyChrome();
        _settings.Changed += (_, s) => _dispatcher.TryEnqueue(() => ApplyTheme(s.ThemeMode));
        ApplyTheme(_settings.Current.ThemeMode);

        RegisterAccelerators();
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

        if (AppWindowTitleBar.IsCustomizationSupported())
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
        var hideTitleBar = immersive || _chrome.IsFullScreen || _chrome.IsCompactOverlay;
        AppTitleBar.Visibility = hideTitleBar ? Visibility.Collapsed : Visibility.Visible;
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
        AppTitleBar.IsBackButtonVisible = ContentFrame.CanGoBack;

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
