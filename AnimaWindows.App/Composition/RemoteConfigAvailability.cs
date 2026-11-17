using AnimaWindows.Core.Abstractions;

namespace AnimaWindows.App.Composition;

/// <summary>Remote kill switch: a provider is available unless the remote config disables it.</summary>
public sealed class RemoteConfigAvailability : IProviderAvailability
{
    private readonly IProviderConfig _config;

    public RemoteConfigAvailability(IProviderConfig config) => _config = config;

    public bool IsEnabled(string id) => _config.ForProvider(id).Enabled;
}
