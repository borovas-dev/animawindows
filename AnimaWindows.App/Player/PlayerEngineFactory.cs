using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.App.Player;

/// <summary>Creates playback engines. <see cref="Create"/> must run on the UI thread (engines own XAML elements).</summary>
public interface IPlayerEngineFactory
{
    /// <summary>True when libmpv-2.dll is present and usable.</summary>
    bool IsMpvAvailable { get; }

    IPlaybackEngine Create(EngineKind kind);
}

public sealed class PlayerEngineFactory : IPlayerEngineFactory
{
    private readonly LocalStreamProxy _proxy;
    private readonly IShellChrome _chrome;

    public PlayerEngineFactory(LocalStreamProxy proxy, IShellChrome chrome)
    {
        _proxy = proxy;
        _chrome = chrome;
    }

    public bool IsMpvAvailable => MpvEngine.IsAvailable;

    public IPlaybackEngine Create(EngineKind kind) => kind switch
    {
        EngineKind.Mpv => new MpvEngine(() => _chrome.WindowHandle),
        _ => new NativeEngine(_proxy),
    };
}
