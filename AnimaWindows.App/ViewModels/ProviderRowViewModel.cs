using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.ViewModels;

/// <summary>One provider in Settings › Advanced (enable/disable and reorder).</summary>
public sealed partial class ProviderRowViewModel : ObservableObject
{
    private readonly Action<ProviderRowViewModel> _onToggled;
    private bool _initialising = true;

    public ProviderRowViewModel(string id, string name, bool isEnabled, bool canMoveUp, bool canMoveDown, Action<ProviderRowViewModel> onToggled)
    {
        Id = id;
        Name = name;
        IsEnabled = isEnabled;
        CanMoveUp = canMoveUp;
        CanMoveDown = canMoveDown;
        _onToggled = onToggled;
        _initialising = false;
    }

    public string Id { get; }

    public string Name { get; }

    public bool CanMoveUp { get; }

    public bool CanMoveDown { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_initialising)
        {
            _onToggled(this);
        }
    }
}
