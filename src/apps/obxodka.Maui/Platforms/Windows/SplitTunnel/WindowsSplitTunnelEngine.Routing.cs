using obxodka.Core.Models;

namespace obxodka.Platforms.Windows;

//управление системной таблицей маршрутизации windows (iphlpapi.dll)
internal static partial class WindowsSplitTunnelEngine
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_IPFORWARDROW
    {
        public uint DwForwardDest;
        public uint DwForwardMask;
        public uint DwForwardPolicy;
        public uint DwForwardNextHop;
        public uint DwForwardIfIndex;
        public uint DwForwardType;
        public uint DwForwardProto;
        public uint DwForwardAge;
        public uint DwForwardNextHopAS;
        public uint DwForwardMetric1;
        public uint DwForwardMetric2;
        public uint DwForwardMetric3;
        public uint DwForwardMetric4;
        public uint DwForwardMetric5;
    }

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DeleteIpForwardEntry(ref MIB_IPFORWARDROW pRoute);

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DnsFlushResolverCache();

    //удаление неактуальных маршрутов при смене политики исключений
    private static void PruneStaleRoutes(SplitTunnelPolicy policy)
    {
        var customIps = policy.CustomBypassIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedAny = false;
        Span<byte> ipBytes = stackalloc byte[4];

        foreach (var kvp in s_routeReasons.ToArray())
        {
            var ipUint = kvp.Key;
            var reason = kvp.Value;

            MemoryMarshal.Write(ipBytes, in ipUint);
            var ipStr = new IPAddress(ipBytes).ToString();

            var stillValid = customIps.Contains(ipStr) ||
                            customIps.Contains(reason) ||
                            (reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsProcessBypassed(reason, reason)) ||
                            (!reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsDomainBypassed(reason));

            if (!stillValid)
            {
                _ = s_routeReasons.TryRemove(ipUint, out _);
                if (s_pinnedRoutes.TryRemove(ipUint, out var row))
                {
                    _ = DeleteIpForwardEntry(ref row);
                }
                removedAny = true;
            }
        }

        if (removedAny)
        {
            Policy.ClearActiveRoutes();
            foreach (var kvp in s_routeReasons)
            {
                var ipUint = kvp.Key;
                MemoryMarshal.Write(ipBytes, in ipUint);
                var ipStr = new IPAddress(ipBytes).ToString();
                Policy.RecordBypassRoute(ipStr, kvp.Value);
            }
        }
    }

    //закрепление прямого физического маршрута хоста в обход wintun
    public static void PinPhysicalHostRoute(uint destIpNetworkOrder, string reason, bool notify = true)
    {
        if (s_physicalGatewayUint == 0 || !IsRoutablePublicIpv4(destIpNetworkOrder))
        {
            return;
        }

        s_routeReasons[destIpNetworkOrder] = reason;

        if (s_pinnedRoutes.ContainsKey(destIpNetworkOrder))
        {
            return;
        }

        var row = new MIB_IPFORWARDROW
        {
            DwForwardDest = destIpNetworkOrder,
            DwForwardMask = 0xFFFFFFFFu,
            DwForwardPolicy = 0,
            DwForwardNextHop = s_physicalGatewayUint,
            DwForwardIfIndex = (uint)Math.Max(s_physicalIfIndex, 0),
            DwForwardType = 4,
            DwForwardProto = 3,
            DwForwardAge = 0,
            DwForwardNextHopAS = 0,
            DwForwardMetric1 = Math.Max(s_baseMetric, 1u),
            DwForwardMetric2 = uint.MaxValue,
            DwForwardMetric3 = uint.MaxValue,
            DwForwardMetric4 = uint.MaxValue,
            DwForwardMetric5 = uint.MaxValue
        };

        uint[] candidateMetrics = [Math.Max(s_baseMetric, 1u), 25u, 35u, 50u, 100u];
        var status = -1;
        for (var i = 0; i < candidateMetrics.Length; i++)
        {
            row.DwForwardMetric1 = candidateMetrics[i];
            status = CreateIpForwardEntry(ref row);
            if (status == 5010)
            {
                status = SetIpForwardEntry(ref row);
            }
            if (status == 0)
            {
                break;
            }
        }

        s_pinnedRoutes[destIpNetworkOrder] = row;

        Span<byte> ipBytes = stackalloc byte[4];
        MemoryMarshal.Write(ipBytes, in destIpNetworkOrder);
        var ipStr = new IPAddress(ipBytes).ToString();
        Policy.RecordBypassRoute(ipStr, reason);
        if (notify)
        {
            NotifyRoutesUpdatedAsync();
        }

        //fallback через команду route add если прямой вызов win32 вернул ошибку
        if (status != 0)
        {
            Span<byte> gwBytes = stackalloc byte[4];
            var gwUint = s_physicalGatewayUint;
            MemoryMarshal.Write(gwBytes, in gwUint);
            var gwStr = new IPAddress(gwBytes).ToString();
            var ifIdx = s_physicalIfIndex;
            _ = Task.Run(() =>
            {
                try
                {
                    var ifArg = ifIdx > 0 ? $" if {ifIdx}" : "";
                    using var p = Process.Start(new ProcessStartInfo("route", $"add {ipStr} mask 255.255.255.255 {gwStr} metric 1{ifArg}")
                    {
                        CreateNoWindow = true,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        UseShellExecute = false
                    });
                    _ = p?.WaitForExit(1500);
                }
                catch { }
            });
        }
    }

    //очистка всех закрепленных маршрутов раздельного туннеля
    public static void CleanupAllRoutes()
    {
        CleanupAllRoutesInternal();
        NotifyRoutesUpdatedAsync();
    }

    //внутренняя очистка маршрутов и сброс состояния
    private static void CleanupAllRoutesInternal()
    {
        foreach (var kvp in s_pinnedRoutes)
        {
            var row = kvp.Value;
            _ = DeleteIpForwardEntry(ref row);
        }

        s_pinnedRoutes.Clear();
        s_routeReasons.Clear();
        s_checkedUdpFlows.Clear();
        s_portCache.Clear();
        Policy.ClearActiveRoutes();
    }
}
