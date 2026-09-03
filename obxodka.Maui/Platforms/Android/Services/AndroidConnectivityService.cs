using Android.Content;
using Android.Net;

namespace obxodka.Maui.Platforms.Android.Services;

[SupportedOSPlatform("android29.0")]
public sealed class AndroidConnectivityService(Context context) : IConnectivityService
{
    private readonly ConnectivityManager? _connectivityManager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

    public AppNetworkAccess NetworkAccess
    {
        get
        {
            if (_connectivityManager is null)
            {
                return AppNetworkAccess.Unknown;
            }

            var network = _connectivityManager.ActiveNetwork;
            if (network is null)
            {
                return AppNetworkAccess.None;
            }

            var capabilities = _connectivityManager.GetNetworkCapabilities(network);
            return capabilities is null
                ? AppNetworkAccess.Unknown
                : capabilities.HasTransport(TransportType.Wifi) ||
                  capabilities.HasTransport(TransportType.Cellular) ||
                  capabilities.HasTransport(TransportType.Ethernet)
                ? AppNetworkAccess.Internet
                : AppNetworkAccess.Local;
        }
    }

    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add { }
        remove { }
    }

    public Task<AppConnectionProfile> GetConnectionProfileAsync()
    {
        if (_connectivityManager is null)
        {
            return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.Unknown));
        }

        var network = _connectivityManager.ActiveNetwork;
        if (network is null)
        {
            return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.None));
        }

        var capabilities = _connectivityManager.GetNetworkCapabilities(network);
        if (capabilities is null)
        {
            return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.None));
        }

        var connType = capabilities.HasTransport(TransportType.Wifi)
            ? NetworkConnectionType.Wifi
            : capabilities.HasTransport(TransportType.Cellular)
            ? NetworkConnectionType.Cellular
            : capabilities.HasTransport(TransportType.Ethernet)
            ? NetworkConnectionType.Ethernet
            : NetworkConnectionType.Unknown;

        return Task.FromResult(new AppConnectionProfile(connType, AppNetworkAccess.Internet));
    }
}
