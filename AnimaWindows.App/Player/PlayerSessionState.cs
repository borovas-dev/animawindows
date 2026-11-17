namespace AnimaWindows.App.Player;

/// <summary>Per-app-session player memory: the engine an Auto failover settled on stays in use until the app restarts.</summary>
public sealed class PlayerSessionState
{
    public EngineKind? PreferredEngine { get; set; }
}
