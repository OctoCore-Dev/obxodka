using Microsoft.Win32;
using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//диагностика и настройка сетевых интерфейсов, защита от утечек DNS
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsVpnService
{
    private const string NrptRuleComment = "ObxodkaVpnDns";
    private static int? s_savedDisableSmartNameResolution;
    private static volatile uint s_dnsLeakAdapterIfIndex;

    //предстартовая проверка сети и DNS
    public async Task<string?> RunNetworkPreflightAsync()
    {
        //проверка физической доступности сети без лишних задержек
        if (!OctopusEngine.IsUplinkAvailable)
        {
            return "Отсутствует подключение к сети (Wi-Fi или сетевой кабель отключены).\n\n" +
                   "Проверьте физическое сетевое подключение и повторите попытку.";
        }

        OctopusEngine.Current.EmitStatus(VpnStatusMessages.CheckingInternetAndDns);

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
            if (internetOk)
            {
                //прямой ip уже не отвечает при рабочем интернете — сразу штрафуем его, чтобы стартовать с Cloudflare без 12с простоя
                OctopusEngine.MarkNodeByteCapped(AppConfig.DirectServerIp);
            }

            OctopusEngine.Current.EmitStatus(VpnStatusMessages.MainNodeUnavailableDirectly);
        }

        if (!dnsOk)
        {
            OctopusEngine.Current.EmitStatus(VpnStatusMessages.DnsFailureDetected);
            await AutoHealAdapterDnsAsync().ConfigureAwait(false);
            dnsOk = await ProbeSystemDnsAsync("ya.ru", 2500).ConfigureAwait(false);
            OctopusEngine.Current.EmitStatus(dnsOk
                ? VpnStatusMessages.DnsRestoredSuccessfully
                : VpnStatusMessages.SwitchingToAutonomousDns);
        }
        else if (internetOk || serverOk)
        {
            OctopusEngine.Current.EmitStatus(VpnStatusMessages.InternetAndDnsHealthy);
        }

        return null;
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
        catch
        {
            // ignored
        }

        return null;
    }

    private static string GetSmhnrBackupPath() => Path.Join(GetAppDataFolderPath(), "smhnr_backup.txt");

    private static string GetDnsBackupPath() => Path.Join(GetAppDataFolderPath(), "dns_backup.txt");

    private static string GetVpnDnsLeakBackupPath() => Path.Join(GetAppDataFolderPath(), "vpn_dns_leak_backup.txt");

    private static string GetAppDataFolderPath()
    {
        var dir = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka");
        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    //безопасное получение ipv4 индекса адаптера без исключения на интерфейсах без стека ipv4
    private static int TryGetIpv4IfIndex(NetworkInterface nic)
    {
        try
        {
            return nic.Supports(NetworkInterfaceComponent.IPv4)
                ? nic.GetIPProperties().GetIPv4Properties().Index
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    //сбор индексов активных физических сетевых адаптеров хоста (без loopback, wintun и сторонних vpn)
    private static HashSet<int> GetPhysicalNetworkAdapterIndices(bool includeAllActive = false)
    {
        HashSet<int> targetIndices = [];

        var (_, physIfIndex) = GetDefaultGatewayInfo();
        if (physIfIndex > 0)
        {
            _ = targetIndices.Add(physIfIndex);
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up ||
                    nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    IsConflictingAdapter(nic) ||
                    nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isPhysicalType = nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.Wireless80211
                    or NetworkInterfaceType.GigabitEthernet;
                var props = nic.GetIPProperties();
                var hasNetworkActivity = props.DnsAddresses.Count > 0 || props.GatewayAddresses.Count > 0;
                if (!isPhysicalType && (!includeAllActive || !hasNetworkActivity))
                {
                    continue;
                }

                var idx = TryGetIpv4IfIndex(nic);
                if (idx > 0)
                {
                    _ = targetIndices.Add(idx);
                }
            }
            catch
            {
                // ignored
            }
        }

        return targetIndices;
    }

    //авто восстановление серверов dns адаптеров через нативный iphlpapi
    private static async Task AutoHealAdapterDnsAsync()
    {
        try
        {
            var targetIndices = GetPhysicalNetworkAdapterIndices();

            //сохраняем исходные dns настройки адаптеров перед лечением (если бекап еще не создан)
            await BackupAdapterDnsIfNeededAsync(targetIndices, GetDnsBackupPath()).ConfigureAwait(false);

            foreach (var ifGuid in targetIndices
                         .Select(static idx => Win32NetworkHelper.TryGetInterfaceGuidByIndex(idx, out var guid) ? (Guid?)guid : null)
                         .OfType<Guid>())
            {
                _ = Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, false, "77.88.8.8,1.1.1.1");
            }

            _ = Win32NetworkHelper.FlushDnsCache();
        }
        catch
        {
            // ignored
        }
    }

    //сохранение исходных dns серверов физических адаптеров перед заменой через iphlpapi
    private static async Task BackupAdapterDnsIfNeededAsync(HashSet<int> targetIndices, string? backupPath = null, bool includeIpv6 = false)
    {
        try
        {
            backupPath ??= GetDnsBackupPath();
            if (targetIndices.Count == 0 || File.Exists(backupPath))
            {
                return;
            }

            var lines = new List<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var idx = TryGetIpv4IfIndex(nic);
                if (idx <= 0 || !targetIndices.Contains(idx))
                {
                    continue;
                }

                if (!Guid.TryParse(nic.Id, out var ifGuid) && !Win32NetworkHelper.TryGetInterfaceGuidByIndex(idx, out ifGuid))
                {
                    continue;
                }

                var v4Configured = Win32NetworkHelper.GetInterfaceDnsServers(ifGuid, false);
                if (string.IsNullOrWhiteSpace(v4Configured))
                {
                    lines.Add($"ipv4|{idx}|{nic.Name}|DHCP");
                }
                else
                {
                    var v4Ips = v4Configured.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    lines.Add(v4Ips.Length > 0
                        ? $"ipv4|{idx}|{nic.Name}|{string.Join(',', v4Ips)}"
                        : $"ipv4|{idx}|{nic.Name}|DHCP");
                }

                if (!includeIpv6)
                {
                    continue;
                }

                var v6Configured = Win32NetworkHelper.GetInterfaceDnsServers(ifGuid, true);
                if (string.IsNullOrWhiteSpace(v6Configured))
                {
                    lines.Add($"ipv6|{idx}|{nic.Name}|DHCP");
                }
                else
                {
                    var v6Ips = v6Configured.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    lines.Add(v6Ips.Length > 0
                        ? $"ipv6|{idx}|{nic.Name}|{string.Join(',', v6Ips)}"
                        : $"ipv6|{idx}|{nic.Name}|DHCP");
                }
            }

            if (lines.Count > 0)
            {
                await File.WriteAllLinesAsync(backupPath, lines).ConfigureAwait(false);
            }
        }
        catch
        {
            // ignored
        }
    }

    //восстановление исходных dns серверов физических адаптеров через iphlpapi
    private static async Task RestoreAdapterDnsBackupAsync(string? backupPath = null)
    {
        try
        {
            backupPath ??= GetDnsBackupPath();
            if (!File.Exists(backupPath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(backupPath).ConfigureAwait(false);
            foreach (var parts in lines.Select(static line => line.Split('|')))
            {
                bool isIpv6;
                int ifIndex;
                string nicName;
                string dnsSpec;
                switch (parts.Length)
                {
                    case >= 4 when parts[0].Equals("ipv4", StringComparison.OrdinalIgnoreCase) ||
                                   parts[0].Equals("ipv6", StringComparison.OrdinalIgnoreCase):
                        isIpv6 = parts[0].Equals("ipv6", StringComparison.OrdinalIgnoreCase);
                        _ = int.TryParse(parts[1], CultureInfo.InvariantCulture, out ifIndex);
                        nicName = parts[2].Trim();
                        dnsSpec = parts[3].Trim();
                        break;
                    case >= 3:
                        isIpv6 = false;
                        _ = int.TryParse(parts[0], CultureInfo.InvariantCulture, out ifIndex);
                        nicName = parts[1].Trim();
                        dnsSpec = parts[2].Trim();
                        break;
                    default:
                        continue;
                }

                if (string.IsNullOrWhiteSpace(nicName) || string.IsNullOrWhiteSpace(dnsSpec))
                {
                    continue;
                }

                if (!TryResolveAdapterGuid(ifIndex, nicName, out var ifGuid))
                {
                    continue;
                }

                if (dnsSpec.Equals("DHCP", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, isIpv6, null);
                }
                else
                {
                    var ips = dnsSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(ip => IPAddress.TryParse(ip, out _))
                        .ToArray();
                    if (ips.Length > 0)
                    {
                        _ = Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, isIpv6, string.Join(',', ips));
                    }
                }
            }

            File.Delete(backupPath);
            _ = Win32NetworkHelper.FlushDnsCache();
        }
        catch
        {
            // ignored
        }
    }

    private static bool TryResolveAdapterGuid(int ifIndex, string nicName, out Guid ifGuid)
    {
        if (ifIndex > 0 && Win32NetworkHelper.TryGetInterfaceGuidByIndex(ifIndex, out ifGuid))
        {
            return true;
        }

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.Name.Equals(nicName, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(nic.Id, out ifGuid))
            {
                return true;
            }
        }

        ifGuid = Guid.Empty;
        return false;
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

    //проверка разрешения DNS
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
        var ifIndex = TryGetIpv4IfIndex(c);
        if (ifIndex > 0 && ifIndex == s_savedPhysicalIfIndex)
        {
            return false;
        }

        //0. системные переходные адаптеры windows ipv6 (teredo, isatap, 6to4) не являются сторонним vpn
        if (c.NetworkInterfaceType == NetworkInterfaceType.Tunnel &&
            (desc.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
             desc.Contains("ISATAP", StringComparison.OrdinalIgnoreCase) ||
             desc.Contains("6to4", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Contains("Teredo", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Contains("ISATAP", StringComparison.OrdinalIgnoreCase) ||
             c.Name.Contains("6to4", StringComparison.OrdinalIgnoreCase)))
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
                return gw is "0.0.0.0" or "::" || !IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any);
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
        if (mac.Length != 0 && (mac.Length != 6 || mac.Any(b => b != 0)))
        {
            return false;
        }

        var ipProps = c.GetIPProperties();
        return ipProps.UnicastAddresses.Count > 0;
    }

    //диагностика сетевых адаптеров
    private static void LogNetworkDiagnostics(Action<string>? onLogUpdated = null)
    {
        try
        {
            foreach (var c in NetworkInterface.GetAllNetworkInterfaces())
            {
                try
                {
                    var props = c.GetIPProperties();
                    var ips = string.Join(", ", props.UnicastAddresses.Select(u => $"{u.Address}/{u.IPv4Mask}"));
                    var gws = string.Join(", ", props.GatewayAddresses.Select(g => g.Address.ToString()));
                    var idx = TryGetIpv4IfIndex(c);
                    AppLogger.Log(
                        $"[NET-DIAG] Card: '{c.Name}' ({c.Description}), IfIndex: {idx}, Status: {c.OperationalStatus}, IPs: [{ips}], Gateways: [{gws}]");

                    if (!IsConflictingAdapter(c))
                    {
                        continue;
                    }

                    AppLogger.Log(
                        $"[CONFLICTING-VPN] Card '{c.Name}' ({c.Description}) is active. It may filter or conflict with Obxodka traffic.");
                    onLogUpdated?.Invoke(VpnStatusMessages.ThirdPartyVpnWarning(c.Name));
                }
                catch
                {
                    // ignored
                }
            }
        }
        catch
        {
            // ignored
        }
    }

    //включение защиты smhnr от утечки dns через другие интерфейсы
    private static void ApplySmhnrProtection()
    {
        try
        {
            using var key = Registry.LocalMachine.CreateSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient");

            var backupPath = GetSmhnrBackupPath();
            if (!File.Exists(backupPath))
            {
                var existing = key.GetValue("DisableSmartNameResolution");
                var origVal = existing is int intVal ? intVal : -1;
                s_savedDisableSmartNameResolution = origVal;
                File.WriteAllText(backupPath, origVal.ToString(CultureInfo.InvariantCulture));
            }
            else if (!s_savedDisableSmartNameResolution.HasValue &&
                     int.TryParse(File.ReadAllText(backupPath).Trim(), CultureInfo.InvariantCulture, out var persistedVal))
            {
                s_savedDisableSmartNameResolution = persistedVal;
            }

            key.SetValue("DisableSmartNameResolution", 1, RegistryValueKind.DWord);
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
            var backupPath = GetSmhnrBackupPath();
            var savedValue = s_savedDisableSmartNameResolution;
            if (!savedValue.HasValue && File.Exists(backupPath) &&
                int.TryParse(File.ReadAllText(backupPath).Trim(), CultureInfo.InvariantCulture, out var persistedVal))
            {
                savedValue = persistedVal;
            }

            if (!savedValue.HasValue)
            {
                return;
            }

            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient", true);
            if (key != null)
            {
                if (savedValue.Value == -1)
                {
                    key.DeleteValue("DisableSmartNameResolution", false);
                }
                else
                {
                    key.SetValue("DisableSmartNameResolution", savedValue.Value, RegistryValueKind.DWord);
                }
            }

            s_savedDisableSmartNameResolution = null;
            if (File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[SMHNR DISABLE ERROR] {ex.Message}");
        }
    }

    //проверка наличия назначенного глобального ipv6 адреса на адаптере туннеля
    private static bool AdapterHasIpv6Address(string adapterName)
    {
        try
        {
            var card = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase));
            return card?.GetIPProperties().UnicastAddresses.Any(u =>
                u.Address is { AddressFamily: AddressFamily.InterNetworkV6, IsIPv6LinkLocal: false } &&
                !IPAddress.IsLoopback(u.Address)) ?? false;
        }
        catch
        {
            return false;
        }
    }

    //запуск скрытого powershell исключительно для системного rpc nrpt (днс cache)
    private static async Task<(int exitCode, string output)> RunNrptPowerShellAsync(string command, int timeoutMs)
    {
        try
        {
            using var proc = new Process();
            proc.StartInfo = new ProcessStartInfo("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"")
            {
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            if (!proc.Start())
            {
                return (-1, "Failed to start powershell");
            }

            using var cts = new CancellationTokenSource(timeoutMs);
            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(cts.Token);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(true);
                }
                catch
                {
                    // ignored
                }

                return (-1, "PowerShell NRPT command timed out");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    //удаление правила nrpt политик разрешения имен obxodka
    private static async Task RemoveObxodkaNrptRulesAsync()
    {
        try
        {
            _ = await RunNrptPowerShellAsync(
                $"Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Comment -eq '{NrptRuleComment}' }} | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue",
                3500).ConfigureAwait(false);
        }
        catch
        {
            // ignored
        }

        try
        {
            using var policyRoot =
                Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig", true);
            if (policyRoot is not null)
            {
                foreach (var subName in policyRoot.GetSubKeyNames())
                {
                    using var sub = policyRoot.OpenSubKey(subName, false);
                    if (sub?.GetValue("Comment") is string comment &&
                        comment.Equals(NrptRuleComment, StringComparison.Ordinal))
                    {
                        policyRoot.DeleteSubKeyTree(subName, false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[DNS-LEAK NRPT CLEANUP WARN] {ex.Message}");
        }
    }

    //подмена ipv4 и ipv6 dns на физическом адаптере через iphlpapi
    private static void ApplyAdapterDnsOverrideNative(int ifIndex, bool includeIpv6)
    {
        if (!Win32NetworkHelper.TryGetInterfaceGuidByIndex(ifIndex, out var ifGuid))
        {
            return;
        }

        var v4Status = Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, false, $"{DnsDefaults.PrimaryDns},{DnsDefaults.SecondaryDns}");
        uint v6Status = 0;
        if (includeIpv6)
        {
            v6Status = Win32NetworkHelper.SetInterfaceDnsServers(ifGuid, true, $"{DnsDefaults.PrimaryDnsV6},{DnsDefaults.SecondaryDnsV6}");
        }

        if (v4Status != 0 || v6Status != 0)
        {
            AppLogger.LogWarning($"[DNS-LEAK NIC WARN] IfIndex {ifIndex}: v4={v4Status}, v6={v6Status}");
        }
    }

    //установка правила nrpt для маршрутизации всех dns-запросов строго на доверенные резолверы
    private static async Task InstallObxodkaNrptRuleAsync(string nrptServers)
    {
        var (code, output) = await RunNrptPowerShellAsync(
            $"Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Comment -eq '{NrptRuleComment}' }} | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue; Add-DnsClientNrptRule -Namespace '.' -NameServers {nrptServers} -Comment '{NrptRuleComment}' -ErrorAction Stop | Out-Null",
            15000).ConfigureAwait(false);
        if (code != 0)
        {
            AppLogger.LogWarning($"[DNS-LEAK NRPT WARN] exit={code}: {output.Trim()}");
        }
    }

    //активация маршрутов и политик dns в интерфейс туннеля для полной защиты от утечек ipv4/ipv6
    private static async Task EnableDnsLeakProtectionAsync(WintunAdapter adapter, string assignedIp)
    {
        _ = assignedIp;
        ApplySmhnrProtection();
        var (tunLuid, tunIfIndex) = ResolveAdapterIdentity(adapter);
        s_dnsLeakAdapterIfIndex = tunIfIndex;
        try
        {
            //шлюз vpn сервера в подсети 100.64.0.0/10
            const string tunGateway = "100.64.0.1";
            foreach (var dns in DnsDefaults.TrustedDnsServers)
            {
                _ = Win32NetworkHelper.AddIpv4Route(dns, "255.255.255.255", tunGateway, tunIfIndex);
            }

            var blockIpv6 = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
            var hasTunV6 = !blockIpv6 && (AdapterHasIpv6Address(adapter.Name) || HasActiveIpv6Connectivity());
            if (hasTunV6 && tunIfIndex > 0)
            {
                foreach (var dnsV6 in DnsDefaults.TunnelDnsServersIPv6)
                {
                    _ = Win32NetworkHelper.AddIpv6Route(dnsV6, 128, tunLuid, tunIfIndex, 1);
                }
            }

            var physIndices = GetPhysicalNetworkAdapterIndices(true);
            await BackupAdapterDnsIfNeededAsync(physIndices, GetVpnDnsLeakBackupPath(), true).ConfigureAwait(false);
            foreach (var idx in physIndices)
            {
                ApplyAdapterDnsOverrideNative(idx, hasTunV6);
            }

            var nrptServers = hasTunV6
                ? $"'{DnsDefaults.PrimaryDns}','{DnsDefaults.SecondaryDns}','{DnsDefaults.PrimaryDnsV6}','{DnsDefaults.SecondaryDnsV6}'"
                : $"'{DnsDefaults.PrimaryDns}','{DnsDefaults.SecondaryDns}'";
            await InstallObxodkaNrptRuleAsync(nrptServers).ConfigureAwait(false);

            _ = Win32NetworkHelper.FlushDnsCache();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DNS-LEAK ERROR] {ex.Message}");
        }
    }

    //удаление защитных маршрутов, nrpt-правила и восстановление исходных dns на физических адаптерах
    private static async Task DisableDnsLeakProtectionAsync()
    {
        RestoreSmhnrProtection();
        try
        {
            await Task.WhenAll(
                RemoveObxodkaNrptRulesAsync(),
                RestoreAdapterDnsBackupAsync(GetVpnDnsLeakBackupPath())).ConfigureAwait(false);

            Win32NetworkHelper.DeleteIpv4Routes(DnsDefaults.TrustedDnsServers.Select(dns => (dns, "255.255.255.255")));

            var targetIfIndex = s_dnsLeakAdapterIfIndex;
            s_dnsLeakAdapterIfIndex = 0;
            var v6Targets = DnsDefaults.TunnelDnsServersIPv6.Select(dnsV6 => (dnsV6, (byte)128)).ToArray();
            Win32NetworkHelper.DeleteIpv6Routes(targetIfIndex > 0 ? targetIfIndex : null, v6Targets);

            _ = Win32NetworkHelper.FlushDnsCache();
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DNS-LEAK CLEANUP ERROR] {ex.Message}");
        }
    }
}
