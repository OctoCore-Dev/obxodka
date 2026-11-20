using System.Buffers.Binary;
using System.Collections.Concurrent;
using obxodka.Shared.Geo;

namespace obxodka.Platforms.Windows;

//ядро движка раздельного туннелирования для windows (wfp и l3/l4 routing)
[SupportedOSPlatform("windows10.0.19041.0")]
internal static partial class WindowsSplitTunnelEngine
{
    private static readonly ConcurrentDictionary<uint, Win32NetworkHelper.MibIpForwardRow> s_pinnedRoutes = new();
    private static readonly ConcurrentDictionary<uint, string> s_routeReasons = new();
    private static readonly ConcurrentDictionary<uint, (string ExeName, string FullPath, long TimestampTicks)> s_pidCache = new();
    private static readonly ConcurrentDictionary<uint, (bool IsBypassed, string ExeName, long TimestampTicks)> s_portCache = new();
    private static readonly ConcurrentDictionary<ulong, byte> s_checkedUdpFlows = new();
    private static readonly SemaphoreSlim s_reloadGate = new(1, 1);

    private static volatile uint s_physicalGatewayUint;
    private static volatile int s_physicalIfIndex;
    private static volatile uint s_baseMetric = 25;
    private static IPAddress? s_physicalLocalIp;
    private static volatile uint s_vpnServerIpUint;
    public static SplitTunnelPolicy Policy { get; } = new();

    public static int ActiveRouteCount => s_pinnedRoutes.Count;

    //передача актуального снимка правил сплит-туннеля в wfp anti-leak фильтр
    public static void SyncWfpLeakGuard()
    {
        var policy = Policy;
        if (!policy.Enabled || s_physicalGatewayUint == 0)
        {
            Win32WfpLeakGuard.SetSplitTunnelSnapshot(false, [], [], []);
            return;
        }

        var (paths, names) = policy.GetBypassedExecutablesSnapshot();
        var list = new List<(uint, uint)>(s_pinnedRoutes.Count);
        foreach (var row in s_pinnedRoutes.Values)
        {
            list.Add((row.DwForwardDest, row.DwForwardMask));
        }

        Win32WfpLeakGuard.SetSplitTunnelSnapshot(true, paths, names, list);
    }

    public static IReadOnlyList<(string DestinationIp, string Reason)> ActiveBypassRoutes
    {
        get
        {
            var keys = s_pinnedRoutes.Keys.ToArray();
            if (keys.Length == 0)
            {
                return [];
            }

            var list = new List<(string DestinationIp, string Reason)>(keys.Length);
            Span<byte> ipBytes = stackalloc byte[4];
            foreach (var ipUint in keys)
            {
                MemoryMarshal.Write(ipBytes, in ipUint);
                var ipStr = new IPAddress(ipBytes).ToString();
                var reason = s_routeReasons.TryGetValue(ipUint, out var r) && !string.IsNullOrWhiteSpace(r)
                    ? r
                    : ipStr;
                list.Add((ipStr, reason));
            }

            return list;
        }
    }


    public static event Action? OnRoutesUpdated;

    //асинхронное уведомление подписчиков об изменении списка активных маршрутов
    private static void NotifyRoutesUpdatedAsync() =>
        _ = ThreadPool.UnsafeQueueUserWorkItem(static _ => OnRoutesUpdated?.Invoke(), null);

    //конфигурация параметров физического сетевого адаптера и шлюза роутера
    public static void ConfigurePhysicalGateway(string gatewayIp, int physicalIfIndex, IPAddress? physicalLocalIp, string? vpnServerIp)
    {
        if (string.IsNullOrWhiteSpace(gatewayIp) || !IPAddress.TryParse(gatewayIp, out var gwAddr) ||
            gwAddr.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        var gwUint = MemoryMarshal.Read<uint>(gwAddr.GetAddressBytes());
        s_physicalGatewayUint = gwUint;
        s_physicalIfIndex = physicalIfIndex;
        s_physicalLocalIp = physicalLocalIp;

        if (!string.IsNullOrWhiteSpace(vpnServerIp) && IPAddress.TryParse(vpnServerIp, out var srvAddr) &&
            srvAddr.AddressFamily == AddressFamily.InterNetwork)
        {
            s_vpnServerIpUint = MemoryMarshal.Read<uint>(srvAddr.GetAddressBytes());
        }

        s_baseMetric = s_vpnServerIpUint != 0 && Win32NetworkHelper.GetBestRoute(s_vpnServerIpUint, 0, out var srvRow) == 0 && srvRow.DwForwardMetric1 > 0
            ? srvRow.DwForwardMetric1
            : Win32NetworkHelper.GetBestRoute(gwUint, 0, out var gwRow) == 0 && gwRow.DwForwardMetric1 > 0
                ? gwRow.DwForwardMetric1
                : 25;
    }

    //активация или перезагрузка правил раздельного туннелирования
    public static async Task ActivateOrReloadAsync()
    {
        if (!await s_reloadGate.WaitAsync(3000).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var policy = Policy;
            s_checkedUdpFlows.Clear();
            s_portCache.Clear();

            if (!policy.Enabled || s_physicalGatewayUint == 0)
            {
                CleanupAllRoutesInternal();
                SyncWfpLeakGuard();
                NotifyRoutesUpdatedAsync();
                return;
            }

            PruneStaleRoutes(policy);

            foreach (var rawIp in policy.CustomBypassIps.ToArray())
            {
                if (!IPAddress.TryParse(rawIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
                {
                    continue;
                }

                var ipUint = MemoryMarshal.Read<uint>(ip.GetAddressBytes());
                PinPhysicalHostRoute(ipUint, rawIp, false);
            }

            if (policy.HasBypassedExecutables)
            {
                PinActiveProcessEndpoints(false);
            }

            if (policy.HasDomainRules)
            {
                var seeds = policy.GetSeedDomainsToPreResolve();
                if (seeds.Count > 0)
                {
                    await PreResolveSeedDomainsOnPhysicalAsync(seeds).ConfigureAwait(false);
                }
            }

            if (policy.BypassAllRuTlds)
            {
                var supernets = RuSubnetRegistry.GetTier1Supernets(16);
                foreach (var (netHost, maskHost) in supernets)
                {
                    var netNet = BinaryPrimitives.ReverseEndianness(netHost);
                    var maskNet = BinaryPrimitives.ReverseEndianness(maskHost);
                    PinPhysicalSubnetRoute(netNet, maskNet, "Tier-1 RU Supernet");
                }
            }

            _ = Win32NetworkHelper.FlushDnsCache();
            SyncWfpLeakGuard();
            NotifyRoutesUpdatedAsync();
        }
        finally
        {
            _ = s_reloadGate.Release();
        }
    }

    //проверка является ли ipv4 адрес маршрутизируемым публичным адресом
    private static bool IsRoutablePublicIpv4(uint ipNetworkOrder)
    {
        var b0 = (byte)(ipNetworkOrder & 0xFF);
        var b1 = (byte)((ipNetworkOrder >> 8) & 0xFF);

        switch (b0)
        {
            case 0 or 127 or 10 or >= 224:
            case 172 when b1 is >= 16 and <= 31:
            case 192 when b1 == 168:
            case 100 when (b1 & 0xC0) == 64:
            case 169 when b1 == 254:
                return false;
        }

        return s_vpnServerIpUint == 0 || ipNetworkOrder != s_vpnServerIpUint;
    }
}
