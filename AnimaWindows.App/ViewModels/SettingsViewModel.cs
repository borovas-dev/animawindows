using System.Collections.ObjectModel;
using System.Globalization;
using AnimaWindows.App.Pages.Health;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Updates;
using AnimaWindows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.ViewModels;

public sealed record LanguageOption(string Code, string Name);

/// <summary>Settings page state; mirrors <see cref="AppSettings"/> (two-way) and hosts backup, update and provider management.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private static readonly QualityPreference[] Qualities =
    [
        QualityPreference.Best, QualityPreference.P1080, QualityPreference.P720,
        QualityPreference.P480, QualityPreference.P360, QualityPreference.Worst,
    ];

    private static readonly string[] SubtitleCodes = ["", "en", "pl", "pt", "es", "fr", "de", "it", "ru", "ja", "ko", "zh", "ar", "id", "tr", "vi", "th"];

    private readonly ISettingsStore _settings;
    private readonly IProviderChain _chain;
    private readonly BackupService _backup;
    private readonly UpdateChecker _updates;
    private readonly FilePickers _pickers;
    private readonly INavigationService _navigation;
    private readonly IDispatcher _dispatcher;
    private readonly IAppPaths _paths;
    private bool _loading;
    private bool _started;

    public SettingsViewModel(
        ISettingsStore settings,
        IProviderChain chain,
        BackupService backup,
        UpdateChecker updates,
        FilePickers pickers,
        INavigationService navigation,
        IDispatcher dispatcher,
        IAppPaths paths)
    {
        _settings = settings;
        _chain = chain;
        _backup = backup;
        _updates = updates;
        _pickers = pickers;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _paths = paths;

        QualityNames = (string[])[Strings.Get("Quality_Best"), "1080p", "720p", "480p", "360p", Strings.Get("Quality_Worst")];
        EngineNames = (string[])[Strings.Get("Engine_Auto"), Strings.Get("Engine_Native"), Strings.Get("Engine_Mpv")];
        AudioNames = (string[])[Strings.Get("Audio_Sub"), Strings.Get("Audio_Dub")];
        SkipBehaviorNames = (string[])[Strings.Get("Skip_Auto"), Strings.Get("Skip_Button")];
        ThemeNames = (string[])[Strings.Get("Theme_System"), Strings.Get("Theme_Light"), Strings.Get("Theme_Dark")];
        StrategyNames = (string[])[Strings.Get("Strategy_Reliable"), Strings.Get("Strategy_BestQuality")];
        SubtitleLanguages = (LanguageOption[])[.. SubtitleCodes.Select(c => new LanguageOption(c, c.Length == 0 ? Strings.Get("Common_Off") : LanguageName(c)))];
        var contentCodes = _chain.Ids
            .Select(id => _chain.Provider(id))
            .Where(p => p is not null)
            .SelectMany(p => p!.ContentLanguages)
            .Distinct()
            .OrderBy(c => c, StringComparer.Ordinal);
        ContentLanguages = (LanguageOption[])[new LanguageOption("auto", Strings.Get("Language_Auto")), .. contentCodes.Select(c => new LanguageOption(c, LanguageName(c)))];
        SubtitleLanguageNames = (string[])[.. SubtitleLanguages.Select(l => l.Name)];
        ContentLanguageNames = (string[])[.. ContentLanguages.Select(l => l.Name)];

        VersionText = Strings.Format("Settings_Version", AppVersion.Current);
        StatusMessage = "";
        DownloadDirectoryText = "";
        UpdateText = "";
        UpdateLink = ReleasesUri;
        ApplySettings(_settings.Current);
        ApplyUpdate(_updates.State);
    }

    public IReadOnlyList<string> QualityNames { get; }

    public IReadOnlyList<string> EngineNames { get; }

    public IReadOnlyList<string> AudioNames { get; }

    public IReadOnlyList<string> SkipBehaviorNames { get; }

    public IReadOnlyList<string> ThemeNames { get; }

    public IReadOnlyList<string> StrategyNames { get; }

    public IReadOnlyList<LanguageOption> SubtitleLanguages { get; }

    public IReadOnlyList<LanguageOption> ContentLanguages { get; }

    public IReadOnlyList<string> SubtitleLanguageNames { get; }

    public IReadOnlyList<string> ContentLanguageNames { get; }

    public ObservableCollection<ProviderRowViewModel> Providers { get; } = [];

    public string VersionText { get; }

    public Uri ReleasesUri { get; } = new($"https://github.com/{UpdateChecker.DefaultRepo}/releases");

    public Uri SourceUri { get; } = new($"https://github.com/{UpdateChecker.DefaultRepo}");

    // ---- mirrored settings ----

    [ObservableProperty]
    public partial int QualityIndex { get; set; }

    [ObservableProperty]
    public partial bool AutoPlayNext { get; set; }

    [ObservableProperty]
    public partial bool PrefetchNextEpisode { get; set; }

    [ObservableProperty]
    public partial bool PrefetchOnMetered { get; set; }

    [ObservableProperty]
    public partial int EngineIndex { get; set; }

    [ObservableProperty]
    public partial int AudioIndex { get; set; }

    [ObservableProperty]
    public partial int SubtitleLanguageIndex { get; set; }

    [ObservableProperty]
    public partial bool AniSkipEnabled { get; set; }

    [ObservableProperty]
    public partial int SkipBehaviorIndex { get; set; }

    [ObservableProperty]
    public partial double SkipOffsetSec { get; set; }

    [ObservableProperty]
    public partial bool AutoSkipIntro { get; set; }

    [ObservableProperty]
    public partial bool AutoSkipOutro { get; set; }

    [ObservableProperty]
    public partial int ThemeIndex { get; set; }

    [ObservableProperty]
    public partial bool DownloadWifiOnly { get; set; }

    [ObservableProperty]
    public partial string DownloadDirectoryText { get; set; }

    [ObservableProperty]
    public partial bool HasCustomDownloadDirectory { get; set; }

    [ObservableProperty]
    public partial int ContentLanguageIndex { get; set; }

    [ObservableProperty]
    public partial int StrategyIndex { get; set; }

    [ObservableProperty]
    public partial bool CheckForUpdates { get; set; }

    [ObservableProperty]
    public partial bool IncludePrereleases { get; set; }

    // ---- status / update ----

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; }

    [ObservableProperty]
    public partial bool HasStatus { get; set; }

    [ObservableProperty]
    public partial string UpdateText { get; set; }

    [ObservableProperty]
    public partial Uri UpdateLink { get; set; }

    [ObservableProperty]
    public partial bool IsCheckingUpdate { get; set; }

    public string SkipOffsetText => Strings.Format("Settings_SkipOffset", (int)SkipOffsetSec);

    public string ProvidersDescription => Strings.Format("Settings_ProvidersLanguage", _chain.EffectiveLanguage(_settings.Current));

    /// <summary>Starts following settings and update changes (page shown).</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnSettingsChanged;
        _updates.StateChanged += OnUpdateChanged;
        ApplySettings(_settings.Current);
        ApplyUpdate(_updates.State);
    }

    /// <summary>Stops following changes (page left).</summary>
    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _settings.Changed -= OnSettingsChanged;
        _updates.StateChanged -= OnUpdateChanged;
    }

    private void OnSettingsChanged(object? sender, AppSettings s) => _dispatcher.TryEnqueue(() => ApplySettings(s));

    private void OnUpdateChanged(object? sender, UpdateState s) => _dispatcher.TryEnqueue(() => ApplyUpdate(s));

    private static string LanguageName(string code)
    {
        try
        {
            var name = CultureInfo.GetCultureInfo(code).NativeName;
            return name.Length > 0 ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..] : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    private void ApplySettings(AppSettings s)
    {
        _loading = true;
        try
        {
            QualityIndex = Math.Max(0, Array.IndexOf(Qualities, s.Quality));
            AutoPlayNext = s.AutoPlayNext;
            PrefetchNextEpisode = s.PrefetchNextEpisode;
            PrefetchOnMetered = s.PrefetchOnMetered;
            EngineIndex = (int)s.Engine;
            AudioIndex = s.Mode == TranslationMode.Dub ? 1 : 0;
            SubtitleLanguageIndex = Math.Max(0, Array.IndexOf(SubtitleCodes, s.SubtitleLanguage));
            AniSkipEnabled = s.AniSkipEnabled;
            SkipBehaviorIndex = s.SkipBehavior == SkipBehavior.Button ? 1 : 0;
            SkipOffsetSec = s.SkipOffsetSec;
            AutoSkipIntro = s.AutoSkipIntro;
            AutoSkipOutro = s.AutoSkipOutro;
            ThemeIndex = (int)s.ThemeMode;
            DownloadWifiOnly = s.DownloadWifiOnly;
            HasCustomDownloadDirectory = !string.IsNullOrWhiteSpace(s.DownloadDirectory);
            DownloadDirectoryText = HasCustomDownloadDirectory ? s.DownloadDirectory! : _paths.DownloadsDirectory;
            var contentIndex = ContentLanguages.ToList().FindIndex(l => l.Code == s.ContentLanguage);
            ContentLanguageIndex = Math.Max(0, contentIndex);
            StrategyIndex = s.SourceStrategy == SourceStrategy.BestQuality ? 1 : 0;
            CheckForUpdates = s.CheckForUpdates;
            IncludePrereleases = s.IncludePrereleases;
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(ProvidersDescription));
        RebuildProviders(s);
    }

    private void Push(Func<AppSettings, AppSettings> transform)
    {
        if (!_loading)
        {
            _ = _settings.UpdateAsync(transform);
        }
    }

    partial void OnQualityIndexChanged(int value)
    {
        if (value >= 0 && value < Qualities.Length)
        {
            Push(s => s with { Quality = Qualities[value] });
        }
    }

    partial void OnAutoPlayNextChanged(bool value) => Push(s => s with { AutoPlayNext = value });

    partial void OnPrefetchNextEpisodeChanged(bool value) => Push(s => s with { PrefetchNextEpisode = value });

    partial void OnPrefetchOnMeteredChanged(bool value) => Push(s => s with { PrefetchOnMetered = value });

    partial void OnEngineIndexChanged(int value)
    {
        if (value >= 0)
        {
            Push(s => s with { Engine = (PlaybackEngineChoice)value });
        }
    }

    partial void OnAudioIndexChanged(int value)
    {
        if (value >= 0)
        {
            Push(s => s with { Mode = value == 1 ? TranslationMode.Dub : TranslationMode.Sub });
        }
    }

    partial void OnSubtitleLanguageIndexChanged(int value)
    {
        if (value >= 0 && value < SubtitleCodes.Length)
        {
            Push(s => s with { SubtitleLanguage = SubtitleCodes[value] });
        }
    }

    partial void OnAniSkipEnabledChanged(bool value) => Push(s => s with { AniSkipEnabled = value });

    partial void OnSkipBehaviorIndexChanged(int value)
    {
        if (value >= 0)
        {
            Push(s => s with { SkipBehavior = value == 1 ? SkipBehavior.Button : SkipBehavior.Auto });
        }
    }

    partial void OnSkipOffsetSecChanged(double value)
    {
        OnPropertyChanged(nameof(SkipOffsetText));
        var seconds = (int)Math.Clamp(Math.Round(value), 0, 5);
        Push(s => s with { SkipOffsetSec = seconds });
    }

    partial void OnAutoSkipIntroChanged(bool value) => Push(s => s with { AutoSkipIntro = value });

    partial void OnAutoSkipOutroChanged(bool value) => Push(s => s with { AutoSkipOutro = value });

    partial void OnThemeIndexChanged(int value)
    {
        if (value >= 0)
        {
            Push(s => s with { ThemeMode = (ThemeMode)value });
        }
    }

    partial void OnDownloadWifiOnlyChanged(bool value) => Push(s => s with { DownloadWifiOnly = value });

    partial void OnContentLanguageIndexChanged(int value)
    {
        if (value >= 0 && value < ContentLanguages.Count)
        {
            var code = ContentLanguages[value].Code;
            Push(s => s with { ContentLanguage = code });
        }
    }

    partial void OnStrategyIndexChanged(int value)
    {
        if (value >= 0)
        {
            Push(s => s with { SourceStrategy = value == 1 ? SourceStrategy.BestQuality : SourceStrategy.Reliable });
        }
    }

    partial void OnCheckForUpdatesChanged(bool value) => Push(s => s with { CheckForUpdates = value });

    partial void OnIncludePrereleasesChanged(bool value) => Push(s => s with { IncludePrereleases = value });

    // ---- providers ----

    private void RebuildProviders(AppSettings s)
    {
        var lang = _chain.EffectiveLanguage(s);
        IReadOnlyList<string> manual = s.ProviderOrder.TryGetValue(lang, out var m) ? m : [];

        int ManualIndex(IAnimeProvider p)
        {
            var i = manual.ToList().IndexOf(p.Id);
            return i < 0 ? int.MaxValue : i;
        }

        IEnumerable<IAnimeProvider> Sort(IEnumerable<IAnimeProvider> src) =>
            src.OrderBy(ManualIndex).ThenBy(p => p.Priority).ThenBy(p => p.Id, StringComparer.Ordinal);

        var all = _chain.Ids.Select(id => _chain.Provider(id)).OfType<IAnimeProvider>().ToList();
        var native = Sort(all.Where(p => p.ContentLanguages.Contains(lang))).ToList();
        var english = lang == "en" ? [] : Sort(all.Where(p => p.ContentLanguages.Contains("en") && !native.Contains(p))).ToList();
        var ordered = native.Concat(english).ToList();

        Providers.Clear();
        for (var i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            Providers.Add(new ProviderRowViewModel(
                p.Id, p.DisplayName, !s.DisabledProviders.Contains(p.Id), i > 0, i < ordered.Count - 1, OnProviderToggled));
        }
    }

    private void OnProviderToggled(ProviderRowViewModel row)
    {
        var id = row.Id;
        var enabled = row.IsEnabled;
        _ = _settings.UpdateAsync(s => s with
        {
            DisabledProviders = enabled ? s.DisabledProviders.Remove(id) : s.DisabledProviders.Add(id),
        });
    }

    public void MoveProvider(ProviderRowViewModel row, int delta)
    {
        var ids = Providers.Select(p => p.Id).ToList();
        var i = ids.IndexOf(row.Id);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= ids.Count)
        {
            return;
        }

        (ids[i], ids[j]) = (ids[j], ids[i]);
        var lang = _chain.EffectiveLanguage(_settings.Current);
        _ = _settings.UpdateAsync(s =>
        {
            var order = new Dictionary<string, IReadOnlyList<string>>(s.ProviderOrder) { [lang] = ids };
            return s with { ProviderOrder = order };
        });
    }

    [RelayCommand]
    private void OpenHealth() => _navigation.Navigate(typeof(HealthPage));

    // ---- downloads folder ----

    [RelayCommand]
    private async Task ChooseDownloadFolderAsync()
    {
        var path = await _pickers.PickFolderAsync();
        if (path is not null)
        {
            await _settings.UpdateAsync(s => s with { DownloadDirectory = path });
        }
    }

    [RelayCommand]
    private Task ResetDownloadFolderAsync() => _settings.UpdateAsync(s => s with { DownloadDirectory = null });

    // ---- backup ----

    [RelayCommand]
    private async Task ExportBackupAsync()
    {
        var path = await _pickers.PickSaveFileAsync("animawindows-backup", Strings.Get("Settings_BackupFileType"), ".json");
        if (path is null)
        {
            return;
        }

        try
        {
            var json = await _settings.ExportJsonAsync();
            await using var stream = File.Create(path);
            await _backup.ExportAsync(stream, json);
            SetStatus(Strings.Get("Backup_ExportOk"), InfoBarSeverity.Success);
        }
        catch (Exception e)
        {
            SetStatus(Strings.Format("Backup_ExportFailed", e.Message), InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportBackupAsync()
    {
        var path = await _pickers.PickOpenFileAsync(".json");
        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var result = await _backup.ImportAsync(stream);
            if (!string.IsNullOrWhiteSpace(result.SettingsJson))
            {
                await _settings.ImportJsonAsync(result.SettingsJson);
            }

            SetStatus(Strings.Format("Backup_ImportOk", result.Anime, result.Progress), InfoBarSeverity.Success);
        }
        catch (Exception e)
        {
            SetStatus(Strings.Format("Backup_ImportFailed", e.Message), InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task ImportAniCliAsync()
    {
        var path = await _pickers.PickOpenFileAsync();
        if (path is null)
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var count = await _backup.ImportAniCliHistoryAsync(stream);
            SetStatus(Strings.Format("Backup_ImportAniCliOk", count), InfoBarSeverity.Success);
        }
        catch (Exception e)
        {
            SetStatus(Strings.Format("Backup_ImportFailed", e.Message), InfoBarSeverity.Error);
        }
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        HasStatus = true;
    }

    // ---- updates ----

    [RelayCommand]
    private async Task CheckUpdatesAsync() => await _updates.CheckAsync(_settings.Current.IncludePrereleases);

    private void ApplyUpdate(UpdateState state)
    {
        UpdateLink = ReleasesUri;
        IsCheckingUpdate = state is UpdateState.Checking;
        switch (state)
        {
            case UpdateState.Checking:
                UpdateText = Strings.Get("Update_Checking");
                break;
            case UpdateState.UpToDate:
                UpdateText = Strings.Get("Update_UpToDate");
                break;
            case UpdateState.Available a:
                UpdateText = Strings.Format("Update_Available", a.Version);
                if (Uri.TryCreate(a.PageUrl, UriKind.Absolute, out var page))
                {
                    UpdateLink = page;
                }

                break;
            case UpdateState.Failed f:
                UpdateText = Strings.Format("Update_Failed", f.Message);
                break;
            default:
                UpdateText = "";
                break;
        }
    }
}
