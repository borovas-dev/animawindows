using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.Pages.Health;

/// <summary>Live check card of one provider; updated in place while <see cref="HealthRunner"/> reports progress.</summary>
internal sealed partial class HealthProviderViewModel : ObservableObject
{
    public HealthProviderViewModel(ProviderHealth initial)
    {
        Id = initial.Id;
        Name = initial.Name;
        Search = new HealthStepViewModel(UiText.Get("Health_StepSearch", "Search"));
        Episodes = new HealthStepViewModel(UiText.Get("Health_StepEpisodes", "Episodes"));
        Streams = new HealthStepViewModel(UiText.Get("Health_StepStreams", "Streams"));
        Update(initial);
    }

    public string Id { get; }

    public string Name { get; }

    public HealthStepViewModel Search { get; }

    public HealthStepViewModel Episodes { get; }

    public HealthStepViewModel Streams { get; }

    /// <summary>"Disabled" / "Not in chain" tag, empty when neither applies.</summary>
    [ObservableProperty]
    public partial string? TagText { get; set; }

    [ObservableProperty]
    public partial bool HasTag { get; set; }

    internal void Update(ProviderHealth health)
    {
        Search.Update(health.Search);
        Episodes.Update(health.Episodes);
        Streams.Update(health.Streams);
        TagText = health.Disabled
            ? UiText.Get("Health_Disabled", "Disabled")
            : !health.InChain ? UiText.Get("Health_NotInChain", "Not in chain") : string.Empty;
        HasTag = !string.IsNullOrEmpty(TagText);
    }
}
