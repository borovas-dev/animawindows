using AnimaWindows.Core.Abstractions;
using Windows.Networking.Connectivity;

namespace AnimaWindows.App.Services;

/// <summary>Maps <see cref="NetworkInformation"/> connection cost to Offline / Unmetered / Metered.</summary>
public sealed class NetworkCostProvider : INetworkCostProvider
{
    private readonly object _gate = new();
    private NetworkCost _current;

    public NetworkCostProvider()
    {
        _current = Query();
        try
        {
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch (Exception)
        {
            // Network change notifications are best effort.
        }
    }

    public NetworkCost Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event EventHandler<NetworkCost>? Changed;

    private void OnNetworkStatusChanged(object sender)
    {
        var next = Query();
        lock (_gate)
        {
            if (next == _current)
            {
                return;
            }

            _current = next;
        }

        Changed?.Invoke(this, next);
    }

    private static NetworkCost Query()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
            {
                return NetworkCost.Offline;
            }

            var cost = profile.GetConnectionCost();
            if (cost.Roaming || cost.OverDataLimit || cost.ApproachingDataLimit)
            {
                return NetworkCost.Metered;
            }

            return cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable
                ? NetworkCost.Metered
                : NetworkCost.Unmetered;
        }
        catch (Exception)
        {
            return NetworkCost.Unmetered;
        }
    }
}
