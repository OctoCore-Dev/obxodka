namespace obxodka.Platforms.Windows;
//мониторинг состояния сетевого подключения windows и определение типа канала связи
public sealed class WindowsConnectivityService : IConnectivityService
{
    private EventHandler<AppConnectivityChangedEventArgs>? _connectivityChanged;

    public WindowsConnectivityService()
    {
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => RaiseConnectivityChanged();
    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => RaiseConnectivityChanged();

    private void RaiseConnectivityChanged() =>
        _connectivityChanged?.Invoke(this, new AppConnectivityChangedEventArgs(NetworkAccess, []));

    //проверка наличия активного подключения к интернету исключая петлевые интерфейсы
    public AppNetworkAccess NetworkAccess
    {
        get
        {
            try
            {
                if (!NetworkInterface.GetIsNetworkAvailable())
                {
                    return AppNetworkAccess.None;
                }

                var hasInternetUplink = NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                    !n.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                    !n.Description.Contains("obxodka", StringComparison.OrdinalIgnoreCase));

                return hasInternetUplink ? AppNetworkAccess.Internet : AppNetworkAccess.Local;
            }
            catch
            {
                return AppNetworkAccess.Unknown;
            }
        }
    }

    public event EventHandler<AppConnectivityChangedEventArgs>? ConnectivityChanged
    {
        add => _connectivityChanged += value;
        remove => _connectivityChanged -= value;
    }

    //определение профиля активного физического адаптера ethernet wifi сотовая связь
    public Task<AppConnectionProfile> GetConnectionProfileAsync()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Tunnel &&
                            !n.Description.Contains("wintun", StringComparison.OrdinalIgnoreCase) &&
                            !n.Description.Contains("obxodka", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (interfaces.Count == 0)
            {
                return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.None));
            }

            var primary = interfaces[0];
            var connectionType = MapNetworkType(primary.NetworkInterfaceType);

            return Task.FromResult(new AppConnectionProfile(connectionType, AppNetworkAccess.Internet));
        }
        catch
        {
            return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.Unknown));
        }
    }

    private static NetworkConnectionType MapNetworkType(NetworkInterfaceType type)
    {
#pragma warning disable IDE0072 // Add missing cases to switch expression
        return type switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkConnectionType.Wifi,
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.Ethernet3Megabit => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.Ppp or
            NetworkInterfaceType.Wwanpp or
            NetworkInterfaceType.Wwanpp2 => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Unknown => NetworkConnectionType.Unknown,
            _ => NetworkConnectionType.Other
        };
#pragma warning restore IDE0072
    }
}
