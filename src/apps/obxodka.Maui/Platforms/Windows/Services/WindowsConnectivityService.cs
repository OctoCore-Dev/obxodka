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
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => NetworkConnectionType.Ethernet,
            NetworkInterfaceType.Ppp or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 => NetworkConnectionType.Cellular,
            NetworkInterfaceType.Unknown => throw new NotImplementedException(),
            NetworkInterfaceType.TokenRing => throw new NotImplementedException(),
            NetworkInterfaceType.Fddi => throw new NotImplementedException(),
            NetworkInterfaceType.BasicIsdn => throw new NotImplementedException(),
            NetworkInterfaceType.PrimaryIsdn => throw new NotImplementedException(),
            NetworkInterfaceType.Loopback => throw new NotImplementedException(),
            NetworkInterfaceType.Ethernet3Megabit => throw new NotImplementedException(),
            NetworkInterfaceType.Slip => throw new NotImplementedException(),
            NetworkInterfaceType.Atm => throw new NotImplementedException(),
            NetworkInterfaceType.GenericModem => throw new NotImplementedException(),
            NetworkInterfaceType.Isdn => throw new NotImplementedException(),
            NetworkInterfaceType.FastEthernetFx => throw new NotImplementedException(),
            NetworkInterfaceType.AsymmetricDsl => throw new NotImplementedException(),
            NetworkInterfaceType.RateAdaptDsl => throw new NotImplementedException(),
            NetworkInterfaceType.SymmetricDsl => throw new NotImplementedException(),
            NetworkInterfaceType.VeryHighSpeedDsl => throw new NotImplementedException(),
            NetworkInterfaceType.IPOverAtm => throw new NotImplementedException(),
            NetworkInterfaceType.Tunnel => throw new NotImplementedException(),
            NetworkInterfaceType.MultiRateSymmetricDsl => throw new NotImplementedException(),
            NetworkInterfaceType.HighPerformanceSerialBus => throw new NotImplementedException(),
            NetworkInterfaceType.Wman => throw new NotImplementedException(),
            _ => NetworkConnectionType.Other
        };
}
