using Uri = System.Uri;

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
    public event Action<AppTrafficStats>? OnTrafficUpdated = delegate { };
    public event Action<string>? OnForceLogoutRequested;

    private string _currentServerIp = "";
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;
    private static bool t_networkSettingsBoosted;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;
    private readonly SemaphoreSlim _vpnGate = new(1, 1);

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateUnbounded<(byte[] buffer, int length)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public WindowsVpnService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => StopVpnAsync().GetAwaiter().GetResult();
        OctopusEngine.OnCertificateRevoked += (msg) => OnForceLogoutRequested?.Invoke(msg);
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        OctopusEngine.Current.OnDeadConnectionDetected += HandleDeadConnection;
    }

    private static async Task CleanupStaleRoutesAsync()
    {
        _ = await RunCmdAsync("route", "delete 0.0.0.0 mask 128.0.0.0");
        _ = await RunCmdAsync("route", "delete 128.0.0.0 mask 128.0.0.0");
        await DisableDnsLeakProtectionAsync();
    }

    private void HandleDeadConnection()
    {
        if (!IsRunning || _isExplicitlyStopped)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _isHandlingDeadConnection, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                Debug.WriteLine($"[DEAD CONNECTION] Packet blackhole detected on Windows. Initiating failover for {_currentServerIp}:{_currentServerPort}...");
                OnLogUpdated?.Invoke("[SMART CONNECT] Обнаружена потеря пакетов. Попытка восстановления маршрута...");
                UpdateState(AppVpnState.Reconnecting);

                var activeProto = OctopusEngine.Current.ActiveProtocol;
                if (activeProto == "FECHSUE")
                {
                    Debug.WriteLine($"[SMART CONNECT] UDP blackholed. Reconnecting on {_currentServerIp}:{_currentServerPort}...");
                    OnLogUpdated?.Invoke("[SMART CONNECT] Потеря UDP пакетов. Попытка переподключения...");
                    try
                    {
                        await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                        UpdateState(AppVpnState.Connected);
                        OnLogUpdated?.Invoke("[SMART CONNECT] Соединение восстановлено!");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[SMART CONNECT] Reconnect failed on {_currentServerIp}:{_currentServerPort}: {ex.Message}");
                    }
                }

                if (_fallbackServers.Count > 1)
                {
                    var gw = await GetDefaultGatewayAsync();
                    for (var i = 0; i < _fallbackServers.Count; i++)
                    {
                        var nextIdx = (_currentServerIndex + 1 + i) % _fallbackServers.Count;
                        var nextServer = _fallbackServers[nextIdx];
                        if (nextServer.Ip == _currentServerIp && _fallbackServers.Count > 1)
                        {
                            continue;
                        }

                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        var oldIp = _currentServerIp;
                        var newIp = nextServer.Ip;
                        var newPort = nextServer.Port > 0 ? nextServer.Port : 443;
                        _currentServerIndex = nextIdx;
                        _currentServerIp = newIp;
                        _currentServerPort = newPort;

                        if (!string.IsNullOrWhiteSpace(nextServer.CertHash))
                        {
                            OctopusEngine.DynamicSslPublicKeyHash = nextServer.CertHash;
                        }

                        OnLogUpdated?.Invoke($"[SMART CONNECT] Переключение на резервный узел: {newIp}...");
                        try
                        {
                            if (!string.IsNullOrEmpty(gw) && !string.IsNullOrEmpty(oldIp))
                            {
                                _ = await RunCmdAsync("route", $"delete {oldIp} mask 255.255.255.255");
                                _ = await RunCmdAsync("route", $"add {newIp} mask 255.255.255.255 {gw} metric 1");
                            }

                            await OctopusEngine.Current.ReconnectAsync(newIp, newPort);
                            UpdateState(AppVpnState.Connected);
                            OnLogUpdated?.Invoke("[SMART CONNECT] Подключение успешно переведено на новый узел!");
                            return;
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[SMART CONNECT] Failover to {newIp} failed: {ex.Message}");
                        }
                    }
                }

                await StopVpnAsync();
                UpdateState(AppVpnState.Error);
                OnErrorOccurred?.Invoke("Сервер отключил соединение.");
            }
            finally
            {
                Volatile.Write(ref _isHandlingDeadConnection, 0);
            }
        });
    }

    private void HandleEngineDrop()
    {
        var autoReconnect = Preferences.Get("AutoReconnect", true);
        var killSwitch = Preferences.Get("KillSwitch", false);

        if (IsRunning && !_isExplicitlyStopped)
        {
            if (!autoReconnect)
            {
                _ = Task.Run(async () =>
                {
                    await StopVpnAsync();
                    UpdateState(AppVpnState.Error);
                    OnErrorOccurred?.Invoke("Связь с сервером потеряна.");
                });
                return;
            }

            UpdateState(AppVpnState.Reconnecting);
            _ = Task.Run(async () =>
            {
                for (var i = 0; i < 10; i++)
                {
                    await Task.Delay(1500);
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
                            _ = await RunCmdAsync("route", $"add {_currentServerIp} mask 255.255.255.255 {gw} metric 1");
                            await OctopusEngine.Current.ConnectAsync(_currentServerIp, _currentServerPort);
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(2500));
                            if (verified)
                            {
                                UpdateState(AppVpnState.Connected);
                                return;
                            }
                            await OctopusEngine.Current.DisposeAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[RECONNECT] Attempt {i + 1} failed: {ex.Message}");
                    }
                }

                if (killSwitch)
                {
                    OnErrorOccurred?.Invoke("Не удалось восстановить связь. Kill Switch блокирует утечку IP. Нажмите «Стоп» для отключения.");
                }
                else
                {
                    await StopVpnAsync();
                    OnErrorOccurred?.Invoke("Связь с сервером потеряна. Не удалось восстановить подключение.");
                }
            });
        }
    }

    public Task StartVpnAsync(string serverIp, int serverPort) =>
        StartVpnAsync(serverIp, serverPort, null);

    public async Task StartVpnAsync(string serverIp, int serverPort, IReadOnlyList<VpnServerDto>? fallbackServers)
    {
        await _vpnGate.WaitAsync();
        try
        {
            _fallbackServers = fallbackServers != null ? [.. fallbackServers] : [];
            _currentServerIndex = _fallbackServers.FindIndex(s => s.Ip == serverIp);
            if (_currentServerIndex < 0 && !string.IsNullOrEmpty(serverIp))
            {
                _fallbackServers.Insert(0, new VpnServerDto(serverIp, serverPort, "", true, 0, null));
                _currentServerIndex = 0;
            }

            UpdateState(AppVpnState.Connecting);
            OnLogUpdated?.Invoke("Очистка старых сетевых настроек...");
            await CleanupStaleRoutesAsync();

            var targetIp = serverIp;
            if (Uri.CheckHostName(serverIp) == UriHostNameType.Dns)
            {
                try
                {
                    var ips = await Dns.GetHostAddressesAsync(serverIp);
                    if (ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                    {
                        targetIp = ipv4.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DNS ERROR] Could not resolve {serverIp}: {ex.Message}");
                }
            }

            if (!IPAddress.TryParse(targetIp, out _))
            {
                throw new InvalidOperationException($"Некорректный IP адрес сервера: '{targetIp}'");
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
                OnLogUpdated?.Invoke($"Построение маршрута через {originalHost}...");

                var connected = false;
                Exception? lastException = null;

                var serversToTry = _fallbackServers.Count > 0
                    ? _fallbackServers
                    : [new VpnServerDto(targetIp, serverPort, "", true, 0, null)];

                for (var idx = 0; idx < serversToTry.Count; idx++)
                {
                    if (_isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }

                    var s = serversToTry[idx];
                    var candidateIp = s.Ip;
                    var candidatePort = s.Port > 0 ? s.Port : serverPort;

                    if (Uri.CheckHostName(candidateIp) == UriHostNameType.Dns)
                    {
                        try
                        {
                            var ips = await Dns.GetHostAddressesAsync(candidateIp);
                            if (ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                            {
                                candidateIp = ipv4.ToString();
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DNS ERROR] Could not resolve candidate {candidateIp}: {ex.Message}");
                            lastException = ex;
                            continue;
                        }
                    }

                    if (!IPAddress.TryParse(candidateIp, out _))
                    {
                        Debug.WriteLine($"[DNS ERROR] Invalid candidate IP: {candidateIp}. Skipping.");
                        continue;
                    }

                    _currentServerIp = candidateIp;
                    _currentServerPort = candidatePort;
                    _currentServerIndex = idx;
                    targetIp = candidateIp;

                    if (!string.IsNullOrWhiteSpace(s.CertHash))
                    {
                        OctopusEngine.DynamicSslPublicKeyHash = s.CertHash;
                    }

                    for (var attempt = 1; attempt <= 2; attempt++)
                    {
                        if (_isExplicitlyStopped)
                        {
                            return;
                        }

                        try
                        {
                            if (attempt > 1)
                            {
                                OnLogUpdated?.Invoke($"Повтор подключения ({attempt}/2)...");
                            }

                            OnLogUpdated?.Invoke($"Подключение к {candidateIp}:{candidatePort}...");
                            await OctopusEngine.Current.ConnectAsync(candidateIp, candidatePort);

                            _cts?.Cancel();
                            _cts?.Dispose();
                            _cts = new CancellationTokenSource();
                            var ip = OctopusEngine.Current.AssignedIp;
                            var ipv6 = OctopusEngine.Current.AssignedIpV6;

                            if (!IPAddress.TryParse(ip, out _) || !IPAddress.TryParse(ipv6, out _))
                            {
                                throw new InvalidOperationException("Получены некорректные IP-адреса от сервера.");
                            }

                            OnLogUpdated?.Invoke($"Получен IP: {ip}");
                            if (_adapter is null)
                            {
                                OnLogUpdated?.Invoke("Инициализация виртуального адаптера Wintun...");
                                _adapter = await Task.Run(() => new WintunAdapter("Obxodka", "Obxodka"));
                            }

                            OnLogUpdated?.Invoke($"Запуск адаптера ({_adapter.Name})...");
                            _adapter.StartSession();

                            OnLogUpdated?.Invoke("Применение настроек сети...");
                            await SetAdapterConfigAsync(_adapter.Name, ip, "255.192.0.0");

                            OnLogUpdated?.Invoke("Перенаправление трафика в туннель...");
                            await SetWindowsRoutesAsync(_adapter.Name, targetIp, ip, true);
                            await EnableDnsLeakProtectionAsync(_adapter.Name, ip);
                            await ApplyExtremeNetworkBoostAsync();

                            OctopusEngine.Current.ResetTrafficCounters();
                            _ = Task.Run(() => ProcessTrafficAsync(_cts.Token));

                            OnLogUpdated?.Invoke("Проверка сквозного прохождения пакетов (RX)...");
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(2500), _cts.Token);
                            if (verified)
                            {
                                OnLogUpdated?.Invoke("Связь подтверждена! Защищенное соединение установлено.");
                                UpdateState(AppVpnState.Connected);
                                connected = true;
                                return;
                            }

                            OnLogUpdated?.Invoke("Входящие пакеты не поступают (0 RX). Быстрое переподключение...");
                            try
                            {
                                _adapter?.Dispose();
                                _adapter = null;
                            }
                            catch { }
                            await OctopusEngine.Current.DisposeAsync();
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            lastException = ex;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            try
                            {
                                _adapter?.Dispose();
                                _adapter = null;
                            }
                            catch { }
                            await OctopusEngine.Current.DisposeAsync();
                            if (attempt < 2)
                            {
                                await Task.Delay(500);
                            }
                        }
                    }

                    if (connected)
                    {
                        break;
                    }

                    if (lastException is UnauthorizedAccessException)
                    {
                        break;
                    }

                    if (idx + 1 < serversToTry.Count)
                    {
                        OnLogUpdated?.Invoke($"Сервер {candidateIp} недоступен или нет трафика. Пробуем запасной узел...");
                        await Task.Delay(300);
                    }
                }

                if (!connected && lastException is not null)
                {
                    if (lastException is OperationCanceledException || _isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }
                    throw lastException;
                }
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
        finally
        {
            _ = _vpnGate.Release();
        }
    }

    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        OctopusEngine.Current.OnPacketReceived += HandlePacketFromVpn;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var txThread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = "Wintun-UploadReader";
            var batch = new PacketBatch();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var adapter = _adapter;
                    if (adapter is null)
                    {
                        break;
                    }

                    var count = adapter.ReceiveBatch(batch, ct);
                    for (var i = 0; i < count; i++)
                    {
                        var (buf, len) = batch[i];
                        var sinkholeResp = DnsAdBlocker.ProcessPacket(buf, len);
                        if (sinkholeResp is not null)
                        {
                            adapter.SendPacket(sinkholeResp);
                            ArrayPool<byte>.Shared.Return(buf);
                            continue;
                        }

                        _ = OctopusEngine.Current.SendPacketFromPoolAsync(buf, len);
                    }
                }
            }
            catch { }
            finally
            {
                _ = tcs.TrySetResult();
            }
        })
        {
            IsBackground = true
        };
        txThread.Start();

        var rxThread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = "Wintun-Downloader";
            var reader = _downstreamChannel.Reader;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    while (reader.TryRead(out var item))
                    {
                        _adapter?.SendPacket(item.buffer, item.length);
                        ArrayPool<byte>.Shared.Return(item.buffer);
                    }

                    if (reader.WaitToReadAsync(ct).AsTask().Result)
                    {
                        continue;
                    }
                }
            }
            catch { }
        })
        {
            IsBackground = true
        };
        rxThread.Start();

        await tcs.Task;
    }

    private void HandlePacketFromVpn(byte[] data, int length) =>
        _downstreamChannel.Writer.TryWrite((data, length));

    private void UpdateState(AppVpnState state)
    {
        CurrentState = state;
        OnStateChanged?.Invoke(state);
    }

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

    private static async Task SetAdapterConfigAsync(string adapterName, string ip, string mask)
    {
        var pfx = mask == "255.192.0.0" ? 10 : 24;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var (exitCode, _) = await RunCmdAsync("netsh", $"interface ipv4 set address name=\"{adapterName}\" static {ip} {mask} none");
            if (exitCode == 0)
            {
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{adapterName}\" static 1.1.1.1 primary");
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{adapterName}\" 1.0.0.1 index=2");
                _ = await RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{adapterName}\" mtu=1360 store=active");
                _ = await RunCmdAsync("netsh", $"interface ipv4 set interface \"{adapterName}\" metric=1");
                Debug.WriteLine("[NET CONFIG] Configured adapter via netsh successfully.");
                return;
            }
            await Task.Delay(100);
        }

        var lastError = "";
        for (var i = 0; i < 20; i++)
        {
            var psScript = $@"
                $ErrorActionPreference = 'Stop';
                $adapter = Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object {{ $_.Name -like '*{adapterName}*' -or $_.InterfaceDescription -like '*Wintun*' -or $_.Name -like '*Wintun*' }} | Select-Object -First 1;
                if (-not $adapter) {{ exit 1; }}
                try {{ Remove-NetIPAddress -InterfaceIndex $adapter.ifIndex -Confirm:$false -ErrorAction SilentlyContinue }} catch {{ }}
                try {{ New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress '{ip}' -PrefixLength {pfx} -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -InterfaceMetric 1 -NlMtuBytes 1360 -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses '1.1.1.1','1.0.0.1' -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Enable-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | Out-Null }} catch {{ }}
            ";
            var (exitCode, output) = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\n", " ").Replace("\r", "")}\"");
            Debug.WriteLine($"[NET CONFIG] Attempt {i}, ExitCode: {exitCode}, Output: {output}");
            if (exitCode == 0)
            {
                Debug.WriteLine("[NET CONFIG] Success via PowerShell!");
                return;
            }

            lastError = output.Length > 200 ? string.Concat(output.AsSpan(0, 200), "...") : output;
            await Task.Delay(300);
        }

        throw new InvalidOperationException($"Не удалось настроить адаптер '{adapterName}'. Ошибка PS: {lastError}");
    }

    private static async Task<(int exitCode, string output)> RunCmdAsync(string fileName, string args)
    {
        var tcs = new TaskCompletionSource<(int, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                if (proc is null)
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
            var (_, output) = await RunCmdAsync("powershell", "-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Sort-Object RouteMetric | Select-Object -First 1).NextHop\"");
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

            try
            {
                await Task.WhenAll(deleteTasks).WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ROUTE DELETE TIMEOUT/ERROR] {ex.Message}");
            }
        }
    }

    public async Task StopVpnAsync()
    {
        await _vpnGate.WaitAsync();
        try
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
                if (adapterToDispose is not null)
                {
                    try
                    {
                        adapterToDispose.Dispose();
                    }
                    catch { }

                    Debug.WriteLine("[DRIVER] Wintun adapter disposed.");
                }

                await DisableDnsLeakProtectionAsync();
                await SetWindowsRoutesAsync(adapterName, serverIp, "", false);
                await CleanupStaleRoutesAsync();
                await RestoreOriginalNetworkSettingsAsync();
                await OctopusEngine.Current.DisposeAsync();
                Debug.WriteLine("[SYSTEM] VPN cleanup complete.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STOP ERROR] {ex.Message}");
            }

            UpdateState(AppVpnState.Disconnected);
        }
        finally
        {
            _ = _vpnGate.Release();
        }
    }

    private static async Task ApplyExtremeNetworkBoostAsync()
    {
        try
        {
            t_networkSettingsBoosted = true;
            var psBoost = @"
                netsh int tcp set global autotuninglevel=normal | Out-Null;
                netsh int tcp set global ecncapability=disabled | Out-Null;
                netsh int tcp set global rss=enabled | Out-Null;
                netsh int tcp set global fastopen=enabled | Out-Null;
                netsh int tcp set global timestamps=allowed | Out-Null;
                netsh int tcp set heuristics disabled | Out-Null;
            ";
            _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psBoost.Replace("\r", "").Replace("\n", " ")}\"");
            Debug.WriteLine("[BOOST] Windows Network Stack accelerated safely to high performance.");
        }
        catch { }
    }

    private static async Task RestoreOriginalNetworkSettingsAsync()
    {
        if (!t_networkSettingsBoosted)
        {
            return;
        }

        t_networkSettingsBoosted = false;
        try
        {
            var psRestore = @"
                netsh int tcp set global autotuninglevel=normal | Out-Null;
                netsh int tcp set global ecncapability=disabled | Out-Null;
                netsh int tcp set heuristics default | Out-Null;
            ";
            _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psRestore.Replace("\r", "").Replace("\n", " ")}\"");
            Debug.WriteLine("[BOOST] Windows Network Stack restored to default.");
        }
        catch { }
    }

    private static async Task EnableDnsLeakProtectionAsync(string adapterName, string assignedIp)
    {
        try
        {
            Debug.WriteLine("[DNS-LEAK] Activating robust DNS leak protection...");

            var ifIndex = GetWintunInterfaceIndex(adapterName);
            var ifArg = ifIndex > 0 ? $" if {ifIndex}" : "";
            _ = await RunCmdAsync("route", $"add 1.1.1.1 mask 255.255.255.255 {assignedIp} metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 1.0.0.1 mask 255.255.255.255 {assignedIp} metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 8.8.8.8 mask 255.255.255.255 {assignedIp} metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 8.8.4.4 mask 255.255.255.255 {assignedIp} metric 1{ifArg}");

            var psScript = @"
                $ErrorActionPreference = 'SilentlyContinue';
                try { Set-ItemProperty -Path 'HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient' -Name 'DisableSmartNameResolution' -Value 1 -Type DWord -Force | Out-Null } catch { };
                Clear-DnsClientCache | Out-Null;
            ";
            _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\r", "").Replace("\n", " ")}\"");
            _ = await RunCmdAsync("ipconfig", "/flushdns");

            Debug.WriteLine("[DNS-LEAK] DNS leak protection activated.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DNS-LEAK ERROR] {ex.Message}");
        }
    }

    private static async Task DisableDnsLeakProtectionAsync()
    {
        try
        {
            Debug.WriteLine("[DNS-LEAK] Removing DNS leak protection...");

            _ = await RunCmdAsync("route", "delete 1.1.1.1 mask 255.255.255.255");
            _ = await RunCmdAsync("route", "delete 1.0.0.1 mask 255.255.255.255");
            _ = await RunCmdAsync("route", "delete 8.8.8.8 mask 255.255.255.255");
            _ = await RunCmdAsync("route", "delete 8.8.4.4 mask 255.255.255.255");

            var psScript = @"
                $ErrorActionPreference = 'SilentlyContinue';
                try { Get-NetFirewallRule -DisplayName 'Obxodka_Block_DNS_*' | Remove-NetFirewallRule | Out-Null } catch { };
                try { Get-DnsClientNrptRule | Where-Object { $_.Namespace -eq '.' } | Remove-DnsClientNrptRule -Force | Out-Null } catch { };
                try { Remove-ItemProperty -Path 'HKLM:\Software\Policies\Microsoft\Windows NT\DNSClient' -Name 'DisableSmartNameResolution' } catch { };
                Clear-DnsClientCache | Out-Null;
            ";
            _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript.Replace("\r", "").Replace("\n", " ")}\"");
            _ = await RunCmdAsync("ipconfig", "/flushdns");

            Debug.WriteLine("[DNS-LEAK] DNS leak protection deactivated.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DNS-LEAK CLEANUP ERROR] {ex.Message}");
        }
    }

    public void Dispose()
    {
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnDeadConnectionDetected -= HandleDeadConnection;
        _ = StopVpnAsync();
        _cts?.Dispose();
        _vpnGate.Dispose();
    }
}
