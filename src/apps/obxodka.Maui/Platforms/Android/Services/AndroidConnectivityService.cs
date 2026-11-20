using Android.Content;
using Android.Net;

namespace obxodka.Services;

//проверка доступности сети и типа интернет-соединения через системный connectivity manager android
[SupportedOSPlatform("android30.0")]
[SuppressMessage("Usage", "CA1001:Types that own disposable fields should be disposable",
    Justification = "Process-lifetime singleton service")]
public sealed class AndroidConnectivityService : IConnectivityService
{
    private readonly ConnectivityManager? _connectivityManager;

    //держим ссылку на callback в поле синглтона, чтобы gc не собрал обёртку
    // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable
    private readonly NetworkChangeCallback? _networkCallback;
    private EventHandler<AppConnectivityChangedEventArgs>? _connectivityChanged;

    public AndroidConnectivityService(Context context)
    {
        _connectivityManager = context.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
        try
        {
            _networkCallback = new NetworkChangeCallback(this);
            _connectivityManager?.RegisterDefaultNetworkCallback(_networkCallback);
        }
        catch
        {
            // ignored
        }
    }

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
                : capabilities.HasCapability(NetCapability.NotVpn) &&
                  (capabilities.HasCapability(NetCapability.Internet) ||
                   capabilities.HasTransport(TransportType.Wifi) ||
                   capabilities.HasTransport(TransportType.Cellular) ||
                   capabilities.HasTransport(TransportType.Ethernet))
                    ? AppNetworkAccess.Internet
                    : AppNetworkAccess.Local;
        }
    }

    //событие смены статуса сети
    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add => _connectivityChanged += value;
        remove => _connectivityChanged -= value;
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


    private void RaiseConnectivityChanged() =>
        _connectivityChanged?.Invoke(this, new AppConnectivityChangedEventArgs(NetworkAccess, []));

    private sealed class NetworkChangeCallback(AndroidConnectivityService owner) : ConnectivityManager.NetworkCallback
    {
        public override void OnAvailable(Network network) => owner.RaiseConnectivityChanged();
        public override void OnLost(Network network) => owner.RaiseConnectivityChanged();

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities networkCapabilities) =>
            owner.RaiseConnectivityChanged();
    }
}
