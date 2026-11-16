using Microsoft.Win32;

namespace obxodka.Platforms.Windows;

//диагностика и настройка сетевых интерфейсов, защита от утечек dns
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService
{
    private static int? s_savedDisableSmartNameResolution;
    private static volatile string? s_dnsLeakAdapterName;

    private static string GetAppDataFolderPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Obxodka");
        _ = Directory.CreateDirectory(dir);
        return dir;
    }

    private static string GetSmhnrBackupPath() => Path.Combine(GetAppDataFolderPath(), "smhnr_backup.txt");

    private static string GetDnsBackupPath() => Path.Combine(GetAppDataFolderPath(), "dns_backup.txt");

    private static string GetVpnDnsLeakBackupPath() => Path.Combine(GetAppDataFolderPath(), "vpn_dns_leak_backup.txt");

    //предстартовая проверка сети и dns
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
                //прямой ip уже не отвечает при рабочем интернете — сразу штрафуем его, чтобы стартовать с cloudflare без 12с простоя
                OctopusEngine.MarkNodeByteCapped(AppConfig.DirectServerIp);
            }
            OctopusEngine.Current.EmitStatus(VpnStatusMessages.MainNodeUnavailableDirectly);
        }

        if (!dnsOk)
        {
            OctopusEngine.Current.EmitStatus(VpnStatusMessages.DnsFailureDetected);
            await AutoHealAdapterDnsAsync().ConfigureAwait(false);
            dnsOk = await ProbeSystemDnsAsync("ya.ru", 2500).ConfigureAwait(false);
            if (dnsOk)
            {
                OctopusEngine.Current.EmitStatus(VpnStatusMessages.DnsRestoredSuccessfully);
            }
            else
            {
                OctopusEngine.Current.EmitStatus(VpnStatusMessages.SwitchingToAutonomousDns);
            }
        }
        else if (internetOk || serverOk)
        {
            OctopusEngine.Current.EmitStatus(VpnStatusMessages.InternetAndDnsHealthy);
        }

        return null;
    }

    //безопасное получение ipv4 индекса адаптера без исключения на интерфейсах без стека ipv4
    private static int TryGetIpv4IfIndex(NetworkInterface nic)
    {
        try
        {
            return nic.Supports(NetworkInterfaceComponent.IPv4)
                ? (nic.GetIPProperties().GetIPv4Properties()?.Index ?? -1)
                : -1;
        }
        catch
        {
            return -1;
        }
    }

    //сбор активных физических сетевых адаптеров хоста (без loopback, wintun и сторонних vpn)
    private static (HashSet<int> Indices, HashSet<string> Names) GetPhysicalNetworkAdapterTargets(bool includeAllActive = false)
    {
        HashSet<int> targetIndices = [];
        HashSet<string> targetNames = [with(StringComparer.OrdinalIgnoreCase)];

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

                if (!string.IsNullOrWhiteSpace(nic.Name))
                {
                    _ = targetNames.Add(nic.Name);
                }
            }
            catch { }
        }

        return (targetIndices, targetNames);
    }

    //автовосстановление серверов dns адаптеров
    private static async Task AutoHealAdapterDnsAsync()
    {
        try
        {
            var (targetIndices, targetNames) = GetPhysicalNetworkAdapterTargets();

            //сохраняем исходные dns настройки адаптеров перед лечением (если бекап еще не создан)
            await BackupAdapterDnsIfNeededAsync(targetIndices, GetDnsBackupPath(), includeIpv6: false).ConfigureAwait(false);

            foreach (var idx in targetIndices)
            {
                _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses '77.88.8.8','1.1.1.1' -ErrorAction Stop }} catch {{ }}\"").ConfigureAwait(false);
            }

            foreach (var name in targetNames)
            {
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{name}\" static 77.88.8.8 primary validate=no").ConfigureAwait(false);
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{name}\" 1.1.1.1 index=2 validate=no").ConfigureAwait(false);
            }

            _ = await RunCmdAsync("ipconfig", "/flushdns").ConfigureAwait(false);
        }
        catch { }
    }

    //сохранение исходных dns серверов физических адаптеров перед заменой
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

                var props = nic.GetIPProperties();
                var idx = TryGetIpv4IfIndex(nic);
                if (idx <= 0 || !targetIndices.Contains(idx))
                {
                    continue;
                }

                var (_, dnsOut) = await RunCmdAsync("netsh", $"interface ipv4 show dnsservers name=\"{nic.Name}\"", timeoutMs: 2500).ConfigureAwait(false);
                if (dnsOut.Contains("DHCP", StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add($"ipv4|{idx}|{nic.Name}|DHCP");
                }
                else
                {
                    var v4Dns = props.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        .Select(a => a.ToString())
                        .ToArray();
                    if (v4Dns.Length > 0)
                    {
                        lines.Add($"ipv4|{idx}|{nic.Name}|{string.Join(',', v4Dns)}");
                    }
                }

                if (includeIpv6)
                {
                    var (_, dnsOutV6) = await RunCmdAsync("netsh", $"interface ipv6 show dnsservers name=\"{nic.Name}\"", timeoutMs: 2500).ConfigureAwait(false);
                    var v6Static = props.DnsAddresses
                        .Where(a => a.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a) && !a.IsIPv6SiteLocal)
                        .Select(a => a.ToString())
                        .ToArray();
                    if (dnsOutV6.Contains("DHCP", StringComparison.OrdinalIgnoreCase) || v6Static.Length == 0)
                    {
                        lines.Add($"ipv6|{idx}|{nic.Name}|DHCP");
                    }
                    else
                    {
                        lines.Add($"ipv6|{idx}|{nic.Name}|{string.Join(',', v6Static)}");
                    }
                }
            }

            if (lines.Count > 0)
            {
                await File.WriteAllLinesAsync(backupPath, lines).ConfigureAwait(false);
            }
        }
        catch { }
    }

    //восстановление исходных dns серверов физических адаптеров
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
            foreach (var line in lines)
            {
                var parts = line.Split('|');
                string family;
                string nicName;
                string dnsSpec;
                if (parts.Length >= 4 && (parts[0].Equals("ipv4", StringComparison.OrdinalIgnoreCase) || parts[0].Equals("ipv6", StringComparison.OrdinalIgnoreCase)))
                {
                    family = parts[0].ToLowerInvariant();
                    nicName = parts[2].Trim();
                    dnsSpec = parts[3].Trim();
                }
                else if (parts.Length >= 3)
                {
                    family = "ipv4";
                    nicName = parts[1].Trim();
                    dnsSpec = parts[2].Trim();
                }
                else
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(nicName) || string.IsNullOrWhiteSpace(dnsSpec))
                {
                    continue;
                }

                if (dnsSpec.Equals("DHCP", StringComparison.OrdinalIgnoreCase))
                {
                    _ = await RunCmdAsync("netsh", $"interface {family} set dnsservers name=\"{nicName}\" dhcp", timeoutMs: 2500).ConfigureAwait(false);
                }
                else
                {
                    var ips = dnsSpec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    if (ips.Length > 0 && IPAddress.TryParse(ips[0], out _))
                    {
                        _ = await RunCmdAsync("netsh", $"interface {family} set dnsservers name=\"{nicName}\" static {ips[0]} primary validate=no", timeoutMs: 2500).ConfigureAwait(false);
                        for (var i = 1; i < ips.Length; i++)
                        {
                            if (IPAddress.TryParse(ips[i], out _))
                            {
                                _ = await RunCmdAsync("netsh", $"interface {family} add dnsservers name=\"{nicName}\" {ips[i]} index={i + 1} validate=no", timeoutMs: 2500).ConfigureAwait(false);
                            }
                        }
                    }
                }
            }

            File.Delete(backupPath);
            _ = await RunCmdAsync("ipconfig", "/flushdns", timeoutMs: 2000).ConfigureAwait(false);
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
                try
                {
                    var props = c.GetIPProperties();
                    var ips = string.Join(", ", props.UnicastAddresses.Select(u => $"{u.Address}/{u.IPv4Mask}"));
                    var gws = string.Join(", ", props.GatewayAddresses.Select(g => g.Address.ToString()));
                    var idx = TryGetIpv4IfIndex(c);
                    AppLogger.Log($"[NET-DIAG] Card: '{c.Name}' ({c.Description}), IfIndex: {idx}, Status: {c.OperationalStatus}, IPs: [{ips}], Gateways: [{gws}]");

                    if (IsConflictingAdapter(c))
                    {
                        AppLogger.Log($"[CONFLICTING-VPN] Card '{c.Name}' ({c.Description}) is active. It may filter or conflict with Obxodka traffic.");
                        onLogUpdated?.Invoke(VpnStatusMessages.ThirdPartyVpnWarning(c.Name));
                    }
                }
                catch { }
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

    private const string NrptRuleComment = "ObxodkaVpnDns";

    //проверка наличия назначенного глобального ipv6 адреса на адаптере туннеля
    private static bool AdapterHasIpv6Address(string adapterName)
    {
        try
        {
            var card = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                n.Name.Equals(adapterName, StringComparison.OrdinalIgnoreCase) ||
                n.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase));
            return card?.GetIPProperties().UnicastAddresses.Any(u =>
                u.Address.AddressFamily == AddressFamily.InterNetworkV6 &&
                !u.Address.IsIPv6LinkLocal &&
                !IPAddress.IsLoopback(u.Address)) ?? false;
        }
        catch
        {
            return false;
        }
    }

    //удаление правила nrpt политик разрешения имен obxodka
    private static async Task RemoveObxodkaNrptRulesAsync()
    {
        try
        {
            _ = await RunCmdAsync(
                "powershell",
                $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Comment -eq '{NrptRuleComment}' }} | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue\"",
                timeoutMs: 3500).ConfigureAwait(false);
        }
        catch { }

        try
        {
            using var policyRoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters\DnsPolicyConfig", writable: true);
            if (policyRoot is not null)
            {
                foreach (var subName in policyRoot.GetSubKeyNames())
                {
                    using var sub = policyRoot.OpenSubKey(subName, writable: false);
                    if (sub?.GetValue("Comment") is string comment &&
                        comment.Equals(NrptRuleComment, StringComparison.Ordinal))
                    {
                        policyRoot.DeleteSubKeyTree(subName, throwOnMissingSubKey: false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[DNS-LEAK NRPT CLEANUP WARN] {ex.Message}");
        }
    }

    //последовательная подмена ipv4 и ipv6 dns на физическом адаптере
    private static async Task ApplyAdapterDnsOverrideAsync(string nicName, bool includeIpv6 = true)
    {
        var (v4SetCode, v4SetOut) = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{nicName}\" static {DnsDefaults.PrimaryDns} primary validate=no", timeoutMs: 3000).ConfigureAwait(false);
        var (v4AddCode, v4AddOut) = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{nicName}\" {DnsDefaults.SecondaryDns} index=2 validate=no", timeoutMs: 3000).ConfigureAwait(false);
        var v6SetCode = 0;
        var v6SetOut = "";
        var v6AddCode = 0;
        var v6AddOut = "";
        if (includeIpv6)
        {
            (v6SetCode, v6SetOut) = await RunCmdAsync("netsh", $"interface ipv6 set dnsservers name=\"{nicName}\" static {DnsDefaults.PrimaryDnsV6} primary validate=no", timeoutMs: 3000).ConfigureAwait(false);
            (v6AddCode, v6AddOut) = await RunCmdAsync("netsh", $"interface ipv6 add dnsservers name=\"{nicName}\" {DnsDefaults.SecondaryDnsV6} index=2 validate=no", timeoutMs: 3000).ConfigureAwait(false);
        }
        if (v4SetCode != 0 || v4AddCode != 0 || v6SetCode != 0 || v6AddCode != 0)
        {
            AppLogger.LogWarning($"[DNS-LEAK NIC WARN] '{nicName}': v4=({v4SetCode},{v4AddCode}) {v4SetOut.Trim()} {v4AddOut.Trim()} | v6=({v6SetCode},{v6AddCode}) {v6SetOut.Trim()} {v6AddOut.Trim()}");
        }
    }

    //установка правила nrpt для маршрутизации всех dns-запросов строго на доверенные резолверы
    private static async Task InstallObxodkaNrptRuleAsync(string nrptServers)
    {
        var (code, output) = await RunCmdAsync(
            "powershell",
            $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-DnsClientNrptRule -ErrorAction SilentlyContinue | Where-Object {{ $_.Comment -eq '{NrptRuleComment}' }} | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue; Add-DnsClientNrptRule -Namespace '.' -NameServers {nrptServers} -Comment '{NrptRuleComment}' -ErrorAction Stop | Out-Null\"",
            timeoutMs: 15000).ConfigureAwait(false);
        if (code != 0)
        {
            AppLogger.LogWarning($"[DNS-LEAK NRPT WARN] exit={code}: {output.Trim()}");
        }
    }

    //активация маршрутов и политик dns в интерфейс туннеля для полной защиты от утечек ipv4/ipv6
    private static async Task EnableDnsLeakProtectionAsync(string adapterName, string assignedIp)
    {
        _ = assignedIp;
        ApplySmhnrProtection();
        s_dnsLeakAdapterName = adapterName;
        try
        {
            var ifIndex = GetWintunInterfaceIndex(adapterName);
            var ifArg = ifIndex > 0 ? $" if {ifIndex}" : "";
            //шлюз vpn сервера в подсети 100.64.0.0/10
            const string tunGateway = "100.64.0.1";
            var tasks = new List<Task>();

            foreach (var dns in DnsDefaults.TrustedDnsServers)
            {
                tasks.Add(RunCmdAsync("route", $"add {dns} mask 255.255.255.255 {tunGateway} metric 1{ifArg}", timeoutMs: 2500));
            }

            var blockIpv6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
            var hasTunV6 = !blockIpv6 && (AdapterHasIpv6Address(adapterName) || HasActiveIpv6Connectivity());
            if (hasTunV6)
            {
                foreach (var dnsV6 in DnsDefaults.TunnelDnsServersIPv6)
                {
                    tasks.Add(RunCmdAsync("netsh", $"interface ipv6 add route {dnsV6}/128 interface=\"{adapterName}\" metric=1 store=active", timeoutMs: 2500));
                }
            }

            var (physIndices, physNames) = GetPhysicalNetworkAdapterTargets(includeAllActive: true);
            await BackupAdapterDnsIfNeededAsync(physIndices, GetVpnDnsLeakBackupPath(), includeIpv6: true).ConfigureAwait(false);
            foreach (var nicName in physNames)
            {
                tasks.Add(ApplyAdapterDnsOverrideAsync(nicName, hasTunV6));
            }

            var nrptServers = hasTunV6
                ? $"'{DnsDefaults.PrimaryDns}','{DnsDefaults.SecondaryDns}','{DnsDefaults.PrimaryDnsV6}','{DnsDefaults.SecondaryDnsV6}'"
                : $"'{DnsDefaults.PrimaryDns}','{DnsDefaults.SecondaryDns}'";
            tasks.Add(InstallObxodkaNrptRuleAsync(nrptServers));

            await Task.WhenAll(tasks).ConfigureAwait(false);
            _ = DnsFlushResolverCache();
            _ = await RunCmdAsync("ipconfig", "/flushdns", timeoutMs: 2000).ConfigureAwait(false);
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
            var tasks = new List<Task>
            {
                RemoveObxodkaNrptRulesAsync(),
                RestoreAdapterDnsBackupAsync(GetVpnDnsLeakBackupPath())
            };

            foreach (var dns in DnsDefaults.TrustedDnsServers)
            {
                tasks.Add(RunCmdAsync("route", $"delete {dns} mask 255.255.255.255", timeoutMs: 1500));
            }

            var targetAdapter = s_dnsLeakAdapterName;
            s_dnsLeakAdapterName = null;
            foreach (var dnsV6 in DnsDefaults.TunnelDnsServersIPv6)
            {
                if (!string.IsNullOrWhiteSpace(targetAdapter))
                {
                    tasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route {dnsV6}/128 interface=\"{targetAdapter}\"", timeoutMs: 1500));
                }
                else
                {
                    tasks.Add(RunCmdAsync(
                        "powershell",
                        $"-NoProfile -ExecutionPolicy Bypass -Command \"Get-NetRoute -AddressFamily IPv6 -DestinationPrefix '{dnsV6}/128' -ErrorAction SilentlyContinue | Where-Object {{ $_.InterfaceAlias -like '*Obxodka*' -or $_.InterfaceAlias -like '*Wintun*' }} | Remove-NetRoute -Confirm:$false -ErrorAction SilentlyContinue\"",
                        timeoutMs: 2000));
                }
            }

            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            _ = DnsFlushResolverCache();
            _ = await RunCmdAsync("ipconfig", "/flushdns", timeoutMs: 2000).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[DNS-LEAK CLEANUP ERROR] {ex.Message}");
        }
    }
}
