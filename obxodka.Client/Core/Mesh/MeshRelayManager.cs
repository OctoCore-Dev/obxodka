namespace obxodka.Core.Mesh;

public static class MeshRelayManager
{
    public static MeshRelayServer? ActiveRelayServer { get; private set; }

    public static async Task StartRelayIfEnabledAsync()
    {
        if (!OperatingSystem.IsWindows() || !MeshSettings.RelayEnabled)
        {
            return;
        }

        var profile = await Connectivity.Current.GetConnectionProfileAsync().ConfigureAwait(false);
        var hasValidNetwork = profile?.ConnectionType is NetworkConnectionType.Wifi or NetworkConnectionType.Ethernet ||
                              Connectivity.Current.NetworkAccess == AppNetworkAccess.Internet;

        if (!hasValidNetwork)
        {
            return;
        }
        try
        {
            if (ActiveRelayServer is null || !ActiveRelayServer.IsRunning)
            {
                ActiveRelayServer = new MeshRelayServer(MeshSettings.RelaySpeedMbps);
                await ActiveRelayServer.StartAsync().ConfigureAwait(false);
                Debug.WriteLine("[MESH RELAY] Relay server started on Windows.");
            }
        }
        finally
        {
        }
    }

    public static async Task StopRelayAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            if (ActiveRelayServer is not null)
            {
                await ActiveRelayServer.DisposeAsync().ConfigureAwait(false);
                ActiveRelayServer = null;
                Debug.WriteLine("[MESH RELAY] Relay server stopped and disposed.");
            }
        }
        finally
        {
        }
    }
}
