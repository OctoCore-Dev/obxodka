namespace obxodka.Core.Models;

public sealed record BypassRouteEntry(string DestinationIp, string Reason);

public sealed class SplitTunnelPolicy
{
    public bool Enabled { get; set; } = true;

    public bool BypassRemoteManagement { get; set; } = true;

    public List<string> CustomBypassIps { get; set; } = [];

    public List<BypassRouteEntry> ActiveBypassRoutes { get; } = [];

    public void RecordBypassRoute(string destinationIp, string reason)
    {
        if (string.IsNullOrWhiteSpace(destinationIp))
        {
            return;
        }

        ActiveBypassRoutes.Add(new BypassRouteEntry(destinationIp, reason));
    }

    public void ClearActiveRoutes() => ActiveBypassRoutes.Clear();
}

