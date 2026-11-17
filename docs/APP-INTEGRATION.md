# App integration contract (wave 3)

Shared brief for the three agents building `AnimaWindows.App`. Read with `docs/ARCHITECTURE.md`.

## File ownership

| Owner | Owns (create/edit only these) |
|---|---|
| **shell** | `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `AnimaWindows.App.csproj`, `Package.appxmanifest`, `app.manifest`, `Assets/`, `Strings/` (all `.resw`), `Composition/`, `Shell/`, `Pages/Home/`, `Pages/Search/`, `Pages/Details/`, `Pages/Settings/`, `Services/` (except `Services/Notifications/`), `Controls/` (except `Controls/Tracking/`), `Themes/`, `Converters/`, `ViewModels/` for the pages it owns |
| **player** | `Player/**` only (page, view model, engines, `LocalStreamProxy`, subtitle overlay, SMTC, PiP) |
| **downloads** | `Pages/Downloads/`, `Pages/Health/`, `Services/Notifications/`, `Controls/Tracking/` |

Anything you need in a file you do not own: write it in your report (e.g. "add package X to csproj", "add string keys …", "add NavigationViewItem for Downloads") — the main session merges it.

## Hand-off points (fixed names — code against them even before they exist)

- `AnimaWindows.App.Player.PlayerNavigationArgs` — `sealed record PlayerNavigationArgs(string ProviderId, Anime Anime, IReadOnlyList<Episode> Episodes, int StartIndex, bool Offline)` (mirrors animadroid `PlayerRoute`). Owned by **player**.
- `AnimaWindows.App.Player.PlayerPage` — navigated to by `INavigationService.Navigate(typeof(PlayerPage), args)`; the shell hides its chrome while the player page is shown (property `Shell.IsImmersive` toggled by player through `IShellChrome`).
- `AnimaWindows.App.Player.PlayerServiceCollectionExtensions.AddPlayer(this IServiceCollection)` — registers engines, proxy, view model. Called by shell's composition root.
- `AnimaWindows.App.Pages.Downloads.DownloadsPage`, `AnimaWindows.App.Pages.Health.HealthPage`, `AnimaWindows.App.Controls.Tracking.TrackingSection` (UserControl hosted in Settings › Advanced), `AnimaWindows.App.Services.Notifications.DownloadsServiceCollectionExtensions.AddDownloadsUi(this IServiceCollection)`. Owned by **downloads**.
- Owned by **shell**, in `Services/`:
  - `INavigationService` — `Navigate(Type page, object? parameter = null)`, `GoBack()`, `CanGoBack`
  - `IShellChrome` — `bool IsImmersive { get; set; }`, `bool IsFullScreen { get; set; }`, `void EnterCompactOverlay()`, `void ExitCompactOverlay()`, `nint WindowHandle`, `Microsoft.UI.Windowing.AppWindow AppWindow`
  - `IDispatcher` — wrapper over `DispatcherQueue` with `Task RunAsync(Action)`
  - `AppServices` — static accessor `AppServices.Get<T>()` for pages constructed by `Frame`

## Composition (shell builds it; others rely on it)

`Microsoft.Extensions.Hosting` host in `App.OnLaunched`. Singletons:

- `IAppPaths` (packaged `ApplicationData.Current.LocalFolder` / `LocalCacheFolder`; downloads `%USERPROFILE%\Videos\animawindows`), `IClock`, `INetworkCostProvider` (`NetworkInformation`), `ISecretStore` (`PasswordVault`), `ICloudflareSolver` (WebView2 dialog).
- Shared `CookieContainer`; main `HttpClient` = `AnimaHttpClientFactory.Create(cookies, solver)`; a second **no-redirect** client on the same cookies (needed by `ShindenProvider.Create(http, config, noRedirectClient)` for Sibnet).
- `JsonSettingsStore` — **call `LoadAsync()` before first use**.
- `AnimaDatabase(IAppPaths)`, `SqliteLibraryRepository(db, clock)`, `SqliteDownloadRepository(db)`, `BackupService(db, clock)` (concrete type also exposes `ImportAniCliHistoryAsync`).
- `RemoteConfigRepository` (bundled file `Assets/providers.json`); providers: `HianimeProvider`, `KickAssAnimeProvider`, `AnimeParadiseProvider` (ctor `(HttpClient, IProviderConfig)`), `ShindenProvider.Create(...)`, `AniLibriaProvider`, `AnimeFireProvider`; `ProviderChain`.
- `AniSkipRepository`, `MalIdResolver`, `UpdateChecker`, `HealthRunner`.
- `SyncRepository` (registered as both concrete and `ISyncRepository`; player calls concrete `ReportProgress`), `SyncScheduler` (start at launch). Tracker client ids from MSBuild properties `AniListClientId` / `MalClientId` → generated constants; blank disables.
- `DownloadManager` (concrete + `IDownloadController`), **call `StartAsync()` at launch**. Completed downloads rewrite `StreamSet` to `file:` URIs (local `main.m3u8` with relative segment paths and `.key` files, or a single media file).

Background events from Core services fire on thread-pool threads — marshal to UI with `IDispatcher`.

## UI rules (all agents)

- Fluent: theme resources only, no hard-coded colours/sizes outside `Themes/`; Mica; standard type ramp; 4 px grid; `x:Bind` only (no `{Binding}`); `CommunityToolkit.Mvvm` source generators (`[ObservableProperty]`, `[RelayCommand]`); every interactive element has `AutomationProperties.Name` and keyboard access; strings from `.resw` via `x:Uid` (en-US, pl-PL, zh-Hans).
- No blocking calls on the UI thread; cancellation on page leave.
- No .NET SDK on the authoring machine: code is compiled only by CI. Prefer conventional, well-known APIs; avoid speculative ones. Use the `winui:winui-design` / `winapp find-api` knowledge where available to confirm control and API names.
