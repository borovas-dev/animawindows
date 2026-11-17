using System.Runtime.InteropServices;

namespace AnimaWindows.App.Services.Notifications;

/// <summary>Taskbar button progress state (<c>TBPFLAG</c>).</summary>
internal enum TaskbarProgressState
{
    NoProgress = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

[ComImport]
[Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class TaskbarListCoClass
{
}

/// <summary>
/// <c>ITaskbarList3</c> up to <c>SetProgressState</c> (vtable order: ITaskbarList, ITaskbarList2, ITaskbarList3). Later slots are
/// not declared because they are never called.
/// </summary>
[ComImport]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ITaskbarList3
{
    [PreserveSig]
    int HrInit();

    [PreserveSig]
    int AddTab(nint hwnd);

    [PreserveSig]
    int DeleteTab(nint hwnd);

    [PreserveSig]
    int ActivateTab(nint hwnd);

    [PreserveSig]
    int SetActiveAlt(nint hwnd);

    [PreserveSig]
    int MarkFullscreenWindow(nint hwnd, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);

    [PreserveSig]
    int SetProgressValue(nint hwnd, ulong completed, ulong total);

    [PreserveSig]
    int SetProgressState(nint hwnd, TaskbarProgressState state);
}

/// <summary>Thin wrapper over <see cref="ITaskbarList3"/>. Must be used from the UI (STA) thread; all failures are swallowed.</summary>
internal sealed class TaskbarProgress
{
    private ITaskbarList3? _list;
    private bool _failed;

    public void Set(nint hwnd, TaskbarProgressState state, ulong completed = 0, ulong total = 0)
    {
        if (hwnd == 0 || _failed)
        {
            return;
        }

        try
        {
            if (_list is null)
            {
                var list = (ITaskbarList3)(object)new TaskbarListCoClass();
                list.HrInit();
                _list = list;
            }

            if (state == TaskbarProgressState.Normal)
            {
                _list.SetProgressValue(hwnd, completed, total);
            }

            _list.SetProgressState(hwnd, state);
        }
        catch (Exception)
        {
            _failed = true;
        }
    }
}
