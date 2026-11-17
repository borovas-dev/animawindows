using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.Services;

/// <summary>Shows <see cref="ContentDialog"/>s one at a time (WinUI throws if two are open on one root).</summary>
public interface IDialogService
{
    /// <summary>Must be called on the UI thread.</summary>
    Task<ContentDialogResult> ShowAsync(ContentDialog dialog);
}

public sealed class DialogService : IDialogService
{
    private readonly ShellChrome _shell;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DialogService(ShellChrome shell) => _shell = shell;

    public async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await _gate.WaitAsync();
        try
        {
            dialog.XamlRoot ??= _shell.XamlRoot;
            if (_shell.Window.Content is FrameworkElement root)
            {
                dialog.RequestedTheme = root.RequestedTheme;
            }

            return await dialog.ShowAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
