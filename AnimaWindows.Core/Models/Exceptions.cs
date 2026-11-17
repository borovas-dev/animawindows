namespace AnimaWindows.Core.Models;

/// <summary>
/// A provider failed (network, parse, missing data, timeout). Mirrors <c>ProviderException</c> in provider/api/AnimeProvider.kt.
/// </summary>
public class ProviderException : Exception
{
    public ProviderException(string message, Exception? innerException = null) : base(message, innerException) { }
}

/// <summary>
/// No provider is available (chain empty after language/disabled/kill-switch filtering, or the anime's provider is unknown).
/// Kotlin throws a plain <c>ProviderException("No provider available ...")</c>; this subtype lets the UI distinguish it.
/// </summary>
public sealed class NoProviderAvailableException : ProviderException
{
    public NoProviderAvailableException(string message) : base(message) { }
}

/// <summary>A provider call exceeded the chain's per-provider timeout.</summary>
public sealed class ProviderTimeoutException : ProviderException
{
    public ProviderTimeoutException(string message) : base(message) { }
}

/// <summary>
/// Thrown when Cloudflare blocks a request; UI should launch the WebView solver (<c>ICloudflareSolver</c>) for
/// <see cref="Url"/>. Deliberately NOT a <see cref="ProviderException"/>, mirroring Kotlin where it derives from
/// <c>Exception</c>; the chain prefers rethrowing it over any other failure. Mirrors <c>CloudflareChallengeException</c> in
/// provider/api/AnimeProvider.kt.
/// </summary>
public sealed class CloudflareChallengeException : Exception
{
    public CloudflareChallengeException(string url) : base($"Cloudflare challenge at {url}") => Url = url;

    /// <summary>The URL that was challenged.</summary>
    public string Url { get; }
}
