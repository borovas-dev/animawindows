namespace AnimaWindows.Core.Models;

/// <summary>
/// State of the self-update check/download. Mirrors <c>UpdateState</c> in core/remoteconfig/UpdateChecker.kt; the Android
/// <c>apkUrl</c> becomes <see cref="Available.PackageUrl"/> (the MSIX/MSIXBUNDLE asset) and the downloaded file is a path.
/// </summary>
public abstract record UpdateState
{
    private UpdateState() { }

    public sealed record Idle : UpdateState;
    public sealed record Checking : UpdateState;
    public sealed record UpToDate : UpdateState;

    public sealed record Available(string Version, string Changelog, string PackageUrl, string PageUrl) : UpdateState;

    /// <param name="Progress">0..1, or -1 when the size is unknown.</param>
    public sealed record Downloading(Available Update, float Progress) : UpdateState;

    public sealed record ReadyToInstall(Available Update, string FilePath) : UpdateState;

    public sealed record Failed(string Message) : UpdateState;
}
