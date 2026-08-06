namespace obxodka.Platforms.Windows;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class WindowsVpnService : IVpnService, IDisposable
{
    private CancellationTokenSource? _cts;
    private WintunAdapter? _adapter;
    public AppVpnState CurrentState { get; private set; } = AppVpnState.Disconnected;
    public bool IsRunning => CurrentState == AppVpnState.Connected;
    public event Action<AppVpnState>? OnStateChanged;
    public event Action<string>? OnLogUpdated;
    public event Action<string>? OnErrorOccurred;
#pragma warning disable CS0067
    public event Action<AppTrafficStats>? OnTrafficUpdated;
#pragma warning restore CS0067
    public event Action<string>? OnForceLogoutRequested;
    public WindowsVpnService()
    {
        AppDomain.CurrentDomain.ProcessExit += (s, e) => StopVpnAsync().GetAwaiter().GetResult();
        OctopusEngine.OnCertificateRevoked += (msg) => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
        _ = CleanupStaleRoutesAsync();
    }
    private static async Task CleanupStaleRoutesAsync()
    {
        _ = await RunCmdAsync("powershell", "-NoProfile -Command \"Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'ObxVPN*' -or $_.Name -like 'Obxodka*' } | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue\"");
        _ = await RunCmdAsync("route", "delete 0.0.0.0 mask 128.0.0.0");
        _ = await RunCmdAsync("route", "delete 128.0.0.0 mask 128.0.0.0");
    }
    private string _currentServerIp = "";
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;

    private void HandleDeadConnection()
    {
        if (IsRunning && !_isExplicitlyStopped)
        {
            _ = Task.Run(async () =>
            {
                await StopVpnAsync();
                UpdateState(AppVpnState.Error);
                OnErrorOccurred?.Invoke("Сервер отключил соединение.");
            });
        }
    }

    private void HandleEngineDrop()
    {
        if (IsRunning && !_isExplicitlyStopped)
        {
            UpdateState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(2000);
                    if (_isExplicitlyStopped)
                    {
                        return;
                    }
                    try
                    {
                        var gw = await GetDefaultGatewayAsync();
                        if (!string.IsNullOrEmpty(gw) && !string.IsNullOrEmpty(_currentServerIp))
                        {
                            _ = await RunCmdAsync("route", $"delete {_currentServerIp} mask 255.255.255.255");
                            var (exitCode, output) = await RunCmdAsync("route", $"add {_currentServerIp} mask 255.255.255.255 {gw} metric 1");
                            await OctopusEngine.Current.ConnectAsync(_currentServerIp, _currentServerPort);
                            UpdateState(AppVpnState.Connected);
                            return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RECONNECT] Attempt {i + 1} failed: {ex.Message}");
                    }
                }
                await StopVpnAsync();
                OnErrorOccurred?.Invoke("Связь с сервером потеряна. Не удалось восстановить подключение.");
            });
        }
    }
    public async Task StartVpnAsync(string serverIp, int serverPort)
    {
        var targetIp = serverIp;
        if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
        {
            try
            {
                var ips = await Dns.GetHostAddressesAsync(serverIp);
                if (ips.Length > 0)
                {
                    targetIp = ips.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
            }
        }

        var ip = OctopusEngine.Current.AssignedIp;
        var ipv6 = OctopusEngine.Current.AssignedIpV6;

        if (!IPAddress.TryParse(targetIp, out _) ||
            !IPAddress.TryParse(ip, out _) ||
            !IPAddress.TryParse(ipv6, out _))
        {
            throw new InvalidOperationException("Получены некорректные IP-адреса от сервера. Возможное вмешательство MITM.");
        }

        var originalHost = serverIp;
        if (Uri.CheckHostName(serverIp) != UriHostNameType.Dns)
        {
            try
            {
                originalHost = new Uri(AppConfig.ApiBaseUrl).Host;
            }
            catch { }
        }
        _currentServerIp = targetIp;
        _currentServerPort = serverPort;
        _isExplicitlyStopped = false;
        try
        {
            UpdateState(AppVpnState.Connecting);
            OnLogUpdated?.Invoke($"Построение маршрута через {originalHost}...");

            var connected = false;
            Exception? lastException = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    if (attempt > 1)
                    {
                        OnLogUpdated?.Invoke($"Переподключение ({attempt}/3)...");
                    }
                    OnLogUpdated?.Invoke($"Подключение к {targetIp}:{serverPort}...");

                    await OctopusEngine.Current.ConnectAsync(targetIp, serverPort);
                    connected = true;
                    break;
                }
                catch (UnauthorizedAccessException ex)
                {
                    lastException = ex;
                    break;
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    if (attempt < 3)
                    {
                        await Task.Delay(2000);
                    }
                }
            }

            if (!connected && lastException != null)
            {
                throw lastException;
            }

            _cts = new CancellationTokenSource();
            ip = OctopusEngine.Current.AssignedIp;
            ipv6 = OctopusEngine.Current.AssignedIpV6;
            OnLogUpdated?.Invoke($"Получен IP: {ip}");
            OnLogUpdated?.Invoke("Инициализация виртуального адаптера Wintun...");
            var uniqueName = "ObxodkaVpn";
            var adapter = await Task.Run(() => new WintunAdapter(uniqueName, "Wintun", Guid.NewGuid()));
            _adapter = adapter;
            OnLogUpdated?.Invoke($"Запуск адаптера ({adapter.Name})...");
            adapter.StartSession();
            OnLogUpdated?.Invoke("Применение настроек сети...");
            await SetAdapterConfigAsync(adapter.Name, ip, ipv6, "255.255.255.0");
            OnLogUpdated?.Invoke("Перенаправление трафика в туннель...");
            await SetWindowsRoutesAsync(adapter.Name, targetIp, ip, true);
            OnLogUpdated?.Invoke("Защищенное соединение установлено!");
            _ = Task.Run(() => ProcessTrafficAsync(_cts.Token));
            UpdateState(AppVpnState.Connected);
        }
        catch (Exception ex)
        {
            UpdateState(AppVpnState.Error);
            if (ex.Message.Contains("timed out", StringComparison.OrdinalIgnoreCase))
            {
                OnErrorOccurred?.Invoke("Сервер недоступен (Timeout).");
            }
            else
            {
                OnErrorOccurred?.Invoke($"Ошибка: {ex.Message}");
            }
        }
    }
    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        OctopusEngine.Current.OnPacketReceived += HandlePacketFromVpn;
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = "Wintun-UploadReader";
            var batch = new PacketBatch();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var adapter = _adapter;
                    if (adapter == null)
                    {
                        break;
                    }
                    var count = adapter.ReceiveBatch(batch, ct);
                    for (var i = 0; i < count; i++)
                    {
                        var (buf, len) = batch[i];
                        _ = OctopusEngine.Current.SendPacketFromPoolAsync(buf, len);
                    }
                }
            }
            catch { }
            finally { _ = tcs.TrySetResult(); }
        })
        {
            IsBackground = true
        };
        thread.Start();
        await tcs.Task;
    }
    private void HandlePacketFromVpn(byte[] data)
    {
        _rxCount++;
        var info = ParseIpPacket(data);
        if (_rxCount % 10000 == 0)
        {
            Debug.WriteLine($"[TRAFFIC IN] {_rxCount}: {info} ({data.Length} bytes)");
        }
        _adapter?.SendPacket(data);
    }
    private long _rxCount;
    private static string ParseIpPacket(byte[] data)
    {
        if (data.Length < 20)
        {
            return "UNKNOWN (Too small)";
        }
        var version = data[0] >> 4;
        if (version == 4)
        {
            var protocol = data[9];
            var src = $"{data[12]}.{data[13]}.{data[14]}.{data[15]}";
            var dst = $"{data[16]}.{data[17]}.{data[18]}.{data[19]}";
            var protoName = protocol switch { 1 => "ICMP", 6 => "TCP", 17 => "UDP", _ => $"Proto{protocol}" };
            return $"{protoName} {src} -> {dst}";
        }
        return version == 6 ? "IPv6 Packet" : $"IPv{version} Packet";
    }
    private void UpdateState(AppVpnState state)
    {
        CurrentState = state;
        OnStateChanged?.Invoke(state);
    }
    private static async Task SetAdapterConfigAsync(string adapterName, string ip, string ipv6, string mask)
    {
        var lastError = "";
        for (var i = 0; i < 40; i++)
        {
            var pfx = mask == "255.192.0.0" ? 10 : 24;
            var psScript = $@"
                $ErrorActionPreference = 'Stop';
                $adapter = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*{adapterName}*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.Name -like '*Wintun*' }} | Select-Object -First 1;
                if (-not $adapter) {{ Write-Output (Get-NetAdapter -ErrorAction SilentlyContinue | Select-Object Name, InterfaceDescription | Out-String); exit 1; }}
                try {{ New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress '{ip}' -PrefixLength {pfx} -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress '{ipv6}' -PrefixLength 64 -AddressFamily IPv6 -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -NlMtuBytes 1420 -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses '8.8.8.8','8.8.4.4' -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Enable-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | Out-Null }} catch {{ }}
            ";
            var (exitCode, output) = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\n", " ").Replace("\r", "")}\"");
            Debug.WriteLine($"[NET CONFIG] Attempt {i}, ExitCode: {exitCode}, Output: {output}");
            if (exitCode == 0 && !output.Contains("Adapter not found"))
            {
                Debug.WriteLine("[NET CONFIG] Success!");
                return;
            }
            lastError = output.Length > 200 ? string.Concat(output.AsSpan(0, 200), "...") : output;
            await Task.Delay(500);
        }
        throw new InvalidOperationException($"Не удалось настроить адаптер '{adapterName}'. Ошибка PS: {lastError}");
    }
    private static async Task<(int exitCode, string output)> RunCmdAsync(string fileName, string args)
    {
        var tcs = new TaskCompletionSource<(int, string)>();
        _ = Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, args)
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    tcs.SetResult((-1, "Failed to start cmd.exe"));
                    return;
                }
                var err = proc.StandardError.ReadToEnd();
                var std = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit();
                tcs.SetResult((proc.ExitCode, string.IsNullOrWhiteSpace(err) ? std : err));
            }
            catch (Exception ex)
            {
                tcs.SetResult((-1, ex.Message));
            }
        });
        return await tcs.Task;
    }
    private static async Task<string> GetDefaultGatewayAsync()
    {
        try
        {
            var (exitCode, output) = await RunCmdAsync("powershell", "-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1).NextHop\"");
            var gw = output.Trim();
            if (string.IsNullOrEmpty(gw))
            {
                var card = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !n.Name.Contains("Obxodka") &&
                    !n.Name.Contains("Radmin") &&
                    n.GetIPProperties().GatewayAddresses.Count != 0);
                return card?.GetIPProperties().GatewayAddresses.FirstOrDefault()?.Address.ToString() ?? "";
            }
            return gw;
        }
        catch
        {
            return "";
        }
    }
    private static async Task SetWindowsRoutesAsync(string adapterName, string serverIp, string assignedIp, bool enable)
    {
        var gw = await GetDefaultGatewayAsync();
        Debug.WriteLine($"[ROUTE] Default Gateway: {gw}, Name: {adapterName}, ServerIP: {serverIp}, Enable: {enable}");
        if (enable && !string.IsNullOrEmpty(adapterName))
        {
            var idxResult = await RunCmdAsync("powershell",
                $"-NoProfile -Command \"(Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue).ifIndex\"");
            var ifIndex = idxResult.output.Trim();
            Debug.WriteLine($"[ROUTE] Wintun ifIndex: '{ifIndex}'");
            if (string.IsNullOrEmpty(ifIndex))
            {
                idxResult = await RunCmdAsync("powershell",
                    $"-NoProfile -Command \"(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*{adapterName}*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.Name -like '*Wintun*' }} | Select-Object -First 1).ifIndex\"");
                ifIndex = idxResult.output.Trim();
                Debug.WriteLine($"[ROUTE] Wintun ifIndex (fallback): '{ifIndex}'");
            }
            if (!string.IsNullOrEmpty(gw) && !string.IsNullOrEmpty(serverIp))
            {
                _ = await RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255");
                var (exitCode, output) = await RunCmdAsync("route", $"add {serverIp} mask 255.255.255.255 {gw} metric 1");
                Debug.WriteLine($"[ROUTE] Add Server Route: ExitCode {exitCode}, Output: {output}");
            }
            if (!string.IsNullOrEmpty(ifIndex))
            {
                await Task.Delay(200);
                var (exitCode, output) = await RunCmdAsync("route", $"add 0.0.0.0 mask 128.0.0.0 {assignedIp} metric 1 if {ifIndex}");
                var r3 = await RunCmdAsync("route", $"add 128.0.0.0 mask 128.0.0.0 {assignedIp} metric 1 if {ifIndex}");
                Debug.WriteLine($"[ROUTE] Add IPv4 Tun Routes: R2={exitCode} ({output}), R3={r3.exitCode} ({r3.output})");
                var psV6 = $@"
                    $idx = {ifIndex};
                    try {{ Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '::/1' -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
                    try {{ Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '8000::/1' -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
                    try {{ New-NetRoute -InterfaceIndex $idx -DestinationPrefix '::/1' -NextHop 'fd00::1' -RouteMetric 1 -ErrorAction Stop | Out-Null }} catch {{ }}
                    try {{ New-NetRoute -InterfaceIndex $idx -DestinationPrefix '8000::/1' -NextHop 'fd00::1' -RouteMetric 1 -ErrorAction Stop | Out-Null }} catch {{ }}
                    Write-Output 'OK'
                ".Replace("\r", "").Replace("\n", " ");
                var r4 = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psV6}\"");
                Debug.WriteLine($"[ROUTE] Add IPv6 Routes: {r4.exitCode} ({r4.output})");
            }
            else
            {
                Debug.WriteLine("[ROUTE] WARNING: Could not find Wintun adapter ifIndex! Routes NOT added.");
            }
        }
        else if (!enable)
        {
            var deleteTasks = new List<Task>
            {
                RunCmdAsync("route", "delete 0.0.0.0 mask 128.0.0.0"),
                RunCmdAsync("route", "delete 128.0.0.0 mask 128.0.0.0")
            };
            if (!string.IsNullOrEmpty(serverIp))
            {
                deleteTasks.Add(RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255"));
            }
            if (!string.IsNullOrEmpty(adapterName))
            {
                var psDelV6 = $@"
                    $idx = (Get-NetAdapter -Name '{adapterName}' -ErrorAction SilentlyContinue | Select-Object -First 1).ifIndex;
                    if ($idx) {{
                        try {{ Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '::/1' -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
                        try {{ Remove-NetRoute -InterfaceIndex $idx -DestinationPrefix '8000::/1' -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
                    }}
                    Write-Output 'OK'
                ";
                deleteTasks.Add(RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psDelV6.Replace("\n", " ").Replace("\r", "")}\""));
            }
            await Task.WhenAll(deleteTasks).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
    }
    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;
        UpdateState(AppVpnState.Disconnecting);
        _cts?.Cancel();
        OnLogUpdated?.Invoke("[SYSTEM] VPN отключён.");

        var adapterToDispose = _adapter;
        var adapterName = _adapter?.Name ?? "";
        var serverIp = _currentServerIp;
        _adapter = null;

        try
        {
            if (adapterToDispose != null)
            {
                try
                { adapterToDispose.Dispose(); }
                catch { }
                Debug.WriteLine("[DRIVER] Wintun adapter disposed.");
            }
            await SetWindowsRoutesAsync(adapterName, serverIp, "", false);
            await OctopusEngine.Current.DisposeAsync();
            Debug.WriteLine("[SYSTEM] VPN cleanup complete.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STOP ERROR] {ex.Message}");
        }

        UpdateState(AppVpnState.Disconnected);
    }
    public void Dispose()
    {
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        _ = StopVpnAsync();
        _cts?.Dispose();
    }
}
