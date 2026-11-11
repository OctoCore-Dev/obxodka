using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//диагностика и настройка сетевых интерфейсов, защита от утечек dns
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    private static int? s_savedDisableSmartNameResolution;

    //предстартовая проверка сети и dns
    public async Task<string?> RunNetworkPreflightAsync()
    {
        //проверка физической доступности сети без лишних задержек
        if (!OctopusEngine.IsUplinkAvailable)
        {
            return "Отсутствует подключение к сети (Wi-Fi или сетевой кабель отключены).\n\n" +
                   "Проверьте физическое сетевое подключение и повторите попытку.";
        }

        OnLogUpdated?.Invoke(VpnStatusMessages.CheckingInternetAndDns);

        string[] referenceIps = ["77.88.55.242", "1.1.1.1", "8.8.8.8"];
        var referenceTasks = referenceIps.Select(ip => ProbeTcpAsync(ip, 443, 3000)).ToArray();
        var serverTask = ProbeTcpAsync(AppConfig.DirectServerIp, 443, 3000);
        var dnsTask = ProbeSystemDnsAsync("ya.ru", 3000);

        var referenceResults = await Task.WhenAll(referenceTasks).ConfigureAwait(false);
        var (serverOk, serverError) = await serverTask.ConfigureAwait(false);
        var dnsOk = await dnsTask.ConfigureAwait(false);

        for (var i = 0; i < referenceIps.Length; i++)
        {
            var (refOk, refError) = referenceResults[i];
            AppLogger.Log($"[PREFLIGHT] TCP {referenceIps[i]}:443 -> {(refOk ? "OK" : refError.ToString())}");
        }
        AppLogger.Log($"[PREFLIGHT] TCP {AppConfig.DirectServerIp}:443 -> {(serverOk ? "OK" : serverError.ToString())}");
        AppLogger.Log($"[PREFLIGHT] System DNS -> {(dnsOk ? "OK" : "FAIL")}");

        var internetOk = referenceResults.Any(r => r.ok);
        if (!internetOk && !serverOk)
        {
            return "Нет доступа в интернет: компьютер не может подключиться ни к одному узлу сети.\n\n" +
                   "Частые причины:\n" +
                   "• Kill Switch другого ранее работавшего VPN блокирует исходящий трафик.\n" +
                   "• Отключен сетевой кабель или Wi-Fi соединение.\n\n" +
                   "Проверьте сетевое подключение и повторите попытку.";
        }

        if (!serverOk)
        {
            OnLogUpdated?.Invoke(VpnStatusMessages.MainNodeUnavailableDirectly);
        }

        if (!dnsOk)
        {
            OnLogUpdated?.Invoke(VpnStatusMessages.DnsFailureDetected);
            await AutoHealAdapterDnsAsync().ConfigureAwait(false);
            dnsOk = await ProbeSystemDnsAsync("ya.ru", 2500).ConfigureAwait(false);
            if (dnsOk)
            {
                OnLogUpdated?.Invoke(VpnStatusMessages.DnsRestoredSuccessfully);
            }
            else
            {
                OnLogUpdated?.Invoke(VpnStatusMessages.SwitchingToAutonomousDns);
            }
        }
        else if (internetOk || serverOk)
        {
            OnLogUpdated?.Invoke(VpnStatusMessages.InternetAndDnsHealthy);
        }

        return null;
    }

    //автовосстановление серверов dns адаптеров
    private static async Task AutoHealAdapterDnsAsync()
    {
        try
        {
            var targetIndices = new HashSet<int>();
            var targetNames = new HashSet<string>();

            var (_, physIfIndex) = GetDefaultGatewayInfo();
            if (physIfIndex > 0)
            {
                _ = targetIndices.Add(physIfIndex);
            }

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                if (IsConflictingAdapter(nic))
                {
                    continue;
                }

                if (nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet)
                {
                    var idx = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                    if (idx > 0)
                    {
                        _ = targetIndices.Add(idx);
                    }
                    if (!string.IsNullOrWhiteSpace(nic.Name))
                    {
                        _ = targetNames.Add(nic.Name);
                    }
                }
            }

            foreach (var idx in targetIndices)
            {
                _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses '77.88.8.8','1.1.1.1' -ErrorAction Stop }} catch {{ }}\"");
            }

            foreach (var name in targetNames)
            {
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{name}\" static 77.88.8.8 primary validate=no");
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{name}\" 1.1.1.1 index=2 validate=no");
            }

            _ = await RunCmdAsync("ipconfig", "/flushdns");
        }
        catch { }
    }

    //зондирование tcp соединения
    private static async Task<(bool ok, SocketError error)> ProbeTcpAsync(string ip, int port, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(IPAddress.Parse(ip), port, cts.Token).ConfigureAwait(false);
            return (true, SocketError.Success);
        }
        catch (SocketException ex)
        {
            return (false, ex.SocketErrorCode);
        }
        catch
        {
            return (false, SocketError.TimedOut);
        }
    }

    //проверка разрешения dns
    private static async Task<bool> ProbeSystemDnsAsync(string host, int timeoutMs)
    {
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            var addrs = await Dns.GetHostAddressesAsync(host, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
            return addrs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    //интеллектуальное определение конкурирующего стороннего vpn адаптера
    private static bool IsConflictingAdapter(NetworkInterface c)
    {
        if (c.OperationalStatus != OperationalStatus.Up ||
            c.NetworkInterfaceType == NetworkInterfaceType.Loopback)
        {
            return false;
        }

        var name = c.Name;
        var desc = c.Description;

        //собственный адаптер obxodka не является сторонним
        if (name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Obxodka", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        //физический сетевой адаптер с активным шлюзом в интернет не является vpn
        var ipv4Props = c.GetIPProperties().GetIPv4Properties();
        var ifIndex = ipv4Props?.Index ?? -1;
        if (ifIndex > 0 && ifIndex == s_savedPhysicalIfIndex)
        {
            return false;
        }

        //1. интерфейс уровня ядра с явным типом туннеля или ppp соединения
        if (c.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp or NetworkInterfaceType.Slip)
        {
            return true;
        }

        //2. виртуальный или программный адаптер с активным шлюзом по умолчанию
        if (IsVirtualOrSoftwareAdapter(c))
        {
            var ipProps = c.GetIPProperties();
            return ipProps.GatewayAddresses.Any(g =>
            {
                var gw = g.Address.ToString();
                return gw is "0.0.0.0" or "::" || (!IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any));
            });
        }

        return false;
    }

    //проверка является ли интерфейс виртуальным программным адаптером
    private static bool IsVirtualOrSoftwareAdapter(NetworkInterface c)
    {
        var name = c.Name;
        var desc = c.Description;

        //проверка описания и имени драйвера на признаки виртуального туннеля
        if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Tunnel", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
        {
            //исключаем виртуальные коммутаторы hyper-v и локальные адаптеры виртуализации без маршрутизации
            return !desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) &&
                   !desc.Contains("Default Switch", StringComparison.OrdinalIgnoreCase);
        }

        //проверка mac адреса: виртуальные туннельные интерфейсы часто имеют пустой или псевдо-mac
        var mac = c.GetPhysicalAddress().GetAddressBytes();
        if (mac.Length == 0 || (mac.Length == 6 && mac.All(b => b == 0)))
        {
            var ipProps = c.GetIPProperties();
            return ipProps.UnicastAddresses.Count > 0;
        }

        return false;
    }

    //обнаружение имени конкурирующего vpn адаптера
    public string? DetectConflictingVpn()
    {
        try
        {
            foreach (var c in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (IsConflictingAdapter(c))
                {
                    return c.Name;
                }
            }
        }
        catch { }

        return null;
    }

    //диагностика сетевых адаптеров
    private static void LogNetworkDiagnostics(Action<string>? onLogUpdated = null)
    {
        try
        {
            foreach (var c in NetworkInterface.GetAllNetworkInterfaces())
            {
                var props = c.GetIPProperties();
                var ips = string.Join(", ", props.UnicastAddresses.Select(u => $"{u.Address}/{u.IPv4Mask}"));
                var gws = string.Join(", ", props.GatewayAddresses.Select(g => g.Address.ToString()));
                var idx = props.GetIPv4Properties()?.Index ?? -1;
                AppLogger.Log($"[NET-DIAG] Card: '{c.Name}' ({c.Description}), IfIndex: {idx}, Status: {c.OperationalStatus}, IPs: [{ips}], Gateways: [{gws}]");

                if (IsConflictingAdapter(c))
                {
                    AppLogger.Log($"[CONFLICTING-VPN] Card '{c.Name}' ({c.Description}) is active. It may filter or conflict with Obxodka traffic.");
                    onLogUpdated?.Invoke(VpnStatusMessages.ThirdPartyVpnWarning(c.Name));
                }
            }
        }
        catch { }
    }

    //включение защиты smhnr от утечки dns через другие интерфейсы
    private static void ApplySmhnrProtection()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient");
            if (key != null)
            {
                var existing = key.GetValue("DisableSmartNameResolution");
                s_savedDisableSmartNameResolution = existing is int intVal ? intVal : -1;
                key.SetValue("DisableSmartNameResolution", 1, RegistryValueKind.DWord);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SMHNR ENABLE ERROR] {ex.Message}");
        }
    }

    //восстановление исходной настройки smhnr
    private static void RestoreSmhnrProtection()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient", true);
            if (key != null)
            {
                if (s_savedDisableSmartNameResolution == -1)
                {
                    key.DeleteValue("DisableSmartNameResolution", false);
                }
                else if (s_savedDisableSmartNameResolution.HasValue)
                {
                    key.SetValue("DisableSmartNameResolution", s_savedDisableSmartNameResolution.Value, RegistryValueKind.DWord);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SMHNR DISABLE ERROR] {ex.Message}");
        }
    }

    //активация маршрутов dns в интерфейс туннеля для защиты от утечек
    private static async Task EnableDnsLeakProtectionAsync(string adapterName, string assignedIp)
    {
        ApplySmhnrProtection();
        try
        {
            var ifIndex = GetWintunInterfaceIndex(adapterName);
            var ifArg = ifIndex > 0 ? $" if {ifIndex}" : "";
            //шлюз vpn сервера в подсети 100.64.0.0/10
            const string tunGateway = "100.64.0.1";
            var addTasks = new List<Task>();
            foreach (var dns in DnsDefaults.TrustedDnsServers)
            {
                addTasks.Add(RunCmdAsync("route", $"add {dns} mask 255.255.255.255 {tunGateway} metric 1{ifArg}"));
            }
            await Task.WhenAll(addTasks).ConfigureAwait(false);
            _ = DnsFlushResolverCache();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DNS-LEAK ERROR] {ex.Message}");
        }
    }

    //удаление защитных маршрутов dns
    private static async Task DisableDnsLeakProtectionAsync()
    {
        RestoreSmhnrProtection();
        try
        {
            var delTasks = new List<Task>();
            foreach (var dns in DnsDefaults.TrustedDnsServers)
            {
                delTasks.Add(RunCmdAsync("route", $"delete {dns} mask 255.255.255.255", timeoutMs: 1500));
            }
            await Task.WhenAll(delTasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            _ = DnsFlushResolverCache();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DNS-LEAK CLEANUP ERROR] {ex.Message}");
        }
    }
}
