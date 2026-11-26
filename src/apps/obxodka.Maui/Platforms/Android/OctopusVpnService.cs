using _Microsoft.Android.Resource.Designer;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using Application = Android.App.Application;
using Environment = System.Environment;
using OperationCanceledException = System.OperationCanceledException;
using ThreadPriority = System.Threading.ThreadPriority;

namespace obxodka;

//системная служба android vpn service для управления туннелем tun0 и защиты сокетов
[Service(
    Name = "obxodka.OctopusVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
    ForegroundServiceType = ForegroundService.TypeSpecialUse)]
[IntentFilter(["android.net.VpnService"])]
[SupportedOSPlatform("android30.0")]
public sealed partial class OctopusVpnService : VpnService
{
    private const string ChannelId = "obxodka_vpn_channel";
    private static readonly int s_notificationId = DateTime.UtcNow.Year;

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateBounded<(byte[] buffer, int length)>(
            new BoundedChannelOptions(2048)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait
            });

    private readonly Lock _tunLock = new();
    private readonly Lock _wakeLockSync = new();
    private string? _currentTunIp;
    private string? _currentTunIpV6;
    private int _currentTunMtu;
    private volatile bool _isTunRunning;

    private VpnNetworkCallback? _networkCallback;
    private int _tunGeneration;
    private PowerManager.WakeLock? _wakeLock;
    private long _wakeLockRenewTicks;

    static OctopusVpnService() => HookProtection();

    public static OctopusVpnService? Instance { get; private set; }
    public bool IsActive { get; private set; }
    public static TaskCompletionSource<OctopusVpnService>? ServiceReadyTcs { get; set; }
    public static string? LastEstablishError { get; private set; }

    //привязка платформенной защиты сокетов к ядру
    public static void HookProtection() =>
        OctopusEngine.Current.ProtectTransportSockets(sock => ProtectSocket(sock));

    //ожидание готовности службы vpn для избежания гонок при старте
    public static async Task<OctopusVpnService?> WaitForServiceReadyAsync(int timeoutMs = 4000, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Instance is { IsActive: true } readyService)
        {
            return readyService;
        }

        var tcs = new TaskCompletionSource<OctopusVpnService>(TaskCreationOptions.RunContinuationsAsynchronously);
        ServiceReadyTcs = tcs;

        if (Instance is { IsActive: true } doubleCheck)
        {
            return doubleCheck;
        }

        try
        {
            return await tcs.Task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Instance;
        }
    }

    [LibraryImport("libc", EntryPoint = "read", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint Read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial nint Write(int fd, byte* buf, nuint count);

    //удержание блокировки процессора для предотвращения засыпания сокетов в фоне
    private void AcquireWakeLock()
    {
        lock (_wakeLockSync)
        {
            if (_wakeLock is null)
            {
                var powerManager = (PowerManager?)GetSystemService(PowerService);
                _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "obxodka::VpnWakeLock");
                _wakeLock?.SetReferenceCounted(false);
            }

            if (_wakeLock is null)
            {
                return;
            }

            _wakeLock.Acquire(3_600_000L);
            Volatile.Write(ref _wakeLockRenewTicks, Environment.TickCount64);
        }
    }

    private void EnsureWakeLockHeld()
    {
        var now = Environment.TickCount64;
        if (_wakeLock is not null && now - Volatile.Read(ref _wakeLockRenewTicks) < 300_000L)
        {
            return;
        }

        try
        {
            AcquireWakeLock();
        }
        catch
        {
            // ignored
        }
    }

    private void ReleaseWakeLock()
    {
        lock (_wakeLockSync)
        {
            if (_wakeLock is { IsHeld: true })
            {
                _wakeLock.Release();
            }

            Volatile.Write(ref _wakeLockRenewTicks, 0);
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        HookProtection();
    }

    public override void OnDestroy()
    {
        IsActive = false;
        if (Instance != null && Instance.Equals(this))
        {
            Instance = null;
        }

        StopNativeVpn();
        base.OnDestroy();
    }

    public override void OnRevoke()
    {
        StopNativeVpn();
        _ = Task.Run(async () =>
        {
            try
            {
                await AndroidVpnService.Orchestrator.StopVpnAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignored
            }
        });
        base.OnRevoke();
    }

    //защита сокета от попадания в собственный vpn интерфейс tun0
    public static bool ProtectSocket(Socket? sock)
    {
        if (sock == null)
        {
            return false;
        }

        var service = Instance;
        if (service == null)
        {
            AppLogger.LogWarning("[VPN PROTECT WARNING] Instance is null, socket cannot be protected yet!");
            return false;
        }

        try
        {
            var fd = (int)sock.Handle;
            var ok = service.Protect(fd);
            if (!ok)
            {
                AppLogger.LogWarning($"[VPN PROTECT WARNING] service.Protect(fd={fd}) returned false!");
            }
            else
            {
                AppLogger.Log($"[VPN PROTECT] Socket fd={fd} successfully protected from VPN routing loop.");
            }

            return ok;
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[VPN PROTECT ERROR] {ex.Message}");
            return false;
        }
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        Instance = this;
        IsActive = true;
        HookProtection();

        RegisterNetworkCallback();
        CreateNotificationChannel();

        if (OperatingSystem.IsAndroidVersionAtLeast(34))
        {
            StartForeground(s_notificationId, CreateNotification(), ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(s_notificationId, CreateNotification());
        }

        OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
        OctopusEngine.Current.OnPacketReceived += InjectPacketToAndroid;

        _ = ServiceReadyTcs?.TrySetResult(this);

        return StartCommandResult.Sticky;
    }

    private void RegisterNetworkCallback()
    {
        try
        {
            UnregisterNetworkCallback();

            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (cm is null)
            {
                return;
            }

            _networkCallback = new VpnNetworkCallback();
            if (OperatingSystem.IsAndroidVersionAtLeast(24))
            {
                cm.RegisterDefaultNetworkCallback(_networkCallback);
            }
            else
            {
                using var builder = new NetworkRequest.Builder();
                var request = builder
                    .AddCapability(NetCapability.Internet)?
                    .AddCapability(NetCapability.NotVpn)?
                    .Build();

                if (request is not null)
                {
                    cm.RegisterNetworkCallback(request, _networkCallback);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[NETWORK CALLBACK ERROR] {ex.Message}");
        }
    }

    private void UnregisterNetworkCallback()
    {
        try
        {
            var callback = _networkCallback;
            _networkCallback = null;
            if (callback is not null)
            {
                callback.IsActive = false;
                var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
                cm?.UnregisterNetworkCallback(callback);
                callback.Dispose();
            }
        }
        catch
        {
            // ignored
        }
    }

    private void CreateNotificationChannel()
    {
        using var channel = new NotificationChannel(ChannelId, "Obxodka VPN Status", NotificationImportance.Low);
        channel.Description = "Показывает статус подключения";
        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.CreateNotificationChannel(channel);
    }

    //формирование системного уведомления с локализованным статусом из ядра
    private Notification CreateNotification()
    {
        using var launchIntent = new Intent(this, typeof(MainActivity));
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            launchIntent,
            PendingIntentFlags.Immutable);

        using var builder = new NotificationCompat.Builder(this, ChannelId);
        _ = builder.SetContentTitle("Obxodka");
        _ = builder.SetContentText(VpnStatusMessages.ProtectedConnectionActive);
        _ = builder.SetSmallIcon(ResourceConstant.Drawable.notification_icon);
        _ = builder.SetOngoing(true);
        _ = builder.SetContentIntent(pendingIntent);

        return builder.Build()!;
    }

    //инжекция входящего пакета от сервера в сетевой стек android
    private void InjectPacketToAndroid(byte[] packet, int length)
    {
        if (!_isTunRunning)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_downstreamChannel.Writer.TryWrite((packet, length)))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    //обновление mtu существующего tun интерфейса при адаптивной подстройке ядра
    public void OnMtuUpdated(int newMtu)
    {
        var clampedMtu = Math.Clamp(newMtu, SmartMtuEngine.MinMtu, 1400);
        if (!_isTunRunning || string.IsNullOrEmpty(_currentTunIp))
        {
            return;
        }

        if (clampedMtu <= _currentTunMtu)
        {
            //при снижении mtu конвейер PacketPipeline сам зажимает tcp mss и фрагментирует пакеты без разрыва активных сокетов tun0
            AppLogger.Log(
                $"[VPN MTU] Applying downshifted MTU {clampedMtu}B in PacketPipeline without tearing down active tun0 ({_currentTunMtu}B).");
            return;
        }

        if (clampedMtu - _currentTunMtu < 20)
        {
            return;
        }

        AppLogger.Log($"[VPN MTU] Applying updated TUN MTU on Android: {_currentTunMtu} -> {clampedMtu}");
        _ = EstablishTun(true);
    }

    //инициализация и подъем виртуального интерфейса tun0 с настройками dns из ядра
    public bool EstablishTun(
        bool forceRebuild = false,
        string? assignedIpv4 = null,
        string? assignedIpv6 = null,
        int? mtu = null)
    {
        lock (_tunLock)
        {
            try
            {
                var ip = !string.IsNullOrWhiteSpace(assignedIpv4) ? assignedIpv4 : OctopusEngine.Current.AssignedIp;
                if (string.IsNullOrEmpty(ip))
                {
                    return false;
                }

                var blockIpv6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
                var ip6 = !string.IsNullOrWhiteSpace(assignedIpv6) ? assignedIpv6 : OctopusEngine.Current.AssignedIpV6;
                var effectiveV6 = !blockIpv6 && !string.IsNullOrEmpty(ip6) && IPAddress.TryParse(ip6, out var parsedV6) &&
                                  parsedV6.AddressFamily == AddressFamily.InterNetworkV6
                    ? ip6
                    : "fd00::2";

                if (!forceRebuild &&
                    _tunInterface != null &&
                    _currentTunIp == ip &&
                    _currentTunIpV6 == effectiveV6 &&
                    _isTunRunning &&
                    _txThread is { IsAlive: true } &&
                    _rxThread is { IsAlive: true })
                {
                    AppLogger.Log(
                        $"[VPN ESTABLISH] TUN already active for {ip}/32 ({effectiveV6}) with healthy worker threads. Preserving tunnel interface.");
                    return true;
                }

                _isTunRunning = false;
                _currentTunIp = null;
                _currentTunIpV6 = null;

                var oldCts = _vpnCts;
                _vpnCts = null;
                var oldTun = _tunInterface;
                _tunInterface = null;
                var oldTx = _txThread;
                _txThread = null;
                var oldRx = _rxThread;
                _rxThread = null;

                try
                {
                    oldCts?.Cancel();
                }
                catch
                {
                    // ignored
                }

                try
                {
                    oldTun?.Close();
                }
                catch
                {
                    // ignored
                }

                try
                {
                    if (oldTx is { IsAlive: true } && Thread.CurrentThread != oldTx)
                    {
                        _ = oldTx.Join(300);
                    }

                    if (oldRx is { IsAlive: true } && Thread.CurrentThread != oldRx)
                    {
                        _ = oldRx.Join(300);
                    }
                }
                catch
                {
                    // ignored
                }

                while (_downstreamChannel.Reader.TryRead(out var stale))
                {
                    ArrayPool<byte>.Shared.Return(stale.buffer);
                }

                try
                {
                    oldCts?.Dispose();
                    oldTun?.Dispose();
                }
                catch
                {
                    // ignored
                }

                _vpnCts = new CancellationTokenSource();
                var ct = _vpnCts.Token;

                using var builder = new Builder(this);
                var targetMtu = Math.Clamp(mtu ?? SmartMtuEngine.CurrentMtu, SmartMtuEngine.MinMtu, 1400);
                _ = builder
                    .SetSession("Obxodka")
                    .AddAddress(ip, 32)
                    .SetMtu(targetMtu)
                    .SetBlocking(true)
                    .AddRoute("0.0.0.0", 0);

                try
                {
                    _ = builder.AddAddress(effectiveV6, 64);
                }
                catch
                {
                    try
                    {
                        _ = builder.AddAddress(effectiveV6, 128);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[VPN V6 ADDR ERROR] {ex.Message}");
                    }
                }

                try
                {
                    _ = builder.AddRoute("::", 0);
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[VPN V6 ROUTE ERROR] {ex.Message}");
                }

                //конфигурация проверенных серверов dns из единого источника истины
                foreach (var dns in DnsDefaults.TunnelDnsServersIPv4)
                {
                    _ = builder.AddDnsServer(dns);
                }

                if (!blockIpv6)
                {
                    foreach (var dns6 in DnsDefaults.TunnelDnsServersIPv6)
                    {
                        try
                        {
                            _ = builder.AddDnsServer(dns6);
                        }
                        catch
                        {
                            // ignored
                        }
                    }
                }

                var bypassed = new AppManager().GetBypassedPackages();
                foreach (var pkg in bypassed)
                {
                    try
                    {
                        _ = builder.AddDisallowedApplication(pkg);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                try
                {
                    _ = builder.AddDisallowedApplication(PackageName ?? "com.octocore.obxodka");
                }
                catch
                {
                    // ignored
                }

                _tunInterface = builder.Establish();
                if (_tunInterface is { FileDescriptor: not null })
                {
                    AcquireWakeLock();
                    var gen = Interlocked.Increment(ref _tunGeneration);
                    _currentTunIp = ip;
                    _currentTunIpV6 = effectiveV6;
                    _currentTunMtu = targetMtu;
                    _isTunRunning = true;

                    var tunFd = _tunInterface.Fd;
                    var pipeline = OctopusEngine.Current.Pipeline;
                    pipeline.AdBlockFilter = (buf, len) => DnsAdBlocker.ProcessPacket(buf, len);
                    pipeline.LocalDelivery = (buf, len) =>
                    {
                        var copy = ArrayPool<byte>.Shared.Rent(len);
                        Buffer.BlockCopy(buf, 0, copy, 0, len);
                        if (!_downstreamChannel.Writer.TryWrite((copy, len)))
                        {
                            ArrayPool<byte>.Shared.Return(copy);
                        }
                    };

                    _txThread = new Thread(() => ProcessTraffic(tunFd, gen, ct))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                        Name = "AndroidTunReader"
                    };
                    _txThread.Start();

                    _rxThread = new Thread(() => ProcessDownstreamTraffic(tunFd, gen, ct))
                    {
                        IsBackground = true,
                        Priority = ThreadPriority.Highest,
                        Name = "AndroidTunWriter"
                    };
                    _rxThread.Start();

                    AppLogger.Log(
                        $"[VPN ESTABLISH] TUN successfully established for {ip}/32 (MTU {Math.Min(SmartMtuEngine.CurrentMtu, 1400)})");
                    LastEstablishError = null;
                    return true;
                }

                var prepareNeeded = Prepare(this) != null;
                AppLogger.LogError($"[VPN ESTABLISH ERROR] builder.Establish() returned null! Prepare needed: {prepareNeeded}");
                LastEstablishError = prepareNeeded
                    ? "VPN разрешение отозвано системой или занято другим приложением."
                    : "Система Android не смогла выделить дескриптор TUN интерфейса.";
                return false;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[VPN ESTABLISH ERROR] {ex.Message}");
                LastEstablishError = $"Ошибка инициализации TUN: {ex.Message}";
                return false;
            }
        }
    }

    //поток записи входящего сетевого трафика в интерфейс tun0
    private void ProcessDownstreamTraffic(int tunFd, int gen, CancellationToken ct)
    {
        var reader = _downstreamChannel.Reader;
        var pipeline = OctopusEngine.Current.Pipeline;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureWakeLockHeld();
                while (reader.TryRead(out var item))
                {
                    pipeline.ProcessIncomingPacket(item.buffer, item.length, (buf, len) =>
                    {
                        try
                        {
                            unsafe
                            {
                                fixed (byte* ptr = buf)
                                {
                                    var written = Write(tunFd, ptr, (nuint)len);
                                    if (written >= 0)
                                    {
                                        return;
                                    }

                                    var errno = Marshal.GetLastPInvokeError();
                                    if (errno == 4)
                                    {
                                        _ = Write(tunFd, ptr, (nuint)len);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            AppLogger.LogError($"[TUN WRITE ERROR] {ex.Message}");
                        }
                    });
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
#pragma warning disable RS0030 //выделенный системный поток чтения tun android не использует threadpool
                    if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
#pragma warning restore RS0030
                    {
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (AggregateException ae) when (ae.InnerException is OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TUN DOWNSTREAM ERROR] {ex.Message}");
        }
        finally
        {
            if (Volatile.Read(ref _tunGeneration) == gen)
            {
                _isTunRunning = false;
            }
        }
    }

    //поток чтения исходящих пакетов устройств из tun0 и отправка в очередь octopus
    private void ProcessTraffic(int tunFd, int gen, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureWakeLockHeld();
                var buffer = ArrayPool<byte>.Shared.Rent(16384);
                nint bytesRead;
                try
                {
                    unsafe
                    {
                        fixed (byte* ptr = buffer)
                        {
                            bytesRead = Read(tunFd, ptr, 16384);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    if (!ct.IsCancellationRequested)
                    {
                        AppLogger.LogError($"[TUN READ ERROR] {ex.Message}");
                    }

                    break;
                }

                if (bytesRead < 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    var errno = Marshal.GetLastPInvokeError();
                    if (errno is 4 or 11)
                    {
                        continue;
                    }

                    break;
                }

                if (bytesRead == 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                var length = (int)bytesRead;
                _ = OctopusEngine.Current.SendPacketFromPoolAsync(buffer, length);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TUN PROCESS TRAFFIC ERROR] {ex.Message}");
        }
        finally
        {
            if (Volatile.Read(ref _tunGeneration) == gen)
            {
                _isTunRunning = false;
            }
        }
    }

    //полная остановка системного туннеля и освобождение ресурсов дескриптора
    public void StopNativeVpn()
    {
        CancellationTokenSource? cts;
        ParcelFileDescriptor? tunIf;
        Thread? txThread;
        Thread? rxThread;

        lock (_tunLock)
        {
            _ = Interlocked.Increment(ref _tunGeneration);
            IsActive = false;
            _currentTunIp = null;
            _isTunRunning = false;

            OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;

            cts = _vpnCts;
            _vpnCts = null;

            tunIf = _tunInterface;
            _tunInterface = null;

            txThread = _txThread;
            _txThread = null;

            rxThread = _rxThread;
            _rxThread = null;

            try
            {
                cts?.Cancel();
            }
            catch
            {
                // ignored
            }

            try
            {
                tunIf?.Close();
            }
            catch
            {
                // ignored
            }

            ReleaseWakeLock();
            UnregisterNetworkCallback();

            while (_downstreamChannel.Reader.TryRead(out var stale))
            {
                if (stale.buffer is { Length: > 0 })
                {
                    ArrayPool<byte>.Shared.Return(stale.buffer);
                }
            }
        }

        if (cts != null || tunIf != null)
        {
            //дожидаемся выхода рабочих потоков tun0 перед освобождением дескриптора без передачи отмененного токена
            _ = Task.Run(() =>
            {
                try
                {
                    if (txThread is { IsAlive: true } && Thread.CurrentThread != txThread)
                    {
                        _ = txThread.Join(500);
                    }

                    if (rxThread is { IsAlive: true } && Thread.CurrentThread != rxThread)
                    {
                        _ = rxThread.Join(500);
                    }

                    cts?.Dispose();
                    tunIf?.Dispose();
                }
                catch
                {
                    // ignored
                }
            });
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                StopForeground(StopForegroundFlags.Remove);
                StopSelf();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[VPN ERROR] Failed to stop foreground service: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopNativeVpn();
            _vpnCts?.Dispose();
            _tunInterface?.Dispose();
            _networkCallback?.Dispose();
        }

        base.Dispose(disposing);
    }

    //отслеживание смены физических сетей wi-fi и сотовой связи для делегирования ре коннекта ядру
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class VpnNetworkCallback : ConnectivityManager.NetworkCallback
    {
        private const long MinRoamingDebounceMs = 1000;
        public volatile bool IsActive = true;
        private long _lastActiveNetworkId = -1;
        private bool _hadLostNetwork;
        private long _lastReconnectTicks;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
        public VpnNetworkCallback() { }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
        public VpnNetworkCallback(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

        public override void OnAvailable(Network network)
        {
            if (!IsActive)
            {
                return;
            }

            base.OnAvailable(network);
            HandleNetworkEvent(network, "OnAvailable");
        }

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
        {
            if (!IsActive)
            {
                return;
            }

            base.OnCapabilitiesChanged(network, capabilities);
            HandleNetworkEvent(network, "OnCapabilitiesChanged", capabilities);
        }

        private void HandleNetworkEvent(Network network, string source, NetworkCapabilities? caps = null)
        {
            try
            {
                var cm = (ConnectivityManager?)Application.Context.GetSystemService(ConnectivityService);
                caps ??= cm?.GetNetworkCapabilities(network);
                if (caps is null || caps.HasTransport(TransportType.Vpn))
                {
                    return;
                }

                if (!caps.HasCapability(NetCapability.Internet))
                {
                    return;
                }

                var netId = network.NetworkHandle;
                AppLogger.Log($"[NETWORK ROAMING] {source}: Physical network available: {network} (Handle: {netId})");

                if (OperatingSystem.IsAndroidVersionAtLeast(22))
                {
                    _ = Instance?.SetUnderlyingNetworks([network]);
                }

                if (_lastActiveNetworkId != -1 && _lastActiveNetworkId != netId || _hadLostNetwork)
                {
                    var now = Environment.TickCount64;
                    var elapsed = now - Volatile.Read(ref _lastReconnectTicks);

                    _lastActiveNetworkId = netId;
                    _hadLostNetwork = false;
                    if (elapsed < MinRoamingDebounceMs)
                    {
                        return;
                    }

                    Volatile.Write(ref _lastReconnectTicks, now);
                    AppLogger.Log($"[NETWORK ROAMING] Physical network changed to {netId}. Delegating seamless reconnect to engine.");
                    AndroidVpnService.Instance.NotifyPlatformNetworkChanged();
                }
                else
                {
                    _lastActiveNetworkId = netId;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[NETWORK ROAMING ERROR] {ex.Message}");
            }
        }

        public override void OnLost(Network network)
        {
            if (!IsActive)
            {
                return;
            }

            try
            {
                base.OnLost(network);
                var cm = (ConnectivityManager?)Application.Context.GetSystemService(ConnectivityService);
                var caps = cm?.GetNetworkCapabilities(network);
                if (caps is not null && caps.HasTransport(TransportType.Vpn))
                {
                    return;
                }

                var netId = network.NetworkHandle;
                AppLogger.Log($"[NETWORK ROAMING] Physical network lost: {network} (Handle: {netId})");
                if (_lastActiveNetworkId == netId)
                {
                    _lastActiveNetworkId = -1;
                    _hadLostNetwork = true;
                    if (OperatingSystem.IsAndroidVersionAtLeast(22))
                    {
                        _ = Instance?.SetUnderlyingNetworks(null);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[NETWORK ROAMING ERROR] {ex.Message}");
            }
        }
    }

#pragma warning disable CA2213
    private ParcelFileDescriptor? _tunInterface;
    private CancellationTokenSource? _vpnCts;
    private Thread? _txThread;
    private Thread? _rxThread;
#pragma warning restore CA2213
}
