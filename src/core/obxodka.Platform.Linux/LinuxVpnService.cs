namespace obxodka.Platforms.Linux;

//платформенный драйвер linux (/dev/net/tun, маршрутизация ip и защита dns) для единого оркестратора vpn
[SupportedOSPlatform("linux")]
public sealed class LinuxVpnService : ITunnelPlatformAdapter, IDisposable
{
    private const int MaxPacketSize = 65536;
    private readonly Lock _fdLock = new();
    private readonly ConcurrentDictionary<string, byte> _appliedBypassIps = new(StringComparer.OrdinalIgnoreCase);

    private int _tunFd = -1;
    private string _tunName = LinuxNetworkRoutingHelper.DefaultTunName;
    private string? _physicalGateway;
    private string? _physicalInterface;
    private bool _usedResolvectl;
    private bool _ipv6RoutesApplied;
    private Task? _trafficTask;
    private volatile bool _isDisposed;
    private Action? _platformNetworkChanged;

    public bool SupportsKillSwitch => true;

    public event Action? OnPlatformNetworkChanged
    {
        add => _platformNetworkChanged += value;
        remove => _platformNetworkChanged -= value;
    }

    //поиск активных конфликтующих vpn интерфейсов в /sys/class/net
    public string? DetectConflictingVpn()
    {
        try
        {
            if (!Directory.Exists("/sys/class/net"))
            {
                return null;
            }

            foreach (var dir in Directory.EnumerateDirectories("/sys/class/net"))
            {
                var name = Path.GetFileName(dir);
                if (string.Equals(name, _tunName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (name.StartsWith("wg", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("tailscale", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("nordlynx", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("tun", StringComparison.OrdinalIgnoreCase))
                {
                    var operPath = Path.Combine(dir, "operstate");
                    var state = File.Exists(operPath) ? File.ReadAllText(operPath).Trim() : string.Empty;
                    if (state is "up" or "unknown")
                    {
                        return $"Обнаружен активный VPN-интерфейс '{name}'. Рекомендуется отключить его перед запуском Obxodka.";
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    //предстартовая проверка прав, устройства /dev/net/tun и маршрута по умолчанию
    public async Task<string?> RunNetworkPreflightAsync()
    {
        if (!File.Exists(LinuxTunNative.TunDevicePath))
        {
            return $"Устройство {LinuxTunNative.TunDevicePath} не найдено. Загрузите модуль ядра: sudo modprobe tun";
        }

        if (!LinuxTunNative.HasNetAdminPrivilege())
        {
            return "Недостаточно прав для создания TUN-интерфейса. Запустите через sudo или выдайте CAP_NET_ADMIN (sudo setcap cap_net_admin+ep ./obxodka).";
        }

        var ok = await RefreshPhysicalGatewayAsync(CancellationToken.None).ConfigureAwait(false);
        return ok ? null : "Не найден активный маршрут по умолчанию (default route). Проверьте подключение к интернету.";
    }

    //проверка системных разрешений перед стартом
    public Task<bool> PreparePermissionsAndStartServiceAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(LinuxTunNative.TunDevicePath) && LinuxTunNative.HasNetAdminPrivilege());
    }

    //очистка устаревших маршрутов и бэкапов dns перед новым подключением
    public async Task CleanupBeforeConnectAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await RestoreDnsAsync(ct).ConfigureAwait(false);
        await RemoveTunnelRoutesAsync(ct).ConfigureAwait(false);
        _ = await RefreshPhysicalGatewayAsync(ct).ConfigureAwait(false);
    }

    //предварительная установка /32 bypass-маршрутов до vpn серверов
    public async Task PrepareBypassHostsAsync(string[] ips, CancellationToken ct)
    {
        if (ips is null || ips.Length == 0)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_physicalInterface))
        {
            _ = await RefreshPhysicalGatewayAsync(ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(_physicalInterface))
        {
            return;
        }

        foreach (var ip in ips)
        {
            ct.ThrowIfCancellationRequested();
            if (!IPAddress.TryParse(ip, out var parsed) || parsed.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var args = LinuxNetworkRoutingHelper.BuildBypassRouteArgs("replace", parsed.ToString(), _physicalGateway, _physicalInterface);
            var (exitCode, _, stderr) = await LinuxNetworkRoutingHelper.RunProcessAsync("ip", args, ct).ConfigureAwait(false);
            if (exitCode == 0)
            {
                _appliedBypassIps[parsed.ToString()] = 1;
            }
            else
            {
                AppLogger.LogWarning($"[LINUX ROUTE] Не удалось добавить bypass {parsed}/32: {stderr.Trim()}");
            }
        }
    }

    //привязка транспортного сокета к физическому интерфейсу через so_bindtodevice
    public void BindPhysicalSocket(Socket socket)
    {
        var phys = _physicalInterface;
        if (string.IsNullOrWhiteSpace(phys))
        {
            return;
        }

        if (!LinuxTunNative.TryBindSocketToDevice(socket, phys, out var errno))
        {
            AppLogger.LogWarning($"[LINUX SOCK] SO_BINDTODEVICE('{phys}') вернул errno={errno}.");
        }
    }

    //открытие и настройка виртуального tun интерфейса (ip, mtu, up)
    public async Task ApplyTunnelInterfaceAsync(ProtocolRouteStep step, string assignedIpv4, string? assignedIpv6, int mtu, CancellationToken ct)
    {
        _ = step;
        ct.ThrowIfCancellationRequested();

        EnsureTunOpened();
        var effectiveMtu = Math.Clamp(mtu > 0 ? mtu : SmartMtuEngine.CurrentMtu, SmartMtuEngine.MinMtu, 1500);

        await RunIpLoggedAsync(["addr", "flush", "dev", _tunName], ct).ConfigureAwait(false);
        await RunIpLoggedAsync(["addr", "replace", $"{assignedIpv4}/10", "dev", _tunName], ct).ConfigureAwait(false);

        var blockIpv6 = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
        if (!blockIpv6 && !string.IsNullOrWhiteSpace(assignedIpv6) && IPAddress.TryParse(assignedIpv6, out var parsedV6) &&
            parsedV6.AddressFamily == AddressFamily.InterNetworkV6)
        {
            await RunIpLoggedAsync(["-6", "addr", "replace", $"{parsedV6}/64", "dev", _tunName], ct).ConfigureAwait(false);
        }

        await RunIpLoggedAsync(["link", "set", "dev", _tunName, "mtu", effectiveMtu.ToString(CultureInfo.InvariantCulture), "up"], ct)
            .ConfigureAwait(false);
        AppLogger.Log($"[LINUX TUN] Интерфейс '{_tunName}' поднят: IPv4={assignedIpv4}/10, MTU={effectiveMtu}.");
    }

    //запуск цикла прокачки пакетов между /dev/net/tun и движком octopus
    public void StartPacketPump(CancellationToken ct) =>
        _trafficTask = Task.Run(() => ProcessTrafficAsync(ct), CancellationToken.None);

    //включение системных маршрутов в туннель и защиты от утечек dns
    public async Task ActivateRoutingAndLeakGuardAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, CancellationToken ct)
    {
        _ = assignedIpv4;
        ct.ThrowIfCancellationRequested();

        if (serverIps.Count > 0)
        {
            await PrepareBypassHostsAsync([.. serverIps], ct).ConfigureAwait(false);
        }

        var blockIpv6 = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
        var hasIpv6 = !blockIpv6 && !string.IsNullOrWhiteSpace(assignedIpv6) &&
                      IPAddress.TryParse(assignedIpv6, out var parsedV6) &&
                      parsedV6.AddressFamily == AddressFamily.InterNetworkV6;
        _ipv6RoutesApplied = hasIpv6;

        foreach (var args in LinuxNetworkRoutingHelper.BuildTunnelRouteArgs("replace", _tunName, hasIpv6))
        {
            await RunIpLoggedAsync(args, ct).ConfigureAwait(false);
        }

        await ApplyDnsLeakProtectionAsync(hasIpv6, ct).ConfigureAwait(false);
    }

    //обновление адреса и маршрутов при бесшовной ротации протокола
    public async Task SyncTunnelIpAndRoutesAsync(IReadOnlyList<string> serverIps, string assignedIpv4, string? assignedIpv6, int mtu, CancellationToken ct)
    {
        await ApplyTunnelInterfaceAsync(default!, assignedIpv4, assignedIpv6, mtu, ct).ConfigureAwait(false);
        await ActivateRoutingAndLeakGuardAsync(serverIps, assignedIpv4, assignedIpv6, ct).ConfigureAwait(false);
    }

    //обновление mtu на лету
    public void UpdateAdapterMtu(int mtu)
    {
        if (_tunFd < 0 || _isDisposed)
        {
            return;
        }

        var clamped = Math.Clamp(mtu, SmartMtuEngine.MinMtu, 1500);
        _ = Task.Run(() => RunIpLoggedAsync(["link", "set", "dev", _tunName, "mtu", clamped.ToString(CultureInfo.InvariantCulture)], CancellationToken.None));
    }

    //обработка потери физической сети
    public async Task HandlePhysicalUplinkLostAsync()
    {
        _physicalGateway = null;
        _physicalInterface = null;
        if (!PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, true))
        {
            await RemoveTunnelRoutesAsync(CancellationToken.None).ConfigureAwait(false);
            await RestoreDnsAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    //штатное отключение адаптера, восстановление маршрутов и dns
    public async Task ShutdownTunnelAsync(IReadOnlyList<string> serverIps, string assignedIpv4, bool keepKillSwitch)
    {
        _ = serverIps;
        _ = assignedIpv4;

        if (keepKillSwitch)
        {
            AppLogger.Log("[LINUX TUN] Kill Switch активен: маршруты туннеля сохранены для блокировки утечек.");
            return;
        }

        await RestoreDnsAsync(CancellationToken.None).ConfigureAwait(false);
        await RemoveTunnelRoutesAsync(CancellationToken.None).ConfigureAwait(false);
        await RemoveBypassRoutesAsync(CancellationToken.None).ConfigureAwait(false);
        CloseTunDevice();

        if (_trafficTask is not null)
        {
            try
            {
                await _trafficTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _trafficTask = null;
        }
    }

    //освобождение ресурсов linux драйвера
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        CloseTunDevice();
    }

    //чтение и запись сырых ip пакетов между /dev/net/tun и octopusengine
    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        var pipeline = OctopusEngine.Current.Pipeline;
        pipeline.AdBlockFilter = static (buf, len) => DnsAdBlocker.ProcessPacket(buf, len);
        pipeline.LocalDelivery = WritePacketToTun;

        OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        OctopusEngine.Current.OnPacketReceived += HandlePacketFromVpn;

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var rxThread = new Thread(() =>
        {
            Thread.CurrentThread.Name = "LinuxTun-UploadReader";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var fd = Volatile.Read(ref _tunFd);
                    if (fd < 0)
                    {
                        break;
                    }

                    var buffer = ArrayPool<byte>.Shared.Rent(MaxPacketSize);
                    nint bytesRead;
                    unsafe
                    {
                        fixed (byte* ptr = buffer)
                        {
                            bytesRead = LinuxTunNative.Read(fd, (nint)ptr, MaxPacketSize);
                        }
                    }

                    if (bytesRead <= 0)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        if (ct.IsCancellationRequested || Volatile.Read(ref _tunFd) < 0)
                        {
                            break;
                        }

                        Thread.Sleep(5);
                        continue;
                    }

                    OctopusEngine.Current.SendPacketFromPool(buffer, (int)bytesRead);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLogger.LogWarning($"[LINUX TUN] Остановка цикла чтения: {ex.Message}");
            }
            finally
            {
                _ = tcs.TrySetResult();
            }
        })
        {
            IsBackground = true
        };

        rxThread.Start();
        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            OctopusEngine.Current.OnPacketReceived -= HandlePacketFromVpn;
        }
    }

    //обработка входящего пакета из туннеля vpn
    private void HandlePacketFromVpn(byte[] data, int length) =>
        OctopusEngine.Current.Pipeline.ProcessIncomingPacket(data, length, WritePacketToTun);

    //запись ip пакета в файловый дескриптор tun
    private void WritePacketToTun(byte[] data, int length)
    {
        if (length <= 0 || length > data.Length)
        {
            return;
        }

        var fd = Volatile.Read(ref _tunFd);
        if (fd < 0)
        {
            return;
        }

        unsafe
        {
            fixed (byte* ptr = data)
            {
                _ = LinuxTunNative.Write(fd, (nint)ptr, (nuint)length);
            }
        }
    }

    //открытие tun дескриптора при необходимости
    private void EnsureTunOpened()
    {
        lock (_fdLock)
        {
            if (_tunFd >= 0)
            {
                return;
            }

            var (fd, actualName) = LinuxTunNative.OpenTunDevice(_tunName);
            _tunFd = fd;
            _tunName = actualName;
        }
    }

    //закрытие tun дескриптора
    private void CloseTunDevice()
    {
        lock (_fdLock)
        {
            if (_tunFd < 0)
            {
                return;
            }

            var fd = _tunFd;
            _tunFd = -1;
            _ = LinuxTunNative.Close(fd);
        }
    }

    //определение текущего физического шлюза и интерфейса по умолчанию
    private async Task<bool> RefreshPhysicalGatewayAsync(CancellationToken ct)
    {
        var (exitCode, stdout, _) = await LinuxNetworkRoutingHelper.RunProcessAsync("ip", ["-4", "route", "show", "default"], ct)
            .ConfigureAwait(false);
        if (exitCode != 0)
        {
            return false;
        }

        if (!LinuxNetworkRoutingHelper.TryParseDefaultRoute(stdout, out var gw, out var dev))
        {
            return false;
        }

        if (string.Equals(dev, _tunName, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(_physicalInterface);
        }

        _physicalGateway = gw;
        _physicalInterface = dev;
        AppLogger.Log($"[LINUX NET] Физический маршрут: dev={dev}, gw={gw ?? "direct"}.");
        return true;
    }

    //включение защиты от утечек dns через resolvectl или атомарный бэкап /etc/resolv.conf
    private async Task ApplyDnsLeakProtectionAsync(bool includeIpv6, CancellationToken ct)
    {
        List<string> dnsServers = [DnsDefaults.PrimaryDns, DnsDefaults.SecondaryDns];
        if (includeIpv6)
        {
            dnsServers.Add(DnsDefaults.PrimaryDnsV6);
            dnsServers.Add(DnsDefaults.SecondaryDnsV6);
        }

        try
        {
            List<string> resolvArgs = ["dns", _tunName, .. dnsServers];
            var (dnsExit, _, _) = await LinuxNetworkRoutingHelper.RunProcessAsync("resolvectl", resolvArgs, ct).ConfigureAwait(false);
            if (dnsExit == 0)
            {
                _ = await LinuxNetworkRoutingHelper.RunProcessAsync("resolvectl", ["domain", _tunName, "~."], ct).ConfigureAwait(false);
                _usedResolvectl = true;
                AppLogger.Log($"[LINUX DNS] Применён DNS через systemd-resolved ({_tunName}): {string.Join(", ", dnsServers)}.");
                return;
            }
        }
        catch
        {
            //переходим на фоллбэк /etc/resolv.conf если resolvectl недоступен
        }

        _usedResolvectl = false;
        try
        {
            LinuxNetworkRoutingHelper.ApplyResolvConfOverride(
                LinuxNetworkRoutingHelper.DefaultResolvConfPath,
                LinuxNetworkRoutingHelper.DefaultResolvBackupPath,
                dnsServers);
            AppLogger.Log($"[LINUX DNS] Применён /etc/resolv.conf с бэкапом в {LinuxNetworkRoutingHelper.DefaultResolvBackupPath}.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLogger.LogWarning($"[LINUX DNS] Не удалось обновить /etc/resolv.conf: {ex.Message}");
        }
    }

    //откат настроек dns
    private async Task RestoreDnsAsync(CancellationToken ct)
    {
        if (_usedResolvectl)
        {
            try
            {
                _ = await LinuxNetworkRoutingHelper.RunProcessAsync("resolvectl", ["revert", _tunName], ct).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }

            _usedResolvectl = false;
        }

        try
        {
            if (LinuxNetworkRoutingHelper.RestoreResolvConfBackup(
                LinuxNetworkRoutingHelper.DefaultResolvConfPath,
                LinuxNetworkRoutingHelper.DefaultResolvBackupPath))
            {
                AppLogger.Log("[LINUX DNS] Восстановлен оригинальный /etc/resolv.conf.");
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            AppLogger.LogWarning($"[LINUX DNS] Ошибка восстановления /etc/resolv.conf: {ex.Message}");
        }
    }

    //удаление перехватывающих маршрутов туннеля
    private async Task RemoveTunnelRoutesAsync(CancellationToken ct)
    {
        foreach (var args in LinuxNetworkRoutingHelper.BuildTunnelRouteArgs("del", _tunName, _ipv6RoutesApplied))
        {
            try
            {
                _ = await LinuxNetworkRoutingHelper.RunProcessAsync("ip", args, ct).ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        }

        _ipv6RoutesApplied = false;
    }

    //удаление ранее добавленных /32 bypass-маршрутов
    private async Task RemoveBypassRoutesAsync(CancellationToken ct)
    {
        var phys = _physicalInterface;
        foreach (var ip in _appliedBypassIps.Keys)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(phys))
                {
                    var args = LinuxNetworkRoutingHelper.BuildBypassRouteArgs("del", ip, _physicalGateway, phys);
                    _ = await LinuxNetworkRoutingHelper.RunProcessAsync("ip", args, ct).ConfigureAwait(false);
                }
            }
            catch
            {
                // ignored
            }
            finally
            {
                _ = _appliedBypassIps.TryRemove(ip, out _);
            }
        }
    }

    //выполнение команды ip с логированием ошибок
    private static async Task RunIpLoggedAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await LinuxNetworkRoutingHelper.RunProcessAsync("ip", args, ct).ConfigureAwait(false);
        if (exitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
        {
            AppLogger.LogWarning($"[LINUX IP] ip {string.Join(' ', args)} -> {stderr.Trim()}");
        }
    }
}
