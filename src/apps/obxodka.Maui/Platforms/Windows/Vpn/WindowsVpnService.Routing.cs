using System.Collections.Concurrent;
using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//управление системными маршрутами windows и привязкой сетевых сокетов к физическому шлюзу
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    private static string s_savedPhysicalGateway = "";
    private static int s_savedPhysicalIfIndex;
    private static IPAddress? s_savedPhysicalLocalIp;
    private static readonly ConcurrentDictionary<string, byte> s_remoteMgmtBypassIps = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim s_adapterConfigLock = new(1, 1);
    private static volatile bool s_ipv6RoutesApplied;

    //проверка реального изменения активного физического адаптера, его локального ip или шлюза
    private static bool HasPhysicalNetworkSnapshotChanged()
    {
        var savedIf = s_savedPhysicalIfIndex;
        var savedGw = s_savedPhysicalGateway;
        var savedLocal = s_savedPhysicalLocalIp;
        if (savedIf <= 0 || string.IsNullOrWhiteSpace(savedGw))
        {
            return false;
        }

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    IsConflictingAdapter(nic) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetIpv4IfIndex(nic) != savedIf)
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                var hasMatchingGw = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork &&
                    string.Equals(g.Address.ToString(), savedGw, StringComparison.Ordinal));
                if (!hasMatchingGw)
                {
                    return true;
                }

                if (savedLocal is null)
                {
                    return false;
                }

                var hasMatchingIp = props.UnicastAddresses.Any(u => u.Address.Equals(savedLocal));
                return !hasMatchingIp;
            }
        }
        catch
        {
            return false;
        }

        return true;
    }

    //сброс кэшированных параметров физического интерфейса при обрыве или смене сети
    private static void ResetSavedPhysicalGateway()
    {
        s_savedPhysicalGateway = "";
        s_savedPhysicalIfIndex = 0;
        s_savedPhysicalLocalIp = null;
    }

    //проверка наличия активного физического ipv6 шлюза или глобальных ipv6 dns на хосте
    private static bool HasActiveIpv6Connectivity()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    IsConflictingAdapter(nic) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var props = nic.GetIPProperties();
                if (props.GatewayAddresses.Any(g =>
                        g.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(g.Address) &&
                        !g.Address.Equals(IPAddress.IPv6Any)))
                {
                    return true;
                }

                if (props.DnsAddresses.Any(a =>
                        a.AddressFamily == AddressFamily.InterNetworkV6 &&
                        !IPAddress.IsLoopback(a) &&
                        a is { IsIPv6LinkLocal: false, IsIPv6SiteLocal: false }))
                {
                    return true;
                }
            }
        }
        catch
        {
            // ignored
        }

        return false;
    }

    //очистка устаревших маршрутов и восстановление исходного состояния сети
    private static async Task CleanupStaleRoutesAsync()
    {
        WindowsSplitTunnelEngine.CleanupAllRoutes();

        List<(string Dest, string Mask)> ipv4Targets =
        [
            ("0.0.0.0", "128.0.0.0"),
            ("128.0.0.0", "128.0.0.0"),
            ("10.0.0.0", "255.0.0.0"),
            ("172.16.0.0", "255.240.0.0"),
            ("192.168.0.0", "255.255.0.0"),
            ("169.254.0.0", "255.255.0.0"),
            .. AppConfig.DirectRelayIps.Select(relayIp => (relayIp, "255.255.255.255"))
        ];

        foreach (var mgmtIp in s_remoteMgmtBypassIps.Keys.ToArray())
        {
            ipv4Targets.Add((mgmtIp, "255.255.255.255"));
            _ = s_remoteMgmtBypassIps.TryRemove(mgmtIp, out _);
        }

        Win32NetworkHelper.DeleteIpv4Routes(ipv4Targets);

        var tunIfIndex = GetWintunInterfaceIndex("Obxodka");
        if (tunIfIndex > 0)
        {
            Win32NetworkHelper.DeleteIpv6Routes((uint)tunIfIndex, ("::", 1), ("8000::", 1), ("2001::", 32));
        }

        await DisableDnsLeakProtectionAsync();
        await RestoreAdapterDnsBackupAsync();
        ResetSavedPhysicalGateway();

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldRecoveryFile = Path.Combine(appData, "Obxodka", "vpn_recovery.json");
            if (File.Exists(oldRecoveryFile))
            {
                File.Delete(oldRecoveryFile);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static (ulong Luid, uint IfIndex) ResolveAdapterIdentity(WintunAdapter? adapter)
    {
        if (adapter is { InterfaceLuid: not 0 })
        {
            var idx = adapter.InterfaceIndex;
            if (idx == 0 && Win32NetworkHelper.TryGetInterfaceIndex(adapter.InterfaceLuid, out var resolvedIdx))
            {
                idx = resolvedIdx;
            }

            return (adapter.InterfaceLuid, idx);
        }

        var fallbackIdx = GetWintunInterfaceIndex(adapter?.Name ?? "Obxodka");
        return fallbackIdx > 0 && Win32NetworkHelper.TryGetInterfaceLuid((uint)fallbackIdx, out var luid)
            ? (luid, (uint)fallbackIdx)
            : (0, 0);
    }

    //получение системного индекса интерфейса wintun
    private static int GetWintunInterfaceIndex(string adapterName)
    {
        try
        {
            var card = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                n.Name.Contains(adapterName, StringComparison.OrdinalIgnoreCase));

            return card is not null ? TryGetIpv4IfIndex(card) : -1;
        }
        catch
        {
            return -1;
        }
    }

    //нативная настройка сетевого адреса, mtu, метрики и dns для адаптера wintun через iphlpapi
    private static async Task SetAdapterConfigAsync(WintunAdapter adapter, string ip, byte prefixLength, string? ipv6 = null)
    {
        await s_adapterConfigLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var (luid, _) = ResolveAdapterIdentity(adapter);
            if (luid == 0)
            {
                AppLogger.LogWarning($"[NET CONFIG] Failed to resolve Wintun LUID for '{adapter.Name}'.");
                return;
            }

            uint ipStatus = 0;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                ipStatus = Win32NetworkHelper.SetUnicastIpAddress(luid, ip, prefixLength);
                if (ipStatus != 1168)
                {
                    break;
                }

                await Task.Delay(50).ConfigureAwait(false);
            }

            var effectiveMtu = (uint)Math.Clamp(SmartMtuEngine.CurrentMtu, SmartMtuEngine.MinMtu, 1500);
            var v4IfStatus = Win32NetworkHelper.ConfigureIpInterface(luid, Win32NetworkHelper.AfInet, effectiveMtu, 50);
            var v4DnsStatus = Win32NetworkHelper.TryGetInterfaceGuid(luid, out var ifGuid)
                ? Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, false, $"{DnsDefaults.PrimaryDns},{DnsDefaults.SecondaryDns}")
                : 87u;
            _ = Win32NetworkHelper.FlushDnsCache();

            AppLogger.Log(
                $"[NET CONFIG] IPv4 Win32 set: ipStatus={ipStatus}, ifStatus={v4IfStatus}, dnsStatus={v4DnsStatus}, wintunMtu={effectiveMtu} (tunnelMtu={SmartMtuEngine.CurrentMtu})");

            var blockIpv6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
            var hasServerIpv6 = !string.IsNullOrWhiteSpace(ipv6) &&
                                IPAddress.TryParse(ipv6, out var parsedV6) &&
                                parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
            var effectiveIpv6 = hasServerIpv6
                ? blockIpv6 ? "fd00::2" : ipv6
                : blockIpv6 || HasActiveIpv6Connectivity()
                    ? "fd00::2"
                    : null;
            if (string.IsNullOrWhiteSpace(effectiveIpv6))
            {
                return;
            }

            var v6IpStatus = Win32NetworkHelper.SetUnicastIpAddress(luid, effectiveIpv6, 64);
            var v6IfStatus = Win32NetworkHelper.ConfigureIpInterface(luid, Win32NetworkHelper.AfInet6, effectiveMtu, 50);
            var v6DnsStatus = !blockIpv6 && ifGuid != Guid.Empty
                ? Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, true, $"{DnsDefaults.PrimaryDnsV6},{DnsDefaults.SecondaryDnsV6}")
                : 0u;

            _ = Win32NetworkHelper.FlushDnsCache();
            AppLogger.Log(
                $"[NET CONFIG] IPv6 Win32 set ({(hasServerIpv6 && !blockIpv6 ? "tunnel" : "leak-guard")}): ipStatus={v6IpStatus}, ifStatus={v6IfStatus}, dnsStatus={v6DnsStatus}");
        }
        finally
        {
            _ = s_adapterConfigLock.Release();
        }
    }

    //обновление mtu адаптера под текущий потолок ядра octopus напрямую через iphlpapi
    private static async Task UpdateAdapterMtuAsync(WintunAdapter adapter, int mtu)
    {
        using var suppressScope = SuppressNetworkChanges(2000);
        var lockAcquired = false;
        try
        {
            await s_adapterConfigLock.WaitAsync().ConfigureAwait(false);
            lockAcquired = true;
            var (luid, _) = ResolveAdapterIdentity(adapter);
            if (luid == 0)
            {
                return;
            }

            var effectiveMtu = (uint)Math.Clamp(mtu, SmartMtuEngine.MinMtu, 1500);
            _ = Win32NetworkHelper.ConfigureIpInterface(luid, Win32NetworkHelper.AfInet, effectiveMtu, null);
            _ = Win32NetworkHelper.ConfigureIpInterface(luid, Win32NetworkHelper.AfInet6, effectiveMtu, null);
            AppLogger.Log(
                $"[MTU ADAPTER] Updated Wintun adapter '{adapter.Name}' MTU to {effectiveMtu}B (tunnel effective MTU: {mtu}B, TCP MSS: {effectiveMtu - 40}B)");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MTU ADAPTER ERR] Failed to update MTU: {ex.Message}");
        }
        finally
        {
            if (lockAcquired)
            {
                _ = s_adapterConfigLock.Release();
            }
        }
    }

    //поиск лучшего маршрута через win32 api
    private static (string Gateway, int InterfaceIndex) QueryBestRouteWin32(string? targetIp)
    {
        try
        {
            var ipStr = !string.IsNullOrEmpty(targetIp) && IPAddress.TryParse(targetIp, out _) ? targetIp : "8.8.8.8";
            var ip = IPAddress.Parse(ipStr);
            var destUint = MemoryMarshal.Read<uint>(ip.GetAddressBytes());
            if (Win32NetworkHelper.GetBestRoute(destUint, 0, out var row) == 0)
            {
                var ifIndex = (int)row.DwForwardIfIndex;
                var gwUint = row.DwForwardNextHop;
                if (gwUint != 0)
                {
                    var gwBytes = BitConverter.GetBytes(gwUint);
                    var gwIp = new IPAddress(gwBytes).ToString();
                    if (gwIp != "0.0.0.0" && !gwIp.Contains(':'))
                    {
                        return (gwIp, ifIndex);
                    }
                }

                foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (TryGetIpv4IfIndex(card) != ifIndex)
                    {
                        continue;
                    }

                    var ipProps = card.GetIPProperties();
                    var gw = ipProps.GatewayAddresses
                        .FirstOrDefault(g =>
                            g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address) &&
                            !g.Address.Equals(IPAddress.Any) && !g.Address.ToString().Contains(':'))?
                        .Address.ToString();
                    if (!string.IsNullOrEmpty(gw))
                    {
                        return (gw, ifIndex);
                    }
                }

                return ("", ifIndex);
            }
        }
        catch
        {
            // ignored
        }

        return ("", 0);
    }

    //привязка сокета к физическому интерфейсу на уровне ядра tcpip.sys в обход маршрутов туннеля
    private static void BindSocketToPhysicalAdapter(Socket socket)
    {
        try
        {
            const int ipUnicastIf = 31;
            var ifIndex = s_savedPhysicalIfIndex;
            if (ifIndex > 0)
            {
                switch (socket.AddressFamily)
                {
                    case AddressFamily.InterNetwork:
                        socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)ipUnicastIf, IPAddress.HostToNetworkOrder(ifIndex));
                        break;
                    case AddressFamily.InterNetworkV6:
                        {
                            if (socket.DualMode)
                            {
                                try
                                {
                                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)ipUnicastIf,
                                        IPAddress.HostToNetworkOrder(ifIndex));
                                }
                                catch (SocketException)
                                {
                                    //игнорируем если стек не разрешает ipv4 опцию на dual-mode сокете
                                }
                            }

                            try
                            {
                                socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)ipUnicastIf, ifIndex);
                            }
                            catch (SocketException)
                            {
                                //игнорируем если стек ipv6 отключен или не поддерживает привязку к интерфейсу
                            }

                            break;
                        }
                }
            }

            if (socket.IsBound)
            {
                return;
            }

            var localIp = s_savedPhysicalLocalIp;
            if (localIp is null || IPAddress.IsLoopback(localIp))
            {
                return;
            }

            try
            {
                switch (socket.AddressFamily)
                {
                    case AddressFamily.InterNetwork when localIp.AddressFamily == AddressFamily.InterNetwork:
                        socket.Bind(new IPEndPoint(localIp, 0));
                        break;
                    case AddressFamily.InterNetworkV6 when socket.DualMode &&
                                                           localIp.AddressFamily == AddressFamily.InterNetwork:
                        socket.Bind(new IPEndPoint(localIp.MapToIPv6(), 0));
                        break;
                }
            }
            catch (SocketException)
            {
                //сбрасываем устаревший локальный ip при роуминге для безопасного пере подбора
                s_savedPhysicalLocalIp = null;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[SOCKET BIND WARN] Could not bind transport socket to physical IP: {ex.Message}");
        }
    }

    //проверка нахождения двух адресов в одной подсети
    private static bool IsInSameSubnet(IPAddress ip, IPAddress gw, IPAddress? mask)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork || gw.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var ipBytes = ip.GetAddressBytes();
        var gwBytes = gw.GetAddressBytes();
        var maskBytes = mask?.GetAddressBytes();
        if (maskBytes is not { Length: 4 })
        {
            maskBytes = [255, 255, 255, 0];
        }

        for (var i = 0; i < 4; i++)
        {
            if ((ipBytes[i] & maskBytes[i]) != (gwBytes[i] & maskBytes[i]))
            {
                return false;
            }
        }

        return true;
    }

    //определение шлюза по умолчанию и физического интерфейса
    private static (string Gateway, int InterfaceIndex) GetDefaultGatewayInfo(string? targetIp = null)
    {
        if (!string.IsNullOrEmpty(s_savedPhysicalGateway) &&
            !s_savedPhysicalGateway.Contains(':') &&
            s_savedPhysicalGateway != "0.0.0.0" &&
            s_savedPhysicalIfIndex > 0)
        {
            return (s_savedPhysicalGateway, s_savedPhysicalIfIndex);
        }

        var targetStr = !string.IsNullOrEmpty(targetIp) && IPAddress.TryParse(targetIp, out _) ? targetIp : "1.1.1.1";
        if (IPAddress.TryParse(targetStr, out var targetAddr))
        {
            try
            {
                using var socket = new Socket(targetAddr.AddressFamily, SocketType.Dgram, 0);
                socket.Connect(targetAddr, 443);
                if (socket.LocalEndPoint is IPEndPoint ep && !ep.Address.Equals(IPAddress.Any) && !IPAddress.IsLoopback(ep.Address))
                {
                    var localIp = ep.Address;
                    foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (card.OperationalStatus != OperationalStatus.Up || card.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        {
                            continue;
                        }

                        var ipProps = card.GetIPProperties();
                        var unicast = ipProps.UnicastAddresses.FirstOrDefault(u => u.Address.Equals(localIp));
                        if (unicast == null)
                        {
                            continue;
                        }

                        var ifIndex = TryGetIpv4IfIndex(card);
                        if (ifIndex < 0)
                        {
                            ifIndex = 0;
                        }

                        var mask = unicast.IPv4Mask;
                        var gw = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                 !IPAddress.IsLoopback(g.Address) &&
                                                 !g.Address.Equals(IPAddress.Any) &&
                                                 g.Address.ToString() != "0.0.0.0" &&
                                                 !g.Address.ToString().Contains(':') &&
                                                 IsInSameSubnet(localIp, g.Address, mask))?
                            .Address.ToString();

                        if (ifIndex <= 0 || string.IsNullOrEmpty(gw) || gw == "0.0.0.0")
                        {
                            continue;
                        }

                        AppLogger.Log(
                            $"[GATEWAY] Kernel socket FIB routed {targetStr} -> Local {localIp}, IfIndex: {ifIndex}, Gateway: {gw}");
                        s_savedPhysicalGateway = gw;
                        s_savedPhysicalIfIndex = ifIndex;
                        s_savedPhysicalLocalIp = localIp;
                        return (gw, ifIndex);
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        var win32Route = QueryBestRouteWin32(targetStr);
        if (win32Route.InterfaceIndex > 0 &&
            !string.IsNullOrEmpty(win32Route.Gateway) && !win32Route.Gateway.Contains(':') && win32Route.Gateway != "0.0.0.0")
        {
            AppLogger.Log($"[GATEWAY] Win32 GetBestRoute found gateway: '{win32Route.Gateway}', IfIndex: {win32Route.InterfaceIndex}");
            s_savedPhysicalGateway = win32Route.Gateway;
            s_savedPhysicalIfIndex = win32Route.InterfaceIndex;
            return win32Route;
        }

        var tunIdx = GetWintunInterfaceIndex("Obxodka");
        if (Win32NetworkHelper.TryGetLowestMetricDefaultRouteV4(tunIdx > 0 ? (uint)tunIdx : 0u, out var tableGw, out var tableIf))
        {
            AppLogger.Log($"[GATEWAY] Win32 IpForwardTable lowest-metric default route: '{tableGw}', IfIndex: {tableIf}");
            s_savedPhysicalGateway = tableGw;
            s_savedPhysicalIfIndex = tableIf;
            return (tableGw, tableIf);
        }

        if (!string.IsNullOrEmpty(s_savedPhysicalGateway) && !s_savedPhysicalGateway.Contains(':') && s_savedPhysicalGateway != "0.0.0.0" &&
            s_savedPhysicalIfIndex > 0)
        {
            return (s_savedPhysicalGateway, s_savedPhysicalIfIndex);
        }

        return ("", 0);
    }

    //закрепление прямого маршрута к серверу vpn через физический шлюз
    private static Task EnsureHostRouteAsync(string targetIp)
    {
        if (string.IsNullOrEmpty(targetIp) || !IPAddress.TryParse(targetIp, out _))
        {
            return Task.CompletedTask;
        }

        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(targetIp);
        if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
        {
            var physIf = physicalIfIndex > 0 ? (uint)physicalIfIndex : 0u;
            Win32NetworkHelper.DeleteIpv4Routes((targetIp, "255.255.255.255"));
            var exitCode = Win32NetworkHelper.AddIpv4Route(targetIp, "255.255.255.255", gw, physIf);
            AppLogger.Log(
                $"[ROUTE PIN] Physical host route for {targetIp} via {gw} (ifIndex {physicalIfIndex}, metric 1): code={exitCode}");
            if (exitCode == 0 || physicalIfIndex <= 0)
            {
                return Task.CompletedTask;
            }

            var fbCode = Win32NetworkHelper.AddIpv4Route(targetIp, "255.255.255.255", gw, 0);
            AppLogger.Log($"[ROUTE PIN] Fallback host route for {targetIp} via {gw} (metric 1): code={fbCode}");
        }
        else
        {
            AppLogger.LogWarning(
                $"[ROUTE PIN] Could not determine physical gateway for {targetIp}! Outer socket relies on kernel default route.");
        }

        return Task.CompletedTask;
    }

    //настройка маршрутов windows для туннеля vpn через iphlpapi
    private static Task SetWindowsRoutesAsync(WintunAdapter? adapter, IReadOnlyList<string> serverIps, string assignedIp, bool enable,
        string? assignedIpV6 = null)
    {
        _ = assignedIp;
        var adapterName = adapter?.Name ?? "Obxodka";
        var primaryIp = serverIps.FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _));
        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(primaryIp);
        AppLogger.Log(
            $"[ROUTE] Default Gateway: {gw}, PhysicalIfIndex: {physicalIfIndex}, Name: {adapterName}, ServerIPs: {string.Join(',', serverIps)}, Enable: {enable}");

        if (enable)
        {
            if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
            {
                var physIf = physicalIfIndex > 0 ? (uint)physicalIfIndex : 0u;
                foreach (var serverIp in serverIps)
                {
                    if (!string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _))
                    {
                        _ = Win32NetworkHelper.AddIpv4Route(serverIp, "255.255.255.255", gw, physIf);
                    }
                }
            }

            var (tunLuid, tunIfIndex) = ResolveAdapterIdentity(adapter);
            if (tunIfIndex > 0)
            {
                //шлюз vpn сервера в подсети 100.64.0.0/10
                const string tunGateway = "100.64.0.1";

                _ = Win32NetworkHelper.ConfigureIpInterface(tunLuid, Win32NetworkHelper.AfInet, null, 1);
                var r2Code = Win32NetworkHelper.AddIpv4Route("0.0.0.0", "128.0.0.0", tunGateway, tunIfIndex);
                var r3Code = Win32NetworkHelper.AddIpv4Route("128.0.0.0", "128.0.0.0", tunGateway, tunIfIndex);
                AppLogger.Log($"[ROUTE] Add IPv4 Tun Routes (iphlpapi): R2={r2Code}, R3={r3Code}");
                _ = Win32NetworkHelper.FlushDnsCache();

                if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
                {
                    WindowsSplitTunnelEngine.ConfigurePhysicalGateway(gw, physicalIfIndex, s_savedPhysicalLocalIp, primaryIp);
                    ApplyPrivateSubnetBypassRoutes(gw, physicalIfIndex);
                    if (SplitTunnelPolicy.Enabled)
                    {
                        _ = Task.Run(WindowsSplitTunnelEngine.ActivateOrReloadAsync);

                        if (SplitTunnelPolicy.BypassRemoteManagement)
                        {
                            _ = Task.Run(() => ApplyRemoteManagementBypassRoutes(gw, physicalIfIndex));
                        }
                    }
                }

                var hasServerIpv6 = !string.IsNullOrWhiteSpace(assignedIpV6) &&
                                    IPAddress.TryParse(assignedIpV6, out var parsedV6) &&
                                    parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
                var blockIpv6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
                if (!hasServerIpv6 && !blockIpv6 && !HasActiveIpv6Connectivity())
                {
                    return Task.CompletedTask;
                }

                s_ipv6RoutesApplied = true;
                _ = Win32NetworkHelper.ConfigureIpInterface(tunLuid, Win32NetworkHelper.AfInet6, null, 1);
                var r61 = Win32NetworkHelper.AddIpv6Route("::", 1, tunLuid, tunIfIndex, 1);
                var r62 = Win32NetworkHelper.AddIpv6Route("8000::", 1, tunLuid, tunIfIndex, 1);
                _ = Win32NetworkHelper.AddIpv6Route("2001::", 32, tunLuid, tunIfIndex, 1);
                _ = Win32NetworkHelper.AddIpv6Route("fd00::", 64, tunLuid, tunIfIndex, 50);
                AppLogger.Log($"[ROUTE] Add IPv6 Tun Routes (iphlpapi): R6_1={r61}, R6_2={r62}");
            }
            else
            {
                AppLogger.LogWarning("[ROUTE] WARNING: Could not find Wintun adapter ifIndex! Routes NOT added.");
            }
        }
        else
        {
            WindowsSplitTunnelEngine.CleanupAllRoutes();

            List<(string Dest, string Mask)> ipv4Targets =
            [
                ("0.0.0.0", "128.0.0.0"),
                ("128.0.0.0", "128.0.0.0"),
                ("10.0.0.0", "255.0.0.0"),
                ("172.16.0.0", "255.240.0.0"),
                ("192.168.0.0", "255.255.0.0"),
                ("169.254.0.0", "255.255.0.0"),
                .. from serverIp in serverIps
                where !string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _)
                select (serverIp, "255.255.255.255")
            ];

            foreach (var mgmtIp in s_remoteMgmtBypassIps.Keys.ToArray())
            {
                ipv4Targets.Add((mgmtIp, "255.255.255.255"));
                _ = s_remoteMgmtBypassIps.TryRemove(mgmtIp, out _);
            }

            Win32NetworkHelper.DeleteIpv4Routes(ipv4Targets);

            if (!s_ipv6RoutesApplied)
            {
                return Task.CompletedTask;
            }

            s_ipv6RoutesApplied = false;
            var (_, tunIfIndex) = ResolveAdapterIdentity(adapter);
            if (tunIfIndex > 0)
            {
                Win32NetworkHelper.DeleteIpv6Routes(tunIfIndex, ("::", 1), ("8000::", 1), ("2001::", 32), ("fd00::", 64));
            }
        }

        return Task.CompletedTask;
    }

    //добавление маршрутов обхода для локальных приватных подсетей
    private static void ApplyPrivateSubnetBypassRoutes(string gw, int physicalIfIndex)
    {
        if (string.IsNullOrEmpty(gw) || physicalIfIndex <= 0)
        {
            return;
        }

        var ifIdx = (uint)physicalIfIndex;
        _ = Win32NetworkHelper.AddIpv4Route("10.0.0.0", "255.0.0.0", gw, ifIdx);
        _ = Win32NetworkHelper.AddIpv4Route("172.16.0.0", "255.240.0.0", gw, ifIdx);
        _ = Win32NetworkHelper.AddIpv4Route("192.168.0.0", "255.255.0.0", gw, ifIdx);
        _ = Win32NetworkHelper.AddIpv4Route("169.254.0.0", "255.255.0.0", gw, ifIdx);
    }

    //добавление маршрутов обхода для сессий удаленного администрирования через нативный tcp table
    private static void ApplyRemoteManagementBypassRoutes(string nextHop, int physicalIfIndex)
    {
        try
        {
            var running = Process.GetProcesses().Any(p =>
            {
                try
                {
                    var n = p.ProcessName;
                    return n.Contains("AnyDesk", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("TeamViewer", StringComparison.OrdinalIgnoreCase) ||
                           n.Contains("RustDesk", StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
                finally
                {
                    p.Dispose();
                }
            });

            if (!running)
            {
                return;
            }

            var physIf = physicalIfIndex > 0 ? (uint)physicalIfIndex : 0u;
            foreach (var remoteIp in Win32NetworkHelper.GetEstablishedTcpRemoteIps(7070, 5938, 3389, 21116)
                         .Where(remoteIp => Win32NetworkHelper.AddIpv4Route(remoteIp, "255.255.255.255", nextHop, physIf) == 0))
            {
                _ = s_remoteMgmtBypassIps.TryAdd(remoteIp, 0);
                SplitTunnelPolicy.RecordBypassRoute(remoteIp, "Сессия удалённого доступа");
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[SPLIT-TUNNEL WARN] Remote management bypass check: {ex.Message}");
        }
    }
}
