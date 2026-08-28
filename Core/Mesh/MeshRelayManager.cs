namespace obxodka.Core.Mesh;

public static class MeshRelayManager
{
    public static MeshRelayServer? ActiveRelayServer { get; private set; }

#if WINDOWS
    public static async Task StartRelayIfEnabledAsync()
    {
        if (!OperatingSystem.IsWindows() || !MeshSettings.RelayEnabled)
        {
            return;
        }

        var profiles = Connectivity.Current.ConnectionProfiles;
        var hasValidNetwork = profiles.Contains(ConnectionProfile.WiFi) ||
                              profiles.Contains(ConnectionProfile.Ethernet) ||
                              Connectivity.Current.NetworkAccess == NetworkAccess.Internet;

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
#else
    public static Task StartRelayIfEnabledAsync() => Task.CompletedTask;

    public static Task StopRelayAsync() => Task.CompletedTask;
#endif
}
