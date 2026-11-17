using System.Net;
using AnimaWindows.App.Player;
using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.App.ViewModels;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.AniSkip;
using AnimaWindows.Core.Chain;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Health;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Net;
using AnimaWindows.Core.RemoteConfig;
using AnimaWindows.Core.Tracking;
using AnimaWindows.Core.Updates;
using AnimaWindows.Data;
using AnimaWindows.Providers.AniLibria;
using AnimaWindows.Providers.AnimeFire;
using AnimaWindows.Providers.AnimeParadise;
using AnimaWindows.Providers.HiAnime;
using AnimaWindows.Providers.KickAssAnime;
using AnimaWindows.Providers.Shinden;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;

namespace AnimaWindows.App.Composition;

/// <summary>The composition root: every singleton of the app (see docs/APP-INTEGRATION.md).</summary>
public static class ServiceRegistration
{
    public static IServiceCollection AddAnimaApp(this IServiceCollection services, DispatcherQueue queue)
    {
        // Platform services.
        services.AddSingleton<IDispatcher>(new DispatcherQueueDispatcher(queue));
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAppPaths, WinAppPaths>();
        services.AddSingleton<INetworkCostProvider, NetworkCostProvider>();
        services.AddSingleton<ISecretStore, PasswordVaultSecretStore>();
        services.AddSingleton<ShellChrome>();
        services.AddSingleton<IShellChrome>(sp => sp.GetRequiredService<ShellChrome>());
        services.AddSingleton<NavigationService>();
        services.AddSingleton<INavigationService>(sp => sp.GetRequiredService<NavigationService>());
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<FilePickers>();

        // Networking: shared cookies, Cloudflare solver, two HTTP clients.
        services.AddSingleton(new CookieContainer());
        services.AddSingleton<WebView2CloudflareSolver>();
        services.AddSingleton<ICloudflareSolver>(sp => sp.GetRequiredService<WebView2CloudflareSolver>());
        services.AddSingleton(sp => AnimaHttpClientFactory.Create(
            sp.GetRequiredService<CookieContainer>(), sp.GetRequiredService<ICloudflareSolver>()));
        services.AddSingleton(sp =>
        {
            var handler = new SocketsHttpHandler
            {
                CookieContainer = sp.GetRequiredService<CookieContainer>(),
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                ConnectTimeout = AnimaHttpClientFactory.DefaultTimeout,
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            };
            var client = new HttpClient(handler) { Timeout = AnimaHttpClientFactory.DefaultTimeout };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(NetworkConstants.AniCliUserAgent);
            return new NoRedirectHttpClient(client);
        });

        // Data.
        services.AddSingleton<JsonSettingsStore>();
        services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<JsonSettingsStore>());
        services.AddSingleton<AnimaDatabase>();
        services.AddSingleton<SqliteLibraryRepository>();
        services.AddSingleton<ILibraryRepository>(sp => sp.GetRequiredService<SqliteLibraryRepository>());
        services.AddSingleton<SqliteDownloadRepository>();
        services.AddSingleton<IDownloadRepository>(sp => sp.GetRequiredService<SqliteDownloadRepository>());
        services.AddSingleton<BackupService>();
        services.AddSingleton<IBackupService>(sp => sp.GetRequiredService<BackupService>());

        // Remote config, providers, chain.
        services.AddSingleton(sp => new RemoteConfigRepository(
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<IAppPaths>(),
            Path.Combine(AppContext.BaseDirectory, "Assets", "providers.json"),
            new RemoteConfigOptions(AppVersion.Current)));
        services.AddSingleton<IRemoteConfigSource>(sp => sp.GetRequiredService<RemoteConfigRepository>());
        services.AddSingleton<IProviderConfig>(sp => sp.GetRequiredService<RemoteConfigRepository>());
        services.AddSingleton<IProviderAvailability, RemoteConfigAvailability>();
        services.AddSingleton<IAnimeProvider>(sp => new HianimeProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProviderConfig>()));
        services.AddSingleton<IAnimeProvider>(sp => new KickAssAnimeProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProviderConfig>()));
        services.AddSingleton<IAnimeProvider>(sp => new AnimeParadiseProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProviderConfig>()));
        // shinden.pl answers .NET's HTTP client with 403 ("update your browser") but serves Edge: route it through WebView2.
        services.AddSingleton<WebView2Fetcher>();
        services.AddSingleton<IAnimeProvider>(sp => ShindenProvider.Create(
            new HttpClient(new BrowserRoutingHandler(sp.GetRequiredService<WebView2Fetcher>(), sp.GetRequiredService<HttpClient>(), "shinden.pl"))
            {
                Timeout = TimeSpan.FromSeconds(60),
            },
            sp.GetRequiredService<IProviderConfig>(),
            sp.GetRequiredService<NoRedirectHttpClient>().Client));
        services.AddSingleton<IAnimeProvider>(sp => new AniLibriaProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProviderConfig>()));
        services.AddSingleton<IAnimeProvider>(sp => new AnimeFireProvider(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<IProviderConfig>()));
        services.AddSingleton(sp => new ProviderChain(
            sp.GetServices<IAnimeProvider>(),
            sp.GetRequiredService<ISettingsStore>(),
            ProviderChainOptions.Default,
            sp.GetRequiredService<IProviderAvailability>()));
        services.AddSingleton<IProviderChain>(sp => sp.GetRequiredService<ProviderChain>());

        // AniSkip, updates, health.
        services.AddSingleton(sp => new AniSkipRepository(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<ISkipTimesRepository>(sp => sp.GetRequiredService<AniSkipRepository>());
        services.AddSingleton(sp => new MalIdResolver(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IMalIdResolver>(sp => sp.GetRequiredService<MalIdResolver>());
        services.AddSingleton(sp => new UpdateChecker(sp.GetRequiredService<HttpClient>(), AppVersion.Current));
        services.AddSingleton<HealthRunner>();

        // Tracking.
        services.AddSingleton(sp => new SyncRepository(
            new SyncConfig(TrackerIds.AniListClientId, TrackerIds.MalClientId),
            new AniListClient(sp.GetRequiredService<HttpClient>()),
            new MalClient(sp.GetRequiredService<HttpClient>()),
            sp.GetRequiredService<ISecretStore>(),
            new SyncQueueStore(sp.GetRequiredService<IAppPaths>().DataDirectory),
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<ISyncRepository>(sp => sp.GetRequiredService<SyncRepository>());
        services.AddSingleton(sp => new SyncScheduler(sp.GetRequiredService<SyncRepository>(), sp.GetRequiredService<INetworkCostProvider>()));

        // Downloads.
        services.AddSingleton(sp => new DownloadPlanner(sp.GetRequiredService<IProviderChain>(), sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new HlsDownloader(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new ProgressiveDownloader(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new DownloadManager(
            sp.GetRequiredService<IDownloadRepository>(),
            sp.GetRequiredService<DownloadPlanner>(),
            sp.GetRequiredService<HlsDownloader>(),
            sp.GetRequiredService<ProgressiveDownloader>(),
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<INetworkCostProvider>(),
            sp.GetRequiredService<IAppPaths>(),
            sp.GetRequiredService<IClock>()));
        services.AddSingleton<IDownloadController>(sp => sp.GetRequiredService<DownloadManager>());

        // View models of the shell-owned pages.
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<SearchViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<DetailsViewModel>();
        services.AddTransient<SettingsViewModel>();

        // Other work packages.
        services.AddPlayer();
        services.AddDownloadsUi();
        return services;
    }
}
