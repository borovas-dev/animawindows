using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Health;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.Pages.Health;

/// <summary>One check step (search / episodes / streams) of a provider.</summary>
internal sealed partial class HealthStepViewModel : ObservableObject
{
    public HealthStepViewModel(string label)
    {
        Label = label;
        Update(new StepResult(StepStatus.Pending));
    }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsPending { get; set; }

    [ObservableProperty]
    public partial bool IsOk { get; set; }

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    [ObservableProperty]
    public partial bool IsSkipped { get; set; }

    /// <summary>Latency, plus the error text for failed steps.</summary>
    [ObservableProperty]
    public partial string? Detail { get; set; }

    [ObservableProperty]
    public partial string? AutomationName { get; set; }

    internal void Update(StepResult result)
    {
        IsPending = result.Status == StepStatus.Pending;
        IsOk = result.Status == StepStatus.Ok;
        IsFailed = result.Status == StepStatus.Failed;
        IsSkipped = result.Status == StepStatus.Skipped;
        Detail = result.Status switch
        {
            StepStatus.Ok => UiText.Format("Health_Millis", "{0} ms", result.Millis),
            StepStatus.Failed => string.IsNullOrWhiteSpace(result.Message)
                ? UiText.Format("Health_Millis", "{0} ms", result.Millis)
                : UiText.Format("Health_MillisError", "{0} ms - {1}", result.Millis, result.Message),
            _ => string.Empty,
        };
        var status = result.Status switch
        {
            StepStatus.Ok => UiText.Get("Health_StatusOk", "OK"),
            StepStatus.Failed => UiText.Get("Health_StatusFailed", "Failed"),
            StepStatus.Skipped => UiText.Get("Health_StatusSkipped", "Skipped"),
            _ => UiText.Get("Health_StatusPending", "Running"),
        };
        AutomationName = $"{Label}: {status} {Detail}".Trim();
    }
}
