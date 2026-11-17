using AnimaWindows.App.Services;
using AnimaWindows.Core.AniSkip;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Chain;
using AnimaWindows.Core.Tracking;
using Microsoft.Extensions.DependencyInjection;

namespace AnimaWindows.App.Player;

/// <summary>Registration of the player (engines, loopback proxy, view model) in the composition root.</summary>
public static class PlayerServiceCollectionExtensions
{
    /// <summary>
    /// Registers the player services. Expects the shell's registrations: <see cref="IProviderChain"/> (or the concrete
    /// <see cref="ProviderChain"/>), <see cref="ISettingsStore"/>, <see cref="ILibraryRepository"/>, <see cref="IDownloadRepository"/>,
    /// <see cref="ISkipTimesRepository"/> / <see cref="AniSkipRepository"/>, <see cref="IMalIdResolver"/> / <see cref="MalIdResolver"/>,
    /// <see cref="SyncRepository"/>, <see cref="IClock"/>, <see cref="INetworkCostProvider"/>, <see cref="ICloudflareSolver"/>,
    /// <see cref="IDispatcher"/>, <see cref="IShellChrome"/> and <see cref="INavigationService"/>.
    /// </summary>
    public static IServiceCollection AddPlayer(this IServiceCollection services)
    {
        services.AddSingleton<LocalStreamProxy>();
        services.AddSingleton<PlayerSessionState>();
        services.AddSingleton<SubtitleLoader>();
        services.AddSingleton<IPlayerEngineFactory, PlayerEngineFactory>();
        services.AddTransient<PlayerMediaSession>();
        services.AddTransient(sp => new PlaybackResolver(
            Either<IProviderChain, ProviderChain>(sp),
            sp.GetRequiredService<IDownloadRepository>(),
            sp.GetRequiredService<ISettingsStore>()));
        services.AddTransient(sp => new PlayerViewModel(
            sp.GetRequiredService<ISettingsStore>(),
            sp.GetRequiredService<ILibraryRepository>(),
            sp.GetRequiredService<PlaybackResolver>(),
            Either<ISkipTimesRepository, AniSkipRepository>(sp),
            Either<IMalIdResolver, MalIdResolver>(sp),
            sp.GetRequiredService<SyncRepository>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<INetworkCostProvider>(),
            sp.GetRequiredService<ICloudflareSolver>(),
            sp.GetRequiredService<IPlayerEngineFactory>(),
            sp.GetRequiredService<PlayerSessionState>(),
            sp.GetRequiredService<SubtitleLoader>(),
            sp.GetRequiredService<PlayerMediaSession>(),
            sp.GetRequiredService<IDispatcher>(),
            sp.GetRequiredService<IShellChrome>(),
            sp.GetRequiredService<INavigationService>()));
        return services;
    }

    /// <summary>The interface registration, else the concrete one (the shell may register either).</summary>
    private static TInterface Either<TInterface, TConcrete>(IServiceProvider provider)
        where TInterface : class
        where TConcrete : class, TInterface =>
        provider.GetService<TInterface>() ?? provider.GetRequiredService<TConcrete>();
}
