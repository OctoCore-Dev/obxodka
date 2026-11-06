using Android.Content;
using Android.Net;

namespace obxodka.Platforms.Android;

//проверка доступности сети и типа интернет-соединения через системный connectivitymanager android
[SupportedOSPlatform("android29.0")]
public sealed class AndroidConnectivityService(Context context) : IConnectivityService
{
    private readonly ConnectivityManager? _connectivityManager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;

    //текущий статус доступа в интернет с учетом vpn, wifi и сотовой сети
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
                : capabilities.HasCapability(NetCapability.Internet) ||
                    capabilities.HasTransport(TransportType.Vpn) ||
                    capabilities.HasTransport(TransportType.Wifi) ||
                    capabilities.HasTransport(TransportType.Cellular) ||
                    capabilities.HasTransport(TransportType.Ethernet)
                    ? AppNetworkAccess.Internet
                    : AppNetworkAccess.Local;
        }
    }

    //событие смены статуса сети
    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add { }
        remove { }
    }

    //получение профиля активного сетевого подключения (wifi, сотовая связь, ethernet)
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
