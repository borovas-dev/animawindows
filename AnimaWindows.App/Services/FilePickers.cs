using Windows.Storage.Pickers;
using WinRT.Interop;

namespace AnimaWindows.App.Services;

/// <summary>File and folder pickers bound to the main window (needed in desktop apps).</summary>
public sealed class FilePickers
{
    private readonly IShellChrome _shell;

    public FilePickers(IShellChrome shell) => _shell = shell;

    /// <summary>Returns the chosen folder path, or null when cancelled.</summary>
    public async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _shell.WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Returns the chosen file path, or null when cancelled. Empty <paramref name="extensions"/> allows any file.</summary>
    public async Task<string?> PickOpenFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        if (extensions.Length == 0)
        {
            picker.FileTypeFilter.Add("*");
        }
        else
        {
            foreach (var extension in extensions)
            {
                picker.FileTypeFilter.Add(extension);
            }
        }

        InitializeWithWindow.Initialize(picker, _shell.WindowHandle);
        var file = await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>Returns the chosen (already created, empty) file path, or null when cancelled.</summary>
    public async Task<string?> PickSaveFileAsync(string suggestedName, string typeDescription, string extension)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(typeDescription, new List<string> { extension });
        InitializeWithWindow.Initialize(picker, _shell.WindowHandle);
        var file = await picker.PickSaveFileAsync();
        return file?.Path;
    }
}
