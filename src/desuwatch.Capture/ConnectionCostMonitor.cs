using Windows.Networking.Connectivity;

namespace Desuwatch.Capture;

/// <summary>
/// Watches the current internet connection's cost profile and raises
/// <see cref="CostChanged"/> when the metered/unrestricted state or
/// over-limit flags change. Backed by WinRT's NetworkInformation APIs.
/// </summary>
public sealed class ConnectionCostMonitor : IDisposable
{
    private ConnectionCostInfo _current = ConnectionCostInfo.Unknown;
    private bool _subscribed;

    /// <summary>
    /// Raised when the connection cost changes. Fires on a threadpool thread;
    /// marshal to the UI thread in handlers.
    /// </summary>
    public event EventHandler<ConnectionCostInfo>? CostChanged;

    public ConnectionCostInfo Current => _current;

    public void Start()
    {
        if (_subscribed) return;

        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        _subscribed = true;

        // Prime the cached value so consumers can render the banner
        // without waiting for the first change event.
        Refresh();
    }

    private void OnNetworkStatusChanged(object sender) => Refresh();

    private void Refresh()
    {
        var next = Read();
        if (next == _current) return;

        _current = next;
        CostChanged?.Invoke(this, next);
    }

    private static ConnectionCostInfo Read()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return ConnectionCostInfo.Unknown;

            var cost = profile.GetConnectionCost();
            if (cost is null) return ConnectionCostInfo.Unknown;

            var level = cost.NetworkCostType switch
            {
                NetworkCostType.Unrestricted => CostLevel.Unrestricted,
                NetworkCostType.Fixed        => CostLevel.Fixed,
                NetworkCostType.Variable     => CostLevel.Variable,
                _                            => CostLevel.Unknown
            };

            return new ConnectionCostInfo(
                Level: level,
                IsRoaming: cost.Roaming,
                IsOverDataLimit: cost.OverDataLimit,
                ApproachingDataLimit: cost.ApproachingDataLimit,
                ProfileName: profile.ProfileName);
        }
        catch
        {
            // No internet connection at all, or WinRT projection unavailable.
            return ConnectionCostInfo.Unknown;
        }
    }

    public void Dispose()
    {
        if (!_subscribed) return;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
        _subscribed = false;
    }
}