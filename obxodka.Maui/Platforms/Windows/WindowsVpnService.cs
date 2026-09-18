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
    private uint _currentServerIpUint;
    private uint _localGatewayIpUint;
    private static uint t_publicWanIpUint;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<uint, string> t_bypassedManagementIps = new();
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;
    private static bool t_networkSettingsBoosted;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    private long _vpnRxPacketsCount;

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

    private async Task SyncAdapterIpAsync()
    {
        if (_adapter != null)
        {
            var ip = OctopusEngine.Current.AssignedIp;
            if (!string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _))
            {
                await SetAdapterConfigAsync(_adapter.Name, ip, "255.192.0.0");
            }
        }
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
                        await SyncAdapterIpAsync();
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
                    var (gw, physicalIfIndex) = await GetDefaultGatewayInfoAsync();
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

                        _currentServerIpUint = IPAddress.TryParse(newIp, out var parsedNewIp) && parsedNewIp.AddressFamily == AddressFamily.InterNetwork
                            ? BitConverter.ToUInt32(parsedNewIp.GetAddressBytes(), 0)
                            : 0;

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
                                var ifParam = physicalIfIndex > 0 ? $" if {physicalIfIndex}" : "";
                                _ = await RunCmdAsync("route", $"add {newIp} mask 255.255.255.255 {gw} metric 1{ifParam}");
                            }

                            await OctopusEngine.Current.ReconnectAsync(newIp, newPort);
                            await SyncAdapterIpAsync();
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
                        var (gw, physicalIfIndex) = await GetDefaultGatewayInfoAsync();
                        if (!string.IsNullOrEmpty(gw) && !string.IsNullOrEmpty(_currentServerIp))
                        {
                            _ = await RunCmdAsync("route", $"delete {_currentServerIp} mask 255.255.255.255");
                            var ifParam = physicalIfIndex > 0 ? $" if {physicalIfIndex}" : "";
                            _ = await RunCmdAsync("route", $"add {_currentServerIp} mask 255.255.255.255 {gw} metric 1{ifParam}");
                            await OctopusEngine.Current.ConnectAsync(_currentServerIp, _currentServerPort);
                            await SyncAdapterIpAsync();
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000));
                            if (verified || OctopusEngine.Current.IsConnected)
                            {
                                Debug.WriteLine($"[RECONNECT] Reconnected! verified={verified}, isConnected={OctopusEngine.Current.IsConnected}");
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

                    _currentServerIpUint = IPAddress.TryParse(candidateIp, out var parsedCandidateIp) && parsedCandidateIp.AddressFamily == AddressFamily.InterNetwork
                        ? BitConverter.ToUInt32(parsedCandidateIp.GetAddressBytes(), 0)
                        : 0;

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

                            var (defaultGw, _) = await GetDefaultGatewayInfoAsync();
                            if (IPAddress.TryParse(defaultGw, out var parsedGw) && parsedGw.AddressFamily == AddressFamily.InterNetwork)
                            {
                                _localGatewayIpUint = BitConverter.ToUInt32(parsedGw.GetAddressBytes(), 0);
                            }

                            OnLogUpdated?.Invoke("Перенаправление трафика в туннель...");
                            await SetWindowsRoutesAsync(_adapter.Name, targetIp, true);
                            await EnableDnsLeakProtectionAsync(_adapter.Name);
                            await ApplyExtremeNetworkBoostAsync();

                            OctopusEngine.Current.ResetTrafficCounters();
                            _ = Task.Run(() => ProcessTrafficAsync(_cts.Token));

                            OnLogUpdated?.Invoke($"Проверка соединения с сервером (RX={OctopusEngine.Current.TotalBytesReceived} B)...");
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(7000), _cts.Token);
                            if (verified)
                            {
                                OnLogUpdated?.Invoke($"Связь подтверждена (RX={OctopusEngine.Current.TotalBytesReceived} B)! Защищенное соединение установлено.");
                            }
                            else
                            {
                                Debug.WriteLine($"[VPN-CONNECT] Downlink probe did not get immediate echo (TX={OctopusEngine.Current.TotalBytesSent} B, RX={OctopusEngine.Current.TotalBytesReceived} B), but tunnel is connected and routes are active. Transitioning to Connected state.");
                                OnLogUpdated?.Invoke($"Туннель запущен ({OctopusEngine.Current.ActiveProtocol}). Ожидание сетевого трафика...");
                            }

                            UpdateState(AppVpnState.Connected);
                            connected = true;
                            return;
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            lastException = ex;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastException = ex;
                            _cts?.Cancel();
                            await Task.Delay(60);
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

                if (!connected)
                {
                    if (lastException is OperationCanceledException || _isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }
                    throw lastException ?? new InvalidOperationException("Сервер не отвечает или пакеты блокируются (0 RX). Проверьте интернет или смените протокол.");
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
            long wintunPktsRead = 0;
            long wintunBytesRead = 0;
            Debug.WriteLine("[WINTUN-TX] Upload reader thread started.");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var adapter = _adapter;
                    if (adapter is null)
                    {
                        Debug.WriteLine("[WINTUN-TX] Adapter is null! Exiting loop.");
                        break;
                    }

                    var count = adapter.ReceiveBatch(batch, ct);
                    for (var i = 0; i < count; i++)
                    {
                        var (buf, len) = batch[i];
                        wintunPktsRead++;
                        wintunBytesRead += len;

                        if (wintunPktsRead <= 15 || wintunPktsRead % 200 == 0)
                        {
                            var version = len >= 20 ? (buf[0] >> 4) : 0;
                            var destStr = version == 4 && len >= 20
                                ? $"{buf[16]}.{buf[17]}.{buf[18]}.{buf[19]}"
                                : "unknown";
                            Debug.WriteLine($"[WINTUN-TX-PKT #{wintunPktsRead}] Read {len}B -> dest {destStr}. Total={wintunBytesRead}B");
                        }

                        // Anti-loopback protection: Never send packets destined for the VPN server, local gateway, public WAN IP, or active remote management sessions back into the tunnel!
                        if (len >= 20 && (buf[0] >> 4) == 4)
                        {
                            var destIp = MemoryMarshal.Read<uint>(buf.AsSpan(16, 4));
                            if ((_currentServerIpUint != 0 && destIp == _currentServerIpUint) ||
                                (_localGatewayIpUint != 0 && destIp == _localGatewayIpUint) ||
                                (t_publicWanIpUint != 0 && destIp == t_publicWanIpUint) ||
                                t_bypassedManagementIps.ContainsKey(destIp))
                            {
                                ArrayPool<byte>.Shared.Return(buf);
                                continue;
                            }
                        }

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
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[WINTUN-TX] Cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WINTUN-TX-ERR] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Debug.WriteLine($"[WINTUN-TX-EXIT] Exited. Stats: {wintunPktsRead} pkts, {wintunBytesRead} bytes.");
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
            long wintunPktsWritten = 0;
            long wintunBytesWritten = 0;
            Debug.WriteLine("[WINTUN-RX] Downloader thread started.");
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    while (reader.TryRead(out var item))
                    {
                        wintunPktsWritten++;
                        wintunBytesWritten += item.length;
                        if (wintunPktsWritten <= 15 || wintunPktsWritten % 200 == 0)
                        {
                            Debug.WriteLine($"[WINTUN-RX-PKT #{wintunPktsWritten}] Writing {item.length}B to Wintun. Total={wintunBytesWritten}B");
                        }
                        _adapter?.SendPacket(item.buffer, item.length);
                        ArrayPool<byte>.Shared.Return(item.buffer);
                    }

                    if (reader.WaitToReadAsync(ct).AsTask().Result)
                    {
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("[WINTUN-RX] Cancelled.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WINTUN-RX-ERR] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Debug.WriteLine($"[WINTUN-RX-EXIT] Exited. Stats: {wintunPktsWritten} pkts, {wintunBytesWritten} bytes.");
                while (reader.TryRead(out var item))
                {
                    ArrayPool<byte>.Shared.Return(item.buffer);
                }
            }
        })
        {
            IsBackground = true
        };
        rxThread.Start();

        try
        {
            await tcs.Task;
        }
        finally
        {
            OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        }
    }

    private void HandlePacketFromVpn(byte[] data, int length)
    {
        var count = Interlocked.Increment(ref _vpnRxPacketsCount);
        if (count <= 15 || count % 200 == 0)
        {
            Debug.WriteLine($"[VPN-RX-PACKET #{count}] Inbound packet from engine: {length} bytes");
        }
        _ = _downstreamChannel.Writer.TryWrite((data, length));
    }

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

    private static async Task<(string Gateway, int InterfaceIndex)> GetDefaultGatewayInfoAsync()
    {
        try
        {
            var (_, output) = await RunCmdAsync("powershell",
                "-NoProfile -Command \"(Get-NetRoute -DestinationPrefix '0.0.0.0/0' -ErrorAction SilentlyContinue | Where-Object { $_.NextHop -ne '0.0.0.0' -and $_.InterfaceAlias -notlike '*Obxodka*' -and $_.InterfaceAlias -notlike '*Wintun*' -and $_.InterfaceAlias -notlike '*Radmin*' -and $_.InterfaceAlias -notlike '*WireGuard*' -and $_.InterfaceAlias -notlike '*Amnezia*' -and $_.InterfaceAlias -notlike '*OpenVPN*' -and $_.InterfaceAlias -notlike '*TAP*' -and $_.InterfaceAlias -notlike '*vEthernet*' } | Sort-Object RouteMetric | Select-Object -First 1 | ForEach-Object { $_.NextHop + '|' + $_.InterfaceIndex })\"");
            var line = output.Trim();
            if (!string.IsNullOrEmpty(line) && line.Contains('|'))
            {
                var parts = line.Split('|');
                if (parts.Length == 2 && IPAddress.TryParse(parts[0], out _))
                {
                    _ = int.TryParse(parts[1], out var ifIndex);
                    return (parts[0], ifIndex);
                }
            }

            foreach (var card in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (card.OperationalStatus != OperationalStatus.Up ||
                    card.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    card.NetworkInterfaceType == NetworkInterfaceType.Tunnel ||
                    card.Description.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("Obxodka", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("Amnezia", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                    card.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
                    card.Name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase) ||
                    card.Name.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    card.Name.Contains("Radmin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var ipProps = card.GetIPProperties();
                var gw = ipProps.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(g.Address) && !g.Address.Equals(IPAddress.Any))?
                    .Address.ToString();

                if (!string.IsNullOrEmpty(gw))
                {
                    var ifIdx = 0;
                    try
                    {
                        ifIdx = ipProps.GetIPv4Properties()?.Index ?? 0;
                    }
                    catch { }
                    return (gw, ifIdx);
                }
            }

            return ("", 0);
        }
        catch
        {
            return ("", 0);
        }
    }

    private static async Task<List<string>> DetectRemoteManagementIpsAsync()
    {
        var result = new List<string>();
        try
        {
            var remoteProcessNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "AnyDesk", "TeamViewer", "RustDesk", "mstsc", "vncserver", "tv_w32", "tv_x64"
            };

            var targetPids = new HashSet<int>();
            foreach (var proc in Process.GetProcesses())
            {
                try
                {
                    if (remoteProcessNames.Contains(proc.ProcessName))
                    {
                        _ = targetPids.Add(proc.Id);
                    }
                }
                catch { }
            }

            var (_, output) = await RunCmdAsync("netstat", "-ano -p tcp");
            if (!string.IsNullOrWhiteSpace(output))
            {
                var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                foreach (var rawLine in lines)
                {
                    var parts = rawLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 5 && parts[0].Equals("TCP", StringComparison.OrdinalIgnoreCase) &&
                        parts[3].Equals("ESTABLISHED", StringComparison.OrdinalIgnoreCase))
                    {
                        var foreignEp = parts[2];
                        var lastColon = foreignEp.LastIndexOf(':');
                        if (lastColon <= 0)
                        {
                            continue;
                        }

                        var foreignIp = foreignEp[..lastColon];
                        var portStr = foreignEp[(lastColon + 1)..];

                        if (!int.TryParse(parts[4], out var pid))
                        {
                            continue;
                        }
                        _ = int.TryParse(portStr, out var port);

                        if (targetPids.Contains(pid) || port is 6568 or 7070 or 3389)
                        {
                            if (IPAddress.TryParse(foreignIp, out var parsed) &&
                                parsed.AddressFamily == AddressFamily.InterNetwork &&
                                !IPAddress.IsLoopback(parsed))
                            {
                                result.Add(foreignIp);
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DETECT REMOTE IP ERROR] {ex.Message}");
        }
        return [.. result.Distinct()];
    }

    private static async Task SetWindowsRoutesAsync(string adapterName, string serverIp, bool enable)
    {
        var (gw, physicalIfIndex) = await GetDefaultGatewayInfoAsync();
        Debug.WriteLine($"[ROUTE] Default Gateway: {gw}, PhysicalIfIndex: {physicalIfIndex}, Name: {adapterName}, ServerIP: {serverIp}, Enable: {enable}");

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
            if (!string.IsNullOrEmpty(gw) && !string.IsNullOrEmpty(serverIp))
            {
                _ = await RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255");
                var (exitCode, output) = await RunCmdAsync("route", $"add {serverIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
                Debug.WriteLine($"[ROUTE] Add Server Route: ExitCode {exitCode}, Output: {output}");
            }

            // 1. Bypass client's detected Public WAN IP (anti-hairpinning protection)
            var wanIp = OctopusEngine.Current.PublicWanIp;
            if (!string.IsNullOrEmpty(wanIp) && IPAddress.TryParse(wanIp, out var parsedWan) && parsedWan.AddressFamily == AddressFamily.InterNetwork)
            {
                t_publicWanIpUint = MemoryMarshal.Read<uint>(parsedWan.GetAddressBytes());
                if (!string.IsNullOrEmpty(gw) && t_bypassedManagementIps.TryAdd(t_publicWanIpUint, wanIp))
                {
                    _ = await RunCmdAsync("route", $"add {wanIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
                    Debug.WriteLine($"[ROUTE] Bypassed Client Public WAN IP: {wanIp} via {gw}{physIfArg}");
                }
            }

            // 2. Bypass active Remote Desktop / Management sessions (AnyDesk, TeamViewer, RustDesk, RDP)
            if (!string.IsNullOrEmpty(gw))
            {
                var remoteIps = await DetectRemoteManagementIpsAsync();
                foreach (var rIp in remoteIps)
                {
                    if (IPAddress.TryParse(rIp, out var parsedRemote) && parsedRemote.AddressFamily == AddressFamily.InterNetwork)
                    {
                        var rUint = MemoryMarshal.Read<uint>(parsedRemote.GetAddressBytes());
                        if (t_bypassedManagementIps.TryAdd(rUint, rIp))
                        {
                            _ = await RunCmdAsync("route", $"add {rIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
                            Debug.WriteLine($"[ROUTE] Bypassed Remote Management session IP: {rIp} via {gw}{physIfArg}");
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(ifIndex))
            {
                await Task.Delay(200);
                var (exitCode, output) = await RunCmdAsync("route", $"add 0.0.0.0 mask 128.0.0.0 0.0.0.0 metric 1 if {ifIndex}");
                var r3 = await RunCmdAsync("route", $"add 128.0.0.0 mask 128.0.0.0 0.0.0.0 metric 1 if {ifIndex}");
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

            foreach (var rIp in t_bypassedManagementIps.Values)
            {
                deleteTasks.Add(RunCmdAsync("route", $"delete {rIp} mask 255.255.255.255"));
            }
            t_bypassedManagementIps.Clear();
            t_publicWanIpUint = 0;

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
                await SetWindowsRoutesAsync(adapterName, serverIp, false);
                await CleanupStaleRoutesAsync();
                await RestoreOriginalNetworkSettingsAsync();
                await OctopusEngine.Current.DisposeAsync();
                Debug.WriteLine("[SYSTEM] VPN cleanup complete.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STOP ERROR] {ex.Message}");
            }

            _currentServerIpUint = 0;
            _localGatewayIpUint = 0;
            t_publicWanIpUint = 0;
            t_bypassedManagementIps.Clear();
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

    private static async Task EnableDnsLeakProtectionAsync(string adapterName)
    {
        try
        {
            Debug.WriteLine("[DNS-LEAK] Activating robust DNS leak protection...");

            var ifIndex = GetWintunInterfaceIndex(adapterName);
            var ifArg = ifIndex > 0 ? $" if {ifIndex}" : "";
            _ = await RunCmdAsync("route", $"add 1.1.1.1 mask 255.255.255.255 0.0.0.0 metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 1.0.0.1 mask 255.255.255.255 0.0.0.0 metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 8.8.8.8 mask 255.255.255.255 0.0.0.0 metric 1{ifArg}");
            _ = await RunCmdAsync("route", $"add 8.8.4.4 mask 255.255.255.255 0.0.0.0 metric 1{ifArg}");

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
