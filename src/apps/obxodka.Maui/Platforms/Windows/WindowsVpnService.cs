using obxodka.Core.Models;
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
    private readonly List<string> _currentServerIpsToRoute = [];
    private int _currentServerPort = 443;
    private bool _isExplicitlyStopped;
    private static bool t_networkSettingsBoosted;
    private List<VpnServerDto> _fallbackServers = [];
    private int _currentServerIndex;
    private int _isHandlingDeadConnection;
    private readonly SemaphoreSlim _vpnGate = new(1, 1);
    public static SplitTunnelPolicy SplitTunnelPolicy { get; } = new();

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateUnbounded<(byte[] buffer, int length)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public WindowsVpnService()
    {
        _ = Task.Run(CleanupStaleRoutesAsync);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                _ = StopVpnAsync().Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        };
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
        _ = await RunCmdAsync("route", $"delete {AppConfig.DirectServerIp} mask 255.255.255.255");
        await DisableDnsLeakProtectionAsync();
        await RestoreNeutralizedAdaptersAsync();
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

                try
                {
                    await EnsureHostRouteAsync(_currentServerIp);
                    OctopusEngine.Current.RegisterServerEndpoint(_currentServerIp);
                    await OctopusEngine.Current.ReconnectAsync(_currentServerIp, _currentServerPort);
                    var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4000));
                    if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
                    {
                        OctopusEngine.Current.ResetTrafficCounters();
                        OctopusEngine.Current.ArmTrafficWatchdog();
                        UpdateState(AppVpnState.Connected);
                        OnLogUpdated?.Invoke("[SMART CONNECT] Соединение восстановлено!");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SMART CONNECT] Reconnect failed on {_currentServerIp}:{_currentServerPort}: {ex.Message}");
                }

                if (_fallbackServers.Count > 1)
                {
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
                        if (!_currentServerIpsToRoute.Contains(newIp))
                        {
                            _currentServerIpsToRoute.Add(newIp);
                        }

                        if (!string.IsNullOrWhiteSpace(nextServer.CertHash))
                        {
                            OctopusEngine.DynamicSslPublicKeyHash = nextServer.CertHash;
                        }

                        OnLogUpdated?.Invoke($"[SMART CONNECT] Переключение на резервный узел: {newIp}...");
                        try
                        {
                            await SwitchHostRouteAsync(oldIp, newIp);
                            OctopusEngine.Current.RegisterServerEndpoint(newIp);
                            await OctopusEngine.Current.ReconnectAsync(newIp, newPort);
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(4000));
                            if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
                            {
                                OctopusEngine.Current.ResetTrafficCounters();
                                OctopusEngine.Current.ArmTrafficWatchdog();
                                UpdateState(AppVpnState.Connected);
                                OnLogUpdated?.Invoke("[SMART CONNECT] Подключение успешно переведено на новый узел!");
                                return;
                            }
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
                        if (!string.IsNullOrEmpty(_currentServerIp))
                        {
                            await EnsureHostRouteAsync(_currentServerIp);
                            OctopusEngine.Current.RegisterServerEndpoint(_currentServerIp);
                            await OctopusEngine.Current.ConnectAsync(_currentServerIp, _currentServerPort);
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(5000));
                            if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
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
                    await AutoHealAdapterDnsAsync().ConfigureAwait(false);
                    try
                    {
                        var ips = await Dns.GetHostAddressesAsync(serverIp);
                        if (ips.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } ipv4)
                        {
                            targetIp = ipv4.ToString();
                        }
                    }
                    catch { }
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

            await NeutralizeConflictingAdaptersAsync(OnLogUpdated);

            try
            {
                LogNetworkDiagnostics(OnLogUpdated);
                OnLogUpdated?.Invoke($"Построение маршрута через {originalHost}...");

                var connected = false;
                Exception? lastException = null;

                var serversToTry = _fallbackServers.Count > 0
                    ? _fallbackServers
                    : [new VpnServerDto(targetIp, serverPort, "", true, 0, null)];

                if (serversToTry.Count > 1)
                {
                    OnLogUpdated?.Invoke("Поиск быстрейшего узла (Happy Eyeballs)...");
                    serversToTry = [.. await NetworkDefaults.RankServersByLatencyAsync(serversToTry, 650).ConfigureAwait(false)];
                }

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

                    var candidateIpsToRoute = new List<string>();
                    if (Uri.CheckHostName(candidateIp) == UriHostNameType.Dns)
                    {
                        try
                        {
                            var ips = await Dns.GetHostAddressesAsync(candidateIp);
                            foreach (var ipAddr in ips.Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                            {
                                candidateIpsToRoute.Add(ipAddr.ToString());
                            }
                            if (candidateIpsToRoute.Count > 0)
                            {
                                candidateIp = candidateIpsToRoute[0];
                            }
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[DNS ERROR] Could not resolve candidate {candidateIp}: {ex.Message}");
                            lastException = ex;
                            continue;
                        }
                    }
                    else
                    {
                        candidateIpsToRoute.Add(candidateIp);
                    }

                    if (!candidateIpsToRoute.Contains(AppConfig.DirectServerIp))
                    {
                        candidateIpsToRoute.Add(AppConfig.DirectServerIp);
                    }

                    if (!IPAddress.TryParse(candidateIp, out _))
                    {
                        Debug.WriteLine($"[DNS ERROR] Invalid candidate IP: {candidateIp}. Skipping.");
                        continue;
                    }

                    _currentServerIp = candidateIp;
                    _currentServerPort = candidatePort;
                    _currentServerIndex = idx;
                    _currentServerIpsToRoute.Clear();
                    _currentServerIpsToRoute.AddRange(candidateIpsToRoute);
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
                            await SetAdapterConfigAsync(_adapter.Name, ip, "255.192.0.0", ipv6);

                            OctopusEngine.Current.ResetTrafficCounters();
                            _ = Task.Run(() => ProcessTrafficAsync(_cts.Token));

                            OnLogUpdated?.Invoke("Проверка готовности туннеля (RX)...");
                            var verified = await OctopusEngine.Current.VerifyDownlinkAsync(TimeSpan.FromMilliseconds(6000), _cts.Token);
                            if (verified || OctopusEngine.Current.TotalBytesReceived > 0)
                            {
                                OnLogUpdated?.Invoke($"Связь подтверждена (RX={OctopusEngine.Current.TotalBytesReceived} B)! Активация...");
                            }
                            else
                            {
                                Debug.WriteLine($"[WINDOWS-VPN] Downlink probe timeout (TX={OctopusEngine.Current.TotalBytesSent}, RX=0). Rejecting dead connection.");
                                OnLogUpdated?.Invoke($"Нет входящего трафика от сервера (0 RX). Переподключение ({attempt}/2)...");
                                throw new InvalidOperationException("Сервер не отвечает на тестовые пакеты (0 RX).");
                            }

                            OnLogUpdated?.Invoke("Перенаправление трафика в туннель...");
                            await SetWindowsRoutesAsync(_adapter.Name, _currentServerIpsToRoute, ip, true);
                            OnLogUpdated?.Invoke("Включение защиты от утечек DNS...");
                            await EnableDnsLeakProtectionAsync(_adapter.Name, ip);
                            ApplyExtremeNetworkBoost();

                            OctopusEngine.Current.ResetTrafficCounters();
                            OctopusEngine.Current.ArmTrafficWatchdog();

                            OnLogUpdated?.Invoke("Защищенное соединение активно.");
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
                    if (_isExplicitlyStopped)
                    {
                        UpdateState(AppVpnState.Disconnected);
                        return;
                    }
                    if (lastException is OperationCanceledException or TimeoutException)
                    {
                        throw new TimeoutException($"Таймаут подключения: сервер {_currentServerIp}:{_currentServerPort} не ответил на TLS/gRPC хэндшейк.");
                    }
                    throw lastException ?? new InvalidOperationException("Сервер не отвечает или пакеты блокируются (0 RX). Проверьте интернет или смените протокол.");
                }
            }
            catch (Exception ex)
            {
                UpdateState(AppVpnState.Error);
                var errMsg = ex is TimeoutException || ex.InnerException is TimeoutException
                    ? ex.Message
                    : $"Ошибка: {ex.Message}";
                OnLogUpdated?.Invoke(errMsg);
                OnErrorOccurred?.Invoke(errMsg);
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
                        if (OctopusEngine.Current.IsServerDestination(buf.AsSpan(0, len), len))
                        {
                            ArrayPool<byte>.Shared.Return(buf);
                            continue;
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
            finally
            {
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

    private static async Task SetAdapterConfigAsync(string adapterName, string ip, string mask, string? ipv6 = null)
    {
        var pfx = mask == "255.192.0.0" ? 10 : 24;

        for (var attempt = 0; attempt < 25; attempt++)
        {
            var (exitCode, _) = await RunCmdAsync("netsh", $"interface ipv4 set address name=\"{adapterName}\" static {ip} {mask} none");
            if (exitCode == 0)
            {
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{adapterName}\" static {NetworkDefaults.PrimaryDns} primary");
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{adapterName}\" {NetworkDefaults.SecondaryDns} index=2");
                _ = await RunCmdAsync("netsh", $"interface ipv4 set subinterface \"{adapterName}\" mtu={NetworkDefaults.DefaultMtu} store=active");
                _ = await RunCmdAsync("netsh", $"interface ipv4 set interface \"{adapterName}\" metric=1");

                if (!string.IsNullOrWhiteSpace(ipv6))
                {
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set address name=\"{adapterName}\" address={ipv6} store=active");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set dnsservers name=\"{adapterName}\" static 2606:4700:4700::1111 primary");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set subinterface \"{adapterName}\" mtu={NetworkDefaults.DefaultMtu} store=active");
                    _ = await RunCmdAsync("netsh", $"interface ipv6 set interface \"{adapterName}\" metric=1");
                }

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
                try {{ Set-NetIPInterface -InterfaceIndex $adapter.ifIndex -InterfaceMetric 1 -NlMtuBytes {NetworkDefaults.DefaultMtu} -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Set-DnsClientServerAddress -InterfaceIndex $adapter.ifIndex -ServerAddresses '{NetworkDefaults.PrimaryDns}','{NetworkDefaults.SecondaryDns}' -ErrorAction Stop | Out-Null }} catch {{ }}
                try {{ Enable-NetAdapterBinding -Name $adapter.Name -ComponentID ms_tcpip6 -ErrorAction SilentlyContinue | Out-Null }} catch {{ }}
                if ('{ipv6}' -ne '') {{ try {{ New-NetIPAddress -InterfaceIndex $adapter.ifIndex -IPAddress '{ipv6}' -PrefixLength 64 -ErrorAction SilentlyContinue | Out-Null }} catch {{ }} }}
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

    private static async Task<(int exitCode, string output)> RunCmdAsync(
        string fileName,
        string args,
        int timeoutMs = 4000,
        CancellationToken ct = default)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(fileName, args)
                {
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false
                }
            };

            if (!proc.Start())
            {
                return (-1, "Failed to start process");
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(timeoutMs);

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = proc.StandardError.ReadToEndAsync(linkedCts.Token);

            try
            {
                await proc.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch { }

                return (-1, "Command timed out or cancelled");
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
    private static partial int GetBestRoute(uint dwDestAddr, uint dwSourceAddr, out MIB_IPFORWARDROW pBestRoute);

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

    private static string t_savedPhysicalGateway = "";
    private static int t_savedPhysicalIfIndex;

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

    public async Task<string?> RunNetworkPreflightAsync()
    {
        OnLogUpdated?.Invoke("Проверка доступа в интернет и DNS...");

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
            Shared.Logging.AppLogger.Log($"[PREFLIGHT] TCP {referenceIps[i]}:443 -> {(refOk ? "OK" : refError.ToString())}");
        }
        Shared.Logging.AppLogger.Log($"[PREFLIGHT] TCP {AppConfig.DirectServerIp}:443 -> {(serverOk ? "OK" : serverError.ToString())}");
        Shared.Logging.AppLogger.Log($"[PREFLIGHT] System DNS -> {(dnsOk ? "OK" : "FAIL")}");

        var internetOk = referenceResults.Any(r => r.ok);
        if (!internetOk && !serverOk)
        {
            return "Нет доступа в интернет: компьютер не может подключиться ни к одному сайту (даже к Яндексу).\n\n" +
                   "Частые причины:\n" +
                   "• Kill Switch другого VPN (Amnezia, WireGuard и т.п.) блокирует интернет, даже когда тот VPN отключён. Выключите Kill Switch в его настройках или полностью закройте программу.\n" +
                   "• Не подключён кабель / Wi-Fi.\n\n" +
                   "После этого попробуйте снова.";
        }

        if (!serverOk)
        {
            OnLogUpdated?.Invoke("Основной узел недоступен напрямую, пробуем резервные пути...");
        }

        if (!dnsOk)
        {
            OnLogUpdated?.Invoke("Обнаружен сбой системного DNS. Автовосстановление серверов...");
            await AutoHealAdapterDnsAsync().ConfigureAwait(false);
            dnsOk = await ProbeSystemDnsAsync("ya.ru", 2500).ConfigureAwait(false);
            if (dnsOk)
            {
                OnLogUpdated?.Invoke("Системный DNS успешно восстановлен (Яндекс / Cloudflare).");
            }
            else
            {
                OnLogUpdated?.Invoke("Обходка переключается на автономный защищённый DNS.");
            }
        }
        else
        {
            OnLogUpdated?.Invoke("Интернет и системный DNS в порядке.");
        }

        return null;
    }

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

                var desc = nic.Description;
                var name = nic.Name;
                if (desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                    desc.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Wintun", StringComparison.OrdinalIgnoreCase))
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
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _ = targetNames.Add(name);
                    }
                }
            }

            foreach (var idx in targetIndices)
            {
                _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"try {{ Set-DnsClientServerAddress -InterfaceIndex {idx} -ServerAddresses '77.88.8.8','1.1.1.1' -ErrorAction Stop }} catch {{ }}\"");
            }

            foreach (var name in targetNames)
            {
                _ = await RunCmdAsync("netsh", $"interface ipv4 set dnsservers name=\"{name}\" static 77.88.8.8 primary");
                _ = await RunCmdAsync("netsh", $"interface ipv4 add dnsservers name=\"{name}\" 1.1.1.1 index=2");
            }

            _ = await RunCmdAsync("ipconfig", "/flushdns");
        }
        catch { }
    }

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

    private static bool IsConflictingAdapter(NetworkInterface c)
    {
        if (c.OperationalStatus != OperationalStatus.Up ||
            c.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
            c.Name.Contains("Obxodka", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = c.Name;
        var desc = c.Description;
        var isConflicting = desc.Contains("Wintun", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("TAP", StringComparison.OrdinalIgnoreCase) ||
                            desc.Contains("VPN", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Amnezia", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("WireGuard", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("CloudflareWARP", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Mullvad", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("OpenVPN", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Proton", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("VPN", StringComparison.OrdinalIgnoreCase);

        if (!isConflicting)
        {
            var props = c.GetIPProperties();
            foreach (var gw in props.GatewayAddresses)
            {
                var gwStr = gw.Address.ToString();
                if (gwStr is "0.0.0.0" or "::")
                {
                    isConflicting = true;
                    break;
                }
            }
        }

        return isConflicting;
    }

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

    private static readonly HashSet<string> s_neutralizedAdapters = new(StringComparer.OrdinalIgnoreCase);

    private static async Task NeutralizeConflictingAdaptersAsync(Action<string>? onLogUpdated)
    {
        try
        {
            foreach (var c in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!IsConflictingAdapter(c))
                {
                    continue;
                }

                var name = c.Name;
                onLogUpdated?.Invoke($"Отключение остаточного адаптера '{name}'...");
                var (code, outStr) = await RunCmdAsync("netsh", $"interface set interface name=\"{name}\" admin=disabled", timeoutMs: 3000);
                if (code != 0)
                {
                    _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"Disable-NetAdapter -Name '{name}' -Confirm:$false -ErrorAction SilentlyContinue\"", timeoutMs: 3000);
                }
                Debug.WriteLine($"[NET-NEUTRALIZE] Disabled '{name}': exitCode={code}, out={outStr}");
                lock (s_neutralizedAdapters)
                {
                    s_neutralizedAdapters.Add(name);
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[NET-NEUTRALIZE-ERR] {ex.Message}");
        }
    }

    private static async Task RestoreNeutralizedAdaptersAsync()
    {
        List<string> adaptersToRestore;
        lock (s_neutralizedAdapters)
        {
            if (s_neutralizedAdapters.Count == 0)
            {
                return;
            }
            adaptersToRestore = [.. s_neutralizedAdapters];
            s_neutralizedAdapters.Clear();
        }

        foreach (var name in adaptersToRestore)
        {
            try
            {
                var (code, outStr) = await RunCmdAsync("netsh", $"interface set interface name=\"{name}\" admin=enabled", timeoutMs: 3000);
                if (code != 0)
                {
                    _ = await RunCmdAsync("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"Enable-NetAdapter -Name '{name}' -Confirm:$false -ErrorAction SilentlyContinue\"", timeoutMs: 3000);
                }
                Debug.WriteLine($"[NET-RESTORE] Re-enabled '{name}': exitCode={code}, out={outStr}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NET-RESTORE-ERR] {name}: {ex.Message}");
            }
        }
    }

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
                Shared.Logging.AppLogger.Log($"[NET-DIAG] Card: '{c.Name}' ({c.Description}), IfIndex: {idx}, Status: {c.OperationalStatus}, IPs: [{ips}], Gateways: [{gws}]");

                if (IsConflictingAdapter(c))
                {
                    Shared.Logging.AppLogger.Log($"[CONFLICTING-VPN] Card '{c.Name}' ({c.Description}) is Up. It may block Obxodka traffic via WFP firewall.");
                    onLogUpdated?.Invoke($"[ВНИМАНИЕ] Обнаружен активный сторонний VPN: '{c.Name}'. Пожалуйста, отключите его!");
                }
            }
        }
        catch { }
    }

    private static (string Gateway, int InterfaceIndex) GetDefaultGatewayInfo(string? targetIp = null)
    {
        LogNetworkDiagnostics();

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
                                Shared.Logging.AppLogger.Log($"[GATEWAY] Kernel socket FIB routed {targetStr} -> Local {localIp}, IfIndex: {ifIndex}, Gateway: {gw}");
                                t_savedPhysicalGateway = gw;
                                t_savedPhysicalIfIndex = ifIndex;
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
            Shared.Logging.AppLogger.Log($"[GATEWAY] Win32 GetBestRoute found gateway: '{win32Route.Gateway}', IfIndex: {win32Route.InterfaceIndex}");
            t_savedPhysicalGateway = win32Route.Gateway;
            t_savedPhysicalIfIndex = win32Route.InterfaceIndex;
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
                    Shared.Logging.AppLogger.Log($"[GATEWAY] PowerShell lowest-metric default route: '{parts[0]}', IfIndex: {psIf}");
                    t_savedPhysicalGateway = parts[0];
                    t_savedPhysicalIfIndex = psIf;
                    return (parts[0], psIf);
                }
            }
        }
        catch { }

        if (!string.IsNullOrEmpty(t_savedPhysicalGateway) && !t_savedPhysicalGateway.Contains(':') && t_savedPhysicalGateway != "0.0.0.0" && t_savedPhysicalIfIndex > 0)
        {
            return (t_savedPhysicalGateway, t_savedPhysicalIfIndex);
        }

        return ("", 0);
    }

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
            var (exitCode, output) = await RunCmdAsync("route", $"add {targetIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
            Debug.WriteLine($"[ROUTE] EnsureHostRoute for {targetIp} via {gw} (if {physicalIfIndex}): {exitCode} {output}");
            if (exitCode != 0 && physicalIfIndex > 0)
            {
                _ = await RunCmdAsync("route", $"add {targetIp} mask 255.255.255.255 {gw} metric 1");
            }
        }
    }

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
            Debug.WriteLine($"[ROUTE] SwitchHostRoute to {newIp} via {gw} (if {physicalIfIndex}): {exitCode} {output}");
            if (exitCode != 0 && physicalIfIndex > 0)
            {
                _ = await RunCmdAsync("route", $"add {newIp} mask 255.255.255.255 {gw} metric 1");
            }
        }
    }

    private static async Task SetWindowsRoutesAsync(string adapterName, IReadOnlyList<string> serverIps, string assignedIp, bool enable)
    {
        var primaryIp = serverIps.FirstOrDefault(ip => !string.IsNullOrEmpty(ip) && IPAddress.TryParse(ip, out _));
        var (gw, physicalIfIndex) = GetDefaultGatewayInfo(primaryIp);
        Debug.WriteLine($"[ROUTE] Default Gateway: {gw}, PhysicalIfIndex: {physicalIfIndex}, Name: {adapterName}, ServerIPs: {string.Join(',', serverIps)}, Enable: {enable}");

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
                foreach (var serverIp in serverIps)
                {
                    if (!string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _))
                    {
                        _ = await RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255");
                        var (exitCode, output) = await RunCmdAsync("route", $"add {serverIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
                        Debug.WriteLine($"[ROUTE] Add Server Route: {serverIp}, ExitCode {exitCode}, Output: {output}");
                        if (exitCode != 0 && physicalIfIndex > 0)
                        {
                            _ = await RunCmdAsync("route", $"add {serverIp} mask 255.255.255.255 {gw} metric 1");
                        }
                    }
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
                var r2Task = RunCmdAsync("route", $"add 0.0.0.0 mask 128.0.0.0 {tunGateway} metric 1 if {ifIndex}");
                var r3Task = RunCmdAsync("route", $"add 128.0.0.0 mask 128.0.0.0 {tunGateway} metric 1 if {ifIndex}");
                var results = await Task.WhenAll(r2Task, r3Task).ConfigureAwait(false);
                Debug.WriteLine($"[ROUTE] Add IPv4 Tun Routes: R2={results[0].exitCode}, R3={results[1].exitCode}");

                if (SplitTunnelPolicy.Enabled && !string.IsNullOrEmpty(gw) && gw != "0.0.0.0" && !gw.Contains(':'))
                {
                    foreach (var bypassIp in SplitTunnelPolicy.CustomBypassIps)
                    {
                        if (IPAddress.TryParse(bypassIp, out _))
                        {
                            _ = await RunCmdAsync("route", $"add {bypassIp} mask 255.255.255.255 {gw} metric 1{physIfArg}");
                            SplitTunnelPolicy.RecordBypassRoute(bypassIp, "Пользовательское исключение");
                        }
                    }

                    if (SplitTunnelPolicy.BypassRemoteManagement)
                    {
                        await ApplyRemoteManagementBypassRoutesAsync(gw, physIfArg);
                    }
                }

                _ = await RunCmdAsync("netsh", $"interface ipv6 add route ::/1 interface=\"{adapterName}\" metric=1", timeoutMs: 1500);
                _ = await RunCmdAsync("netsh", $"interface ipv6 add route 8000::/1 interface=\"{adapterName}\" metric=1", timeoutMs: 1500);
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

            foreach (var serverIp in serverIps)
            {
                if (!string.IsNullOrEmpty(serverIp) && IPAddress.TryParse(serverIp, out _))
                {
                    deleteTasks.Add(RunCmdAsync("route", $"delete {serverIp} mask 255.255.255.255"));
                }
            }

            foreach (var route in SplitTunnelPolicy.ActiveBypassRoutes)
            {
                deleteTasks.Add(RunCmdAsync("route", $"delete {route.DestinationIp} mask 255.255.255.255"));
            }
            SplitTunnelPolicy.ClearActiveRoutes();

            if (!string.IsNullOrEmpty(adapterName))
            {
                deleteTasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route ::/1 interface=\"{adapterName}\"", timeoutMs: 1500));
                deleteTasks.Add(RunCmdAsync("netsh", $"interface ipv6 delete route 8000::/1 interface=\"{adapterName}\"", timeoutMs: 1500));
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
            Debug.WriteLine($"[SPLIT-TUNNEL WARN] Remote management bypass check: {ex.Message}");
        }
    }

    public async Task StopVpnAsync()
    {
        _isExplicitlyStopped = true;
        _cts?.Cancel();

        if (CurrentState == AppVpnState.Disconnected && _adapter == null && !t_networkSettingsBoosted)
        {
            await OctopusEngine.Current.DisposeAsync().ConfigureAwait(false);
            return;
        }

        UpdateState(AppVpnState.Disconnecting);

        if (!await _vpnGate.WaitAsync(3000))
        {
            Debug.WriteLine("[WARN] StopVpnAsync timed out waiting for gate lock.");
        }
        try
        {
            OnLogUpdated?.Invoke("Отключение VPN...");

            var adapterToDispose = _adapter;
            var adapterName = _adapter?.Name ?? "";
            var serverIp = _currentServerIp;
            _adapter = null;

            try
            {
                OnLogUpdated?.Invoke("Восстановление сетевых настроек и DNS...");
                var routeTask = SetWindowsRoutesAsync(adapterName, _currentServerIpsToRoute.Count > 0 ? _currentServerIpsToRoute.ToArray() : (!string.IsNullOrEmpty(serverIp) ? [serverIp] : []), "", false);
                var dnsTask = DisableDnsLeakProtectionAsync();
                await Task.WhenAll(routeTask, dnsTask).ConfigureAwait(false);

                if (adapterToDispose is not null)
                {
                    try
                    {
                        adapterToDispose.Dispose();
                    }
                    catch { }

                    Debug.WriteLine("[DRIVER] Wintun adapter disposed.");
                }

                await RestoreOriginalNetworkSettingsAsync();
                await RestoreNeutralizedAdaptersAsync();
                await OctopusEngine.Current.DisposeAsync();
                Debug.WriteLine("[SYSTEM] VPN cleanup complete.");
                OnLogUpdated?.Invoke("[SYSTEM] VPN отключён.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[STOP ERROR] {ex.Message}");
            }

            UpdateState(AppVpnState.Disconnected);
        }
        finally
        {
            try
            {
                _ = _vpnGate.Release();
            }
            catch { }
        }
    }

    private static void ApplyExtremeNetworkBoost()
    {
        t_networkSettingsBoosted = true;
        _ = Task.Run(async () =>
        {
            try
            {
                _ = await RunCmdAsync("netsh", "int tcp set global autotuninglevel=experimental");
                _ = await RunCmdAsync("netsh", "int tcp set global ecncapability=enabled");
                _ = await RunCmdAsync("netsh", "int tcp set global rss=enabled");
                _ = await RunCmdAsync("netsh", "int tcp set global rsc=enabled");
                _ = await RunCmdAsync("netsh", "int tcp set global fastopen=enabled");
                _ = await RunCmdAsync("netsh", "int tcp set global timestamps=allowed");
                _ = await RunCmdAsync("netsh", "int tcp set global nonsackrttresiliency=disabled");
                _ = await RunCmdAsync("netsh", "int tcp set heuristics disabled");
                _ = await RunCmdAsync("netsh", "int tcp set supplemental template=internet congestionprovider=ctcp");
                Debug.WriteLine("[BOOST] Windows Network Stack accelerated safely to high performance.");
            }
            catch { }
        });
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
            _ = await Task.WhenAll(
                RunCmdAsync("netsh", "int tcp set global autotuninglevel=normal"),
                RunCmdAsync("netsh", "int tcp set global ecncapability=disabled"),
                RunCmdAsync("netsh", "int tcp set heuristics default")
            ).ConfigureAwait(false);
            Debug.WriteLine("[BOOST] Windows Network Stack restored to default.");
        }
        catch { }
    }

    private static async Task EnableDnsLeakProtectionAsync(string adapterName, string assignedIp)
    {
        try
        {
            var ifIndex = GetWintunInterfaceIndex(adapterName);
            var ifArg = ifIndex > 0 ? $" if {ifIndex}" : "";
            var tunGateway = "100.64.0.1";
            if (IPAddress.TryParse(assignedIp, out var parsedAssigned))
            {
                var bytes = parsedAssigned.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    tunGateway = $"{bytes[0]}.{bytes[1]}.0.1";
                }
            }
            var addTasks = new List<Task>();
            foreach (var dns in NetworkDefaults.TrustedDnsServers)
            {
                addTasks.Add(RunCmdAsync("route", $"add {dns} mask 255.255.255.255 {tunGateway} metric 1{ifArg}"));
            }
            await Task.WhenAll(addTasks);

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient");
                key?.SetValue("DisableSmartNameResolution", 1, Microsoft.Win32.RegistryValueKind.DWord);
                key?.SetValue("EnableMulticast", 0, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }

            try
            {
                using var dcacheKey = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"System\CurrentControlSet\Services\Dnscache\Parameters");
                dcacheKey?.SetValue("DisableParallelAandAAAA", 1, Microsoft.Win32.RegistryValueKind.DWord);
            }
            catch { }

            _ = await RunCmdAsync("powershell", "-NoProfile -ExecutionPolicy Bypass -Command \"try { Get-DnsClientNrptRule | Where-Object { $_.DisplayName -eq 'Obxodka-DNS' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue } catch { }; try { Add-DnsClientNrptRule -Namespace '.' -NameServers '1.1.1.1','8.8.8.8' -DisplayName 'Obxodka-DNS' -ErrorAction Stop } catch { }\"");

            _ = await RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-LAN\"");
            _ = await RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-WiFi\"");
            _ = await RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-LAN-TCP\"");
            _ = await RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-WiFi-TCP\"");

            _ = await RunCmdAsync("ipconfig", "/flushdns");
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
            var delTasks = new List<Task>();
            foreach (var dns in NetworkDefaults.TrustedDnsServers)
            {
                delTasks.Add(RunCmdAsync("route", $"delete {dns} mask 255.255.255.255"));
            }

            delTasks.Add(RunCmdAsync("powershell", "-NoProfile -ExecutionPolicy Bypass -Command \"try { Get-DnsClientNrptRule | Where-Object { $_.DisplayName -eq 'Obxodka-DNS' } | Remove-DnsClientNrptRule -Force -ErrorAction SilentlyContinue } catch { }\""));
            delTasks.Add(RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-LAN\""));
            delTasks.Add(RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-WiFi\""));
            delTasks.Add(RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-LAN-TCP\""));
            delTasks.Add(RunCmdAsync("netsh", "advfirewall firewall delete rule name=\"Obxodka-DnsLeak-Block-WiFi-TCP\""));

            await Task.WhenAll(delTasks).ConfigureAwait(false);

            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"Software\Policies\Microsoft\Windows NT\DNSClient", true);
                key?.DeleteValue("DisableSmartNameResolution", false);
                key?.DeleteValue("EnableMulticast", false);
            }
            catch { }

            try
            {
                using var dcacheKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"System\CurrentControlSet\Services\Dnscache\Parameters", true);
                dcacheKey?.DeleteValue("DisableParallelAandAAAA", false);
            }
            catch { }

            _ = Task.Run(() => RunCmdAsync("ipconfig", "/flushdns"));
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
