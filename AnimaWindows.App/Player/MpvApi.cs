using System.Runtime.InteropServices;

namespace AnimaWindows.App.Player;

/// <summary>
/// Late-bound P/Invoke surface of <c>libmpv-2.dll</c> (client API). The library is loaded with <see cref="NativeLibrary"/> so a
/// missing DLL is a normal condition (<see cref="TryLoad"/> returns null) rather than a type-load failure.
/// </summary>
internal sealed class MpvApi
{
    // mpv_event_id values used by the engine.
    public const int EventNone = 0;
    public const int EventShutdown = 1;
    public const int EventEndFile = 7;
    public const int EventFileLoaded = 8;
    public const int EventClientMessage = 16;
    public const int EventSeek = 20;
    public const int EventPlaybackRestart = 21;
    public const int EventPropertyChange = 22;

    // mpv_format values.
    public const int FormatFlag = 3;
    public const int FormatDouble = 5;

    // mpv_end_file_reason.
    public const int EndFileError = 4;

    private static readonly Lazy<MpvApi?> Shared = new(Load);

    private readonly CreateFn _create;
    private readonly InitializeFn _initialize;
    private readonly SetStringFn _setOption;
    private readonly SetStringFn _setProperty;
    private readonly CommandFn _command;
    private readonly ObserveFn _observe;
    private readonly WaitEventFn _waitEvent;
    private readonly WakeupFn _wakeup;
    private readonly DestroyFn _destroy;
    private readonly ErrorStringFn _errorString;

    private MpvApi(
        CreateFn create, InitializeFn initialize, SetStringFn setOption, SetStringFn setProperty, CommandFn command,
        ObserveFn observe, WaitEventFn waitEvent, WakeupFn wakeup, DestroyFn destroy, ErrorStringFn errorString)
    {
        _create = create;
        _initialize = initialize;
        _setOption = setOption;
        _setProperty = setProperty;
        _command = command;
        _observe = observe;
        _waitEvent = waitEvent;
        _wakeup = wakeup;
        _destroy = destroy;
        _errorString = errorString;
    }

    /// <summary>The loaded library, or null when <c>libmpv-2.dll</c> is not present or incomplete.</summary>
    public static MpvApi? TryLoad() => Shared.Value;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint CreateFn();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitializeFn(nint ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SetStringFn(
        nint ctx, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CommandFn(nint ctx, nint args);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ObserveFn(nint ctx, ulong replyUserdata, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int format);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint WaitEventFn(nint ctx, double timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void WakeupFn(nint ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyFn(nint ctx);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint ErrorStringFn(int error);

    private static MpvApi? Load()
    {
        foreach (var candidate in Candidates())
        {
            nint handle;
            try
            {
                if (!NativeLibrary.TryLoad(candidate, out handle)) continue;
            }
            catch (Exception)
            {
                continue;
            }

            var api = Bind(handle);
            if (api is not null) return api;
            NativeLibrary.Free(handle);
        }

        return null;
    }

    private static IEnumerable<string> Candidates()
    {
        var baseDir = AppContext.BaseDirectory;
        var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
        foreach (var name in new[] { "libmpv-2.dll", "mpv-2.dll" })
        {
            yield return Path.Combine(baseDir, name);
            yield return Path.Combine(baseDir, "runtimes", arch, "native", name);
            yield return Path.Combine(baseDir, "mpv", name);
        }

        yield return "libmpv-2.dll";
    }

    private static MpvApi? Bind(nint handle)
    {
        try
        {
            return new MpvApi(
                Export<CreateFn>(handle, "mpv_create"),
                Export<InitializeFn>(handle, "mpv_initialize"),
                Export<SetStringFn>(handle, "mpv_set_option_string"),
                Export<SetStringFn>(handle, "mpv_set_property_string"),
                Export<CommandFn>(handle, "mpv_command"),
                Export<ObserveFn>(handle, "mpv_observe_property"),
                Export<WaitEventFn>(handle, "mpv_wait_event"),
                Export<WakeupFn>(handle, "mpv_wakeup"),
                Export<DestroyFn>(handle, "mpv_terminate_destroy"),
                Export<ErrorStringFn>(handle, "mpv_error_string"));
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static T Export<T>(nint handle, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(handle, name));

    public nint Create() => _create();

    public int Initialize(nint ctx) => _initialize(ctx);

    public int SetOption(nint ctx, string name, string value) => _setOption(ctx, name, value);

    public int SetProperty(nint ctx, string name, string value) => _setProperty(ctx, name, value);

    public int Observe(nint ctx, string name, int format) => _observe(ctx, 0, name, format);

    public nint WaitEvent(nint ctx, double timeoutSeconds) => _waitEvent(ctx, timeoutSeconds);

    public void Wakeup(nint ctx) => _wakeup(ctx);

    public void TerminateDestroy(nint ctx) => _destroy(ctx);

    public string ErrorString(int error) => Marshal.PtrToStringUTF8(_errorString(error)) ?? ("mpv error " + error);

    /// <summary>Runs <c>mpv_command</c> with a NULL-terminated UTF-8 argument vector.</summary>
    public int Command(nint ctx, params string[] args)
    {
        var strings = new nint[args.Length];
        var vector = Marshal.AllocHGlobal(nint.Size * (args.Length + 1));
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                strings[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
                Marshal.WriteIntPtr(vector, i * nint.Size, strings[i]);
            }

            Marshal.WriteIntPtr(vector, args.Length * nint.Size, nint.Zero);
            return _command(ctx, vector);
        }
        finally
        {
            foreach (var s in strings)
            {
                if (s != nint.Zero) Marshal.FreeCoTaskMem(s);
            }

            Marshal.FreeHGlobal(vector);
        }
    }

    // ---- mpv_event / payload readers (layouts are identical on x64 and ARM64) ----

    public static int EventId(nint ev) => Marshal.ReadInt32(ev, 0);

    public static nint EventData(nint ev) => Marshal.ReadIntPtr(ev, 16);

    public static int EventError(nint ev) => Marshal.ReadInt32(ev, 4);

    /// <summary>Reads an <c>mpv_event_property</c>: name, format and the data pointer (null when the property is unavailable).</summary>
    public static (string Name, int Format, nint Data) ReadProperty(nint data) =>
        (Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(data, 0)) ?? string.Empty, Marshal.ReadInt32(data, 8), Marshal.ReadIntPtr(data, 16));

    /// <summary>Reads an <c>mpv_event_end_file</c>: reason and error code.</summary>
    public static (int Reason, int Error) ReadEndFile(nint data) => (Marshal.ReadInt32(data, 0), Marshal.ReadInt32(data, 4));

    /// <summary>First argument of an <c>mpv_event_client_message</c>, or null.</summary>
    public static string? ReadFirstClientArg(nint data)
    {
        if (Marshal.ReadInt32(data, 0) < 1) return null;
        var args = Marshal.ReadIntPtr(data, 8);
        return args == nint.Zero ? null : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(args, 0));
    }
}
