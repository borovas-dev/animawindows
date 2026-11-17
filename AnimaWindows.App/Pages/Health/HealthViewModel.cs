using System.Collections.ObjectModel;
using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimaWindows.App.Pages.Health;

/// <summary>Provider health screen. Mirrors feature/settings HealthViewModel: runs <see cref="HealthRunner"/> on every provider in parallel.</summary>
internal sealed partial class HealthViewModel : ObservableObject
{
    private readonly IProviderChain _chain;
    private readonly ISettingsStore _settings;
    private readonly HealthRunner _runner;
    private readonly IDispatcher _dispatcher;
    private CancellationTokenSource? _cts;

    public HealthViewModel(IProviderChain chain, ISettingsStore settings, HealthRunner runner, IDispatcher dispatcher)
    {
        _chain = chain;
        _settings = settings;
        _runner = runner;
        _dispatcher = dispatcher;
    }

    public ObservableCollection<HealthProviderViewModel> Providers { get; } = [];

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    /// <summary>"Language: en. Chain: A > B".</summary>
    [ObservableProperty]
    public partial string? ChainText { get; set; }

    /// <summary>Runs the checks (also the "Run again" command). Ignored while a run is active.</summary>
    [RelayCommand]
    private async Task RunAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var ct = cts.Token;
        IsRunning = true;
        try
        {
            var settings = _settings.Current;
            var inChain = _chain.ChainFor(settings);
            var initial = new List<(IAnimeProvider Provider, ProviderHealth Health, HealthProviderViewModel Vm)>();
            foreach (var id in _chain.Ids)
            {
                if (_chain.Provider(id) is not { } provider)
                {
                    continue;
                }

                var health = new ProviderHealth(provider.Id, provider.DisplayName)
                {
                    Disabled = settings.DisabledProviders.Contains(provider.Id),
                    InChain = inChain.Any(p => p.Id == provider.Id),
                };
                initial.Add((provider, health, new HealthProviderViewModel(health)));
            }

            var names = string.Join(" > ", inChain.Select(p => p.DisplayName));
            ChainText = UiText.Format("Health_Chain", "Language: {0}. Chain: {1}", _chain.EffectiveLanguage(settings), names.Length == 0 ? "-" : names);
            Providers.Clear();
            foreach (var entry in initial)
            {
                Providers.Add(entry.Vm);
            }

            await Task.WhenAll(initial.Select(entry =>
                _runner.CheckAsync(entry.Provider, entry.Health, update => _ = _dispatcher.RunAsync(() => entry.Vm.Update(update)), ct)));
        }
        catch (OperationCanceledException)
        {
            // page left or re-run requested
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsRunning = false;
            }
        }
    }

    /// <summary>Cancels a running check (page unload).</summary>
    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
        IsRunning = false;
    }
}
