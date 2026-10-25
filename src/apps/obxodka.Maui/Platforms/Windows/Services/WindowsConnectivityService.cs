namespace obxodka.Maui.Platforms.Windows.Services;

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

                var profiles = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up)
                    .Select(n => new AppConnectionProfile(
                        MapNetworkType(n.NetworkInterfaceType),
                        AppNetworkAccess.Internet
                    ));

                return profiles.Any() ? AppNetworkAccess.Internet : AppNetworkAccess.Local;
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

    public Task<AppConnectionProfile> GetConnectionProfileAsync()
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToList();

        if (interfaces.Count == 0)
        {
            return Task.FromResult(new AppConnectionProfile(NetworkConnectionType.Unknown, AppNetworkAccess.None));
        }

        var primary = interfaces.First();
        var connectionType = MapNetworkType(primary.NetworkInterfaceType);

        return Task.FromResult(new AppConnectionProfile(connectionType, AppNetworkAccess.Internet));
    }

    private static NetworkConnectionType MapNetworkType(NetworkInterfaceType type) =>
        type switch
        {
            NetworkInterfaceType.Wireless80211 => NetworkConnectionType.Wifi,
            NetworkInterfaceType.Ethernet => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.GigabitEthernet => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.FastEthernetT => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.FastEthernetFx => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.Ethernet3Megabit => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.Ppp => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Wwanpp => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Wwanpp2 => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Unknown => NetworkConnectionType.Unknown,
            NetworkInterfaceType.TokenRing => NetworkConnectionType.Other,
            NetworkInterfaceType.Fddi => NetworkConnectionType.Other,
            NetworkInterfaceType.BasicIsdn => NetworkConnectionType.Other,
            NetworkInterfaceType.PrimaryIsdn => NetworkConnectionType.Other,
            NetworkInterfaceType.Loopback => NetworkConnectionType.Other,
            NetworkInterfaceType.Slip => NetworkConnectionType.Other,
            NetworkInterfaceType.Atm => NetworkConnectionType.Other,
            NetworkInterfaceType.GenericModem => NetworkConnectionType.Other,
            NetworkInterfaceType.Isdn => NetworkConnectionType.Other,
            NetworkInterfaceType.AsymmetricDsl => NetworkConnectionType.Other,
            NetworkInterfaceType.RateAdaptDsl => NetworkConnectionType.Other,
            NetworkInterfaceType.SymmetricDsl => NetworkConnectionType.Other,
            NetworkInterfaceType.VeryHighSpeedDsl => NetworkConnectionType.Other,
            NetworkInterfaceType.IPOverAtm => NetworkConnectionType.Other,
            NetworkInterfaceType.Tunnel => NetworkConnectionType.Other,
            NetworkInterfaceType.MultiRateSymmetricDsl => NetworkConnectionType.Other,
            NetworkInterfaceType.HighPerformanceSerialBus => NetworkConnectionType.Other,
            NetworkInterfaceType.Wman => NetworkConnectionType.Other,
            _ => NetworkConnectionType.Other
        };
}
