namespace obxodka.Platforms.Windows;

//управление системными маршрутами windows и привязкой сетевых сокетов к физическому шлюзу
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    private static string s_savedPhysicalGateway = "";
    private static int s_savedPhysicalIfIndex;
    private static IPAddress? s_savedPhysicalLocalIp;

    //структура строки таблицы маршрутизации iphlpapi
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

    [LibraryImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int DnsFlushResolverCache();

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

    //очистка устаревших маршрутов и восстановление исходного состояния сети
    private static async Task CleanupStaleRoutesAsync()
    {
        WindowsSplitTunnelEngine.CleanupAllRoutes();
        _ = await RunCmdAsync("route", "delete 0.0.0.0 mask 128.0.0.0");
        _ = await RunCmdAsync("route", "delete 128.0.0.0 mask 128.0.0.0");
        _ = await RunCmdAsync("route", "delete 10.0.0.0 mask 255.0.0.0");
        _ = await RunCmdAsync("route", "delete 172.16.0.0 mask 255.240.0.0");
        _ = await RunCmdAsync("route", "delete 192.168.0.0 mask 255.255.0.0");
        _ = await RunCmdAsync("route", "delete 169.254.0.0 mask 255.255.0.0");
        foreach (var relayIp in AppConfig.DirectRelayIps)
        {
            _ = await RunCmdAsync("route", $"delete {relayIp} mask 255.255.255.255");
        }
        await DisableDnsLeakProtectionAsync();

        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var oldRecoveryFile = Path.Combine(appData, "Obxodka", "vpn_recovery.json");
            if (File.Exists(oldRecoveryFile))
            {
                File.Delete(oldRecoveryFile);
            }
        }
        catch { }
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

            return card?.GetIPProperties().GetIPv4Properties()?.Index ?? -1;
        }
        catch
        {
            return -1;
        }
    }

    //настройка сетевого адреса и dns для адаптера wintun
    private static async Task SetAdapterConfigAsync(string adapterName, string ip, string mask, string? ipv6 = null)
    {
        var pfx = mask == "255.192.0.0" ? 10 : 24;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var (exitCode, _) = await RunCmdAsync("netsh", $"interface ipv4 set address name=\"{adapterName}\" static {ip} {mask} none");
            if (exitCode == 0)
            {
                var (mCode, _) = await RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{adapterName}\" mtu=1500 store=active");
                var (metCode, _) = await RunCmdAsync("netsh", $"interface ipv4 set interface \"{adapterName}\" metric=50");
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{adapterName}\" static {DnsDefaults.PrimaryDns} primary validate=no");
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{adapterName}\" {DnsDefaults.SecondaryDns} index=2 validate=no");
                _ = DnsFlushResolverCache();
                AppLogger.Log($"[NET CONFIG] IPv4 netsh set: mtuExit={mCode}, metricExit={metCode}");

                if (!string.IsNullOrWhiteSpace(ipv6))
                {
                    var (v6Code, v6Out) = await RunCmdAsync("netsh", $"interface ipv6 set address interface=\"{adapterName}\" address={ipv6} store=active");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set subinterface \"{adapterName}\" mtu=1500 store=active");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set interface \"{adapterName}\" metric=50");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set dnsservers name=\"{adapterName}\" static 2606:4700:4700::1111 primary validate=no");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 add dnsservers name=\"{adapterName}\" 2001:4860:4860::8888 index=2 validate=no");
                    _ = DnsFlushResolverCache();
                    AppLogger.Log($"[NET CONFIG] IPv6 netsh set: code={v6Code}, out={v6Out}");
                }

                return;
            }
            await Task.Delay(200);
        }

        var (psCode, psErr) = await RunCmdAsync("powershell",
            $"-NoProfile -Command \"try {{ New-NetIPAddress -InterfaceAlias '{adapterName}' -IPAddress '{ip}' -PrefixLength {pfx} -ErrorAction Stop; Set-DnsClientServerAddress -InterfaceAlias '{adapterName}' -ServerAddresses '{DnsDefaults.PrimaryDns}','{DnsDefaults.SecondaryDns}' -ErrorAction Stop }} catch {{ $_.Exception.Message }}\"");
        AppLogger.Log($"[NET CONFIG] IPv4 PowerShell fallback: code={psCode}, out={psErr}");

        if (!string.IsNullOrWhiteSpace(ipv6))
        {
            var (psV6Code, psV6Err) = await RunCmdAsync("powershell",
                $"-NoProfile -Command \"try {{ New-NetIPAddress -InterfaceAlias '{adapterName}' -IPAddress '{ipv6}' -PrefixLength 64 -ErrorAction Stop; Set-DnsClientServerAddress -InterfaceAlias '{adapterName}' -ServerAddresses '2606:4700:4700::1111','2001:4860:4860::8888' -ErrorAction Stop }} catch {{ $_.Exception.Message }}\"");
            AppLogger.Log($"[NET CONFIG] IPv6 PowerShell fallback: code={psV6Code}, out={psV6Err}");
        }
    }

    //обновление mtu адаптера
    private static async Task UpdateAdapterMtuAsync(string adapterName, int mtu)
    {
        try
        {
            _ = await RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{adapterName}\" mtu=1500 store=active").ConfigureAwait(false);
            _ = await RunCmdAsync("netsh", $"interface ipv6 set subinterface \"{adapterName}\" mtu=1500 store=active").ConfigureAwait(false);
            AppLogger.Log($"[MTU ADAPTER] Maintaining Wintun adapter '{adapterName}' at 1500B (effective MSS MTU: {mtu}B)");
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[MTU ADAPTER ERR] Failed to update MTU: {ex.Message}");
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
            if (GetBestRoute(destUint, 0, out var row) == 0)
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
                    var ipProps = card.GetIPProperties();
                    if (ipProps.GetIPv4Properties()?.Index == ifIndex)
                    {
                        var gw = ipProps.GatewayAddresses
                            .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any) && !g.Address.ToString().Contains(':'))?
                            .Address.ToString();
                        if (!string.IsNullOrEmpty(gw))
                        {
                            return (gw, ifIndex);
                        }
                    }
                }

                return ("", ifIndex);
            }
        }
        catch { }
        return ("", 0);
    }

    //привязка сокета к физическому интерфейсу в обход туннеля
    private static void BindSocketToPhysicalAdapter(Socket socket)
    {
        try
        {
            var localIp = s_savedPhysicalLocalIp;
            if (localIp is not null && localIp.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(localIp))
            {
                socket.Bind(new IPEndPoint(localIp, 0));
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
        if (maskBytes == null || maskBytes.Length != 4)
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
                        if (unicast != null)
                        {
                            var ifIndex = ipProps.GetIPv4Properties()?.Index ?? 0;
                            var mask = unicast.IPv4Mask;
                            var gw = ipProps.GatewayAddresses
                                .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                    !IPAddress.IsLoopback(g.Address) &&
                                                    !g.Address.Equals(IPAddress.Any) &&
                                                    g.Address.ToString() != "0.0.0.0" &&
                                                    !g.Address.ToString().Contains(':') &&
                                                    IsInSameSubnet(localIp, g.Address, mask))?
                                .Address.ToString();

                            if (string.IsNullOrEmpty(gw) && ifIndex > 0)
                            {
                                try
                                {
                                    using var proc = Process.Start(new ProcessStartInfo("powershell", $"-NoProfile -Command \"(Get-NetRoute -InterfaceIndex {ifIndex} -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object {{ $_.NextHop -ne '0.0.0.0' -and $_.NextHop -notlike '*:*' }} | Select-Object -First 1).NextHop\"")
                                    {
                                        CreateNoWindow = true,
                                        WindowStyle = ProcessWindowStyle.Hidden,
                                        RedirectStandardOutput = true,
                                        UseShellExecute = false
                                    });
                                    if (proc != null)
                                    {
                                        var psGw = proc.StandardOutput.ReadToEnd().Trim();
                                        _ = proc.WaitForExit(3000);
                                        if (!string.IsNullOrEmpty(psGw) && IPAddress.TryParse(psGw, out var parsedPsGw) && psGw != "0.0.0.0" && !psGw.Contains(':') && IsInSameSubnet(localIp, parsedPsGw, mask))
                                        {
                                            gw = psGw;
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (ifIndex > 0 && !string.IsNullOrEmpty(gw) && gw != "0.0.0.0")
                            {
                                AppLogger.Log($"[GATEWAY] Kernel socket FIB routed {targetStr} -> Local {localIp}, IfIndex: {ifIndex}, Gateway: {gw}");
                                s_savedPhysicalGateway = gw;
                                s_savedPhysicalIfIndex = ifIndex;
                                s_savedPhysicalLocalIp = localIp;
                                return (gw, ifIndex);
                            }
                        }
                    }
                }
            }
            catch { }
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

        try
        {
            using var psProc = Process.Start(new ProcessStartInfo("powershell", "-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.NextHop -notlike '*:*' } | Sort-Object RouteMetric | Select-Object -First 1 | ForEach-Object { $_.NextHop + ',' + $_.InterfaceIndex })\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (psProc != null)
            {
                var line = psProc.StandardOutput.ReadToEnd().Trim();
                _ = psProc.WaitForExit(3000);
                var parts = line.Split(',');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out _) && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var psIf) && psIf > 0)
                {
                    AppLogger.Log($"[GATEWAY] PowerShell lowest-metric default route: '{parts[0]}', IfIndex: {psIf}");
                    s_savedPhysicalGateway = parts[0];
                    s_savedPhysicalIfIndex = psIf;
                    return (parts[0], psIf);
                }
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(s_savedPhysicalGateway) && !s_savedPhysicalGateway.Contains(':') && s_savedPhysicalGateway != "0.0.0.0" && s_savedPhysicalIfIndex > 0)
        {
            return (s_savedPhysicalGateway, s_savedPhysicalIfIndex);
        }

        return ("", 0);
    }

    //закрепление прямого маршрута к серверу vpn через физический шлюз
    private static async Task EnsureHostRouteAsync(string targetIp)
    {
        if (string.IsNullOrEmpty(targetIp) || !IPAddress.TryParse(targetIp, out _))
        {
            return;
        }
        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(targetIp);
        if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
        {
            var physIfArg = physicalIfIndex > 0 ? $" if {physicalIfIndex}" : "";
            _ = await RunCmdAsync("route", $"delete {targetIp} mask 255.255.255.255");
            var (exitCode, _) = await RunCmdAsync("route", $"add {targetIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
            AppLogger.Log($"[ROUTE PIN] Physical host route for {targetIp} via {gw} (ifIndex {physicalIfIndex}, metric 1): exitCode={exitCode}");
            if (exitCode != 0 && physicalIfIndex > 0)
            {
                var (fbCode, _) = await RunCmdAsync("route", $"add {targetIp} mask 255.255.255.255 {gw} metric 1");
                AppLogger.Log($"[ROUTE PIN] Fallback host route for {targetIp} via {gw} (metric 1): exitCode={fbCode}");
            }
        }
        else
        {
            AppLogger.LogWarning($"[ROUTE PIN] Could not determine physical gateway for {targetIp}! Outer socket relies on kernel default route.");
        }
    }

    //переключение маршрута хоста при смене сервера
    private static async Task SwitchHostRouteAsync(string oldIp, string newIp)
    {
        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(newIp);
        if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
        {
            var physIfArg = physicalIfIndex > 0 ? $" if {physicalIfIndex}" : "";
            if (!string.IsNullOrEmpty(oldIp) && oldIp != newIp)
            {
                _ = await RunCmdAsync("route", $"delete {oldIp} mask 255.255.255.255");
            }
            _ = await RunCmdAsync("route", $"delete {newIp} mask 255.255.255.255");
            var (exitCode, output) = await RunCmdAsync("route", $"add {newIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
            AppLogger.Log($"[ROUTE] SwitchHostRoute to {newIp} via {gw} (if {physicalIfIndex}): {exitCode} {output}");
            if (exitCode != 0 && physicalIfIndex > 0)
            {
                _ = await RunCmdAsync("route", $"add {newIp} mask 255.255.255.255 {gw} metric 1");
            }
        }
    }

    //настройка маршрутов windows для туннеля vpn
    private static async Task SetWindowsRoutesAsync(string adapterName, IReadOnlyList<string> serverIps, string assignedIp, bool enable, string? assignedIpV6 = null)
    {
        var primaryIp = serverIps.FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _));
        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(primaryIp);
        AppLogger.Log($"[ROUTE] Default Gateway: {gw}, PhysicalIfIndex: {physicalIfIndex}, Name: {adapterName}, ServerIPs: {string.Join(',', serverIps)}, Enable: {enable}");

        if (enable && !string.IsNullOrEmpty(adapterName))
        {
            var ifIndex = "";
            for (var attempt = 0; attempt < 25; attempt++)
            {
                var idx = GetWintunInterfaceIndex(adapterName);
                if (idx > 0)
                {
                    ifIndex = idx.ToString(CultureInfo.InvariantCulture);
                    break;
                }
                await Task.Delay(100);
            }

            if (string.IsNullOrEmpty(ifIndex))
            {
                var (_, output) = await RunCmdAsync("powershell",
                    $"-NoProfile -Command \"(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*{adapterName}*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.Name -like '*Wintun*' }} | Select-Object -First 1).ifIndex\"");
                ifIndex = output.Trim();
            }

            var physIfArg = physicalIfIndex > 0 ? $" if {physicalIfIndex}" : "";
            if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
            {
                var srvRouteTasks = new List<Task>();
                foreach (var serverIp in serverIps)
                {
                    if (!string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _))
                    {
                        srvRouteTasks.Add(RunCmdAsync("route", $"add {serverIp} mask 255.255.255.255 {gw} metric 1{physIfArg}", timeoutMs: 1500));
                    }
                }
                if (srvRouteTasks.Count > 0)
                {
                    await Task.WhenAll(srvRouteTasks).ConfigureAwait(false);
                }
            }

            if (!string.IsNullOrEmpty(ifIndex))
            {
                var tunGateway = "100.64.0.1";
                if (IPAddress.TryParse(assignedIp, out var parsedAssigned))
                {
                    var bytes = parsedAssigned.GetAddressBytes();
                    if (bytes.Length == 4)
                    {
                        tunGateway = $"{bytes[0]}.{bytes[1]}.0.1";
                    }
                }

                var r2Task = RunCmdAsync("route", $"add 0.0.0.0 mask 128.0.0.0 {tunGateway} metric 1 if {ifIndex}", timeoutMs: 1500);
                var r3Task = RunCmdAsync("route", $"add 128.0.0.0 mask 128.0.0.0 {tunGateway} metric 1 if {ifIndex}", timeoutMs: 1500);
                var metricTask = RunCmdAsync("netsh", $"interface ipv4 set interface \"{adapterName}\" metric=1", timeoutMs: 1500);
                var results = await Task.WhenAll(r2Task, r3Task, metricTask).ConfigureAwait(false);
                AppLogger.Log($"[ROUTE] Add IPv4 Tun Routes: R2={results[0].exitCode}, R3={results[1].exitCode}");
                _ = DnsFlushResolverCache();

                if (!string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
                {
                    WindowsSplitTunnelEngine.ConfigurePhysicalGateway(gw, physicalIfIndex, s_savedPhysicalLocalIp, primaryIp);
                    await ApplyPrivateSubnetBypassRoutesAsync(gw, physicalIfIndex).ConfigureAwait(false);
                    if (SplitTunnelPolicy.Enabled)
                    {
                        _ = Task.Run(WindowsSplitTunnelEngine.ActivateOrReloadAsync);

                        if (SplitTunnelPolicy.BypassRemoteManagement)
                        {
                            _ = Task.Run(() => ApplyRemoteManagementBypassRoutesAsync(gw, physIfArg));
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(assignedIpV6) && IPAddress.TryParse(assignedIpV6, out var parsedV6) && parsedV6.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    var v6MetricTask = RunCmdAsync("netsh", $"interface ipv6 set interface \"{adapterName}\" metric=1", timeoutMs: 1500);
                    var v6R1Task = RunCmdAsync("netsh", $"interface ipv6 add route ::/1 interface=\"{adapterName}\" metric=1", timeoutMs: 1500);
                    var v6R2Task = RunCmdAsync("netsh", $"interface ipv6 add route 8000::/1 interface=\"{adapterName}\" metric=1", timeoutMs: 1500);
                    var v6R3Task = RunCmdAsync("netsh", $"interface ipv6 add route fd00::/64 interface=\"{adapterName}\" metric=50", timeoutMs: 1500);
                    _ = await Task.WhenAll(v6MetricTask, v6R1Task, v6R2Task, v6R3Task).ConfigureAwait(false);
                }
            }
            else
            {
                AppLogger.LogWarning("[ROUTE] WARNING: Could not find Wintun adapter ifIndex! Routes NOT added.");
            }
        }
        else if (!enable)
        {
            WindowsSplitTunnelEngine.CleanupAllRoutes();

            var deleteTasks = new List<Task>
            {
                RunCmdAsync("route", "delete 0.0.0.0 mask 128.0.0.0", timeoutMs: 1500),
                RunCmdAsync("route", "delete 128.0.0.0 mask 128.0.0.0", timeoutMs: 1500),
                RunCmdAsync("route", "delete 10.0.0.0 mask 255.0.0.0", timeoutMs: 1500),
                RunCmdAsync("route", "delete 172.16.0.0 mask 255.240.0.0", timeoutMs: 1500),
                RunCmdAsync("route", "delete 192.168.0.0 mask 255.255.0.0", timeoutMs: 1500),
                RunCmdAsync("route", "delete 169.254.0.0 mask 255.255.0.0", timeoutMs: 1500)
            };

            foreach (var serverIp in serverIps)
            {
                if (!string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _))
                {
                    deleteTasks.Add(RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255", timeoutMs: 1500));
                }
            }

            if (!string.IsNullOrEmpty(adapterName))
            {
                deleteTasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route ::/1 interface=\"{adapterName}\"", timeoutMs: 1500));
                deleteTasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route 8000::/1 interface=\"{adapterName}\"", timeoutMs: 1500));
                deleteTasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route fd00::/64 interface=\"{adapterName}\"", timeoutMs: 1500));
            }

            try
            {
                await Task.WhenAll(deleteTasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[ROUTE DELETE TIMEOUT/ERROR] {ex.Message}");
            }
        }
    }

    //добавление маршрутов обхода для локальных приватных подсетей
    private static async Task ApplyPrivateSubnetBypassRoutesAsync(string gw, int physicalIfIndex)
    {
        if (string.IsNullOrEmpty(gw) || physicalIfIndex <= 0)
        {
            return;
        }

        var physIfArg = $"if {physicalIfIndex}";
        var t1 = RunCmdAsync("route", $"add 10.0.0.0 mask 255.0.0.0 {gw} metric 1 {physIfArg}", timeoutMs: 1500);
        var t2 = RunCmdAsync("route", $"add 172.16.0.0 mask 255.240.0.0 {gw} metric 1 {physIfArg}", timeoutMs: 1500);
        var t3 = RunCmdAsync("route", $"add 192.168.0.0 mask 255.255.0.0 {gw} metric 1 {physIfArg}", timeoutMs: 1500);
        var t4 = RunCmdAsync("route", $"add 169.254.0.0 mask 255.255.0.0 {gw} metric 1 {physIfArg}", timeoutMs: 1500);
        _ = await Task.WhenAll(t1, t2, t3, t4).ConfigureAwait(false);
    }

    //добавление маршрутов обхода для сессий удаленного администрирования
    private static async Task ApplyRemoteManagementBypassRoutesAsync(string nextHop, string physIfArg)
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
                catch { return false; }
                finally { p.Dispose(); }
            });

            if (!running)
            {
                return;
            }

            var (code, output) = await RunCmdAsync("netstat", "-n -p tcp");
            if (code == 0 && !string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split('\n');
                foreach (var line in lines)
                {
                    if (!line.Contains("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (line.Contains(":7070 ") || line.Contains(":5938 ") || line.Contains(":3389 ") || line.Contains(":21116 "))
                    {
                        var parts = line.Split([' '], StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 3)
                        {
                            var foreignAddr = parts[2];
                            var colonIdx = foreignAddr.LastIndexOf(':');
                            if (colonIdx > 0)
                            {
                                var remoteIp = foreignAddr[..colonIdx];
                                if (IPAddress.TryParse(remoteIp, out var parsedIp) &&
                                    !IPAddress.IsLoopback(parsedIp) &&
                                    parsedIp.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    _ = await RunCmdAsync("route", $"add {remoteIp} mask 255.255.255.255 {nextHop} metric 1{physIfArg}");
                                    SplitTunnelPolicy.RecordBypassRoute(remoteIp, "Сессия удалённого доступа");
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[SPLIT-TUNNEL WARN] Remote management bypass check: {ex.Message}");
        }
    }
}
