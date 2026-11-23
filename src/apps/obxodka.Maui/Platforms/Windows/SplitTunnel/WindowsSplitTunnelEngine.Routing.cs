namespace obxodka.Platforms.Windows;

//управление системной таблицей маршрутизации windows через общий win32networkhelper
internal static partial class WindowsSplitTunnelEngine
{
    //удаление неактуальных маршрутов при смене политики исключений
    private static void PruneStaleRoutes(SplitTunnelPolicy policy)
    {
        var customIps = policy.CustomBypassIps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedAny = false;
        Span<byte> ipBytes = stackalloc byte[4];

        foreach (var (ipUint, reason) in s_routeReasons.ToArray())
        {
            MemoryMarshal.Write(ipBytes, in ipUint);
            var ipStr = new IPAddress(ipBytes).ToString();

            var stillValid = customIps.Contains(ipStr) ||
                             customIps.Contains(reason) ||
                             policy.BypassAllRuTlds && (reason.StartsWith("Auto-Bypass RU", StringComparison.Ordinal) ||
                                                        reason == "Tier-1 RU Supernet") ||
                             policy.BypassRemoteManagement && reason == "Сессия удалённого доступа" ||
                             reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsProcessBypassed(reason, reason) ||
                             !reason.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && policy.IsDomainBypassed(reason);

            if (!stillValid)
            {
                _ = s_routeReasons.TryRemove(ipUint, out _);
                if (s_pinnedRoutes.TryRemove(ipUint, out var row))
                {
                    _ = Win32NetworkHelper.DeleteIpForwardEntry(ref row);
                }

                removedAny = true;
            }
        }

        if (!removedAny)
        {
            return;
        }

        Policy.ClearActiveRoutes();
        foreach (var (ipUint, value) in s_routeReasons)
        {
            MemoryMarshal.Write(ipBytes, in ipUint);
            var ipStr = new IPAddress(ipBytes).ToString();
            Policy.RecordBypassRoute(ipStr, value);
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

        ReadOnlySpan<uint> candidateMetrics = [Math.Max(s_baseMetric, 1u), 25u, 35u, 50u, 100u];
        var status = Win32NetworkHelper.AddIpv4RouteWithMetricFallback(
            destIpNetworkOrder,
            0xFFFFFFFFu,
            s_physicalGatewayUint,
            (uint)Math.Max(s_physicalIfIndex, 0),
            candidateMetrics,
            out var row);

        if (status != 0)
        {
            _ = s_routeReasons.TryRemove(destIpNetworkOrder, out _);
            return;
        }

        s_pinnedRoutes[destIpNetworkOrder] = row;
        Win32WfpLeakGuard.AllowBypassIpv4Route(destIpNetworkOrder, 0xFFFFFFFFu);

        Span<byte> ipBytes = stackalloc byte[4];
        MemoryMarshal.Write(ipBytes, in destIpNetworkOrder);
        var ipStr = new IPAddress(ipBytes).ToString();
        Policy.RecordBypassRoute(ipStr, reason);
        if (notify)
        {
            NotifyRoutesUpdatedAsync();
        }
    }

    //закрепление агрегированного диапазона подсети в физический адаптер
    public static void PinPhysicalSubnetRoute(uint netNetworkOrder, uint maskNetworkOrder, string reason)
    {
        if (s_physicalGatewayUint == 0 || s_pinnedRoutes.ContainsKey(netNetworkOrder))
        {
            return;
        }

        var status = Win32NetworkHelper.AddIpv4Route(
            netNetworkOrder,
            maskNetworkOrder,
            s_physicalGatewayUint,
            (uint)Math.Max(s_physicalIfIndex, 0),
            Math.Max(s_baseMetric, 1u),
            out var row);

        if (status != 0)
        {
            return;
        }

        s_pinnedRoutes[netNetworkOrder] = row;
        s_routeReasons[netNetworkOrder] = reason;
        Win32WfpLeakGuard.AllowBypassIpv4Route(netNetworkOrder, maskNetworkOrder);
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
            _ = Win32NetworkHelper.DeleteIpForwardEntry(ref row);
        }

        s_pinnedRoutes.Clear();
        s_routeReasons.Clear();
        s_checkedUdpFlows.Clear();
        s_portCache.Clear();
        Policy.ClearActiveRoutes();
    }
}
