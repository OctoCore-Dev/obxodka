using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;

namespace obxodka.Platforms.Android;

//системная служба android vpn service для управления туннелем tun0 и защиты сокетов
[Service(
    Name = "obxodka.OctopusVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
[IntentFilter(["android.net.VpnService"])]
[SupportedOSPlatform("android29.0")]
public sealed partial class OctopusVpnService : VpnService
{
    private static readonly int s_notificationId = DateTime.UtcNow.Year;
    private const string ChannelId = "obxodka_vpn_channel";

    public static OctopusVpnService? Instance { get; private set; }
    public bool IsActive { get; private set; }
    public static TaskCompletionSource<OctopusVpnService>? ServiceReadyTcs { get; set; }
    public static string? LastEstablishError { get; private set; }

    static OctopusVpnService() => HookProtection();

    //ожидание готовности службы vpn для избежания гонок при старте
    public static async Task<OctopusVpnService?> WaitForServiceReadyAsync(int timeoutMs = 4000)
    {
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

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return completed == tcs.Task ? await tcs.Task.ConfigureAwait(false) : Instance;
    }

#pragma warning disable CA2213
    private ParcelFileDescriptor? _tunInterface;
    private CancellationTokenSource? _vpnCts;
    private Thread? _txThread;
    private Thread? _rxThread;
#pragma warning restore CA2213
    private PowerManager.WakeLock? _wakeLock;
    private long _wakeLockRenewTicks;

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateBounded<(byte[] buffer, int length)>(
            new BoundedChannelOptions(2048) { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });

    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint read(int fd, byte* buf, nuint count);

    [LibraryImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint write(int fd, byte* buf, nuint count);

    //удержание блокировки процессора для предотвращения засыпания сокетов в фоне
    private void AcquireWakeLock()
    {
        if (_wakeLock is null)
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "obxodka::VpnWakeLock");
            _wakeLock?.SetReferenceCounted(false);
        }

        if (_wakeLock is not null)
        {
            _wakeLock.Acquire(3_600_000L);
            Volatile.Write(ref _wakeLockRenewTicks, System.Environment.TickCount64);
        }
    }

    private void EnsureWakeLockHeld()
    {
        var now = System.Environment.TickCount64;
        if (now - Volatile.Read(ref _wakeLockRenewTicks) >= 300_000L || _wakeLock is { IsHeld: false })
        {
            try
            {
                AcquireWakeLock();
            }
            catch { }
        }
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock is { IsHeld: true })
        {
            _wakeLock.Release();
        }
    }

    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        IsActive = true;
        HookProtection();
        _ = ServiceReadyTcs?.TrySetResult(this);
    }

    public override void OnDestroy()
    {
        IsActive = false;
        if (Instance == this)
        {
            Instance = null;
        }
        StopNativeVpn();
        base.OnDestroy();
    }

    public override void OnRevoke()
    {
        StopNativeVpn();
        AndroidVpnService.Instance.ChangeState(AppVpnState.Disconnected);
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

    //привязка платформенной защиты сокетов к транспортным фабрикам ядра
    public static void HookProtection()
    {
        ObxodkaStreamTransport.OnSocketCreated = sock => ProtectSocket(sock);
        ObxodkaWebSocketTransport.OnSocketCreated = sock => ProtectSocket(sock);
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
            StartForeground(s_notificationId, CreateNotification(), global::Android.Content.PM.ForegroundService.TypeSpecialUse);
        }
        else
        {
            StartForeground(s_notificationId, CreateNotification());
        }

        OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
        OctopusEngine.Current.OnPacketReceived += InjectPacketToAndroid;
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;

        _ = ServiceReadyTcs?.TrySetResult(this);

        return StartCommandResult.Sticky;
    }

    private VpnNetworkCallback? _networkCallback;

    private void RegisterNetworkCallback()
    {
        try
        {
            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (cm is not null)
            {
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
            if (_networkCallback is not null)
            {
                _networkCallback.IsActive = false;
                var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
                cm?.UnregisterNetworkCallback(_networkCallback);
                _networkCallback = null;
            }
        }
        catch { }
    }

    //отслеживание смены физических сетей wi-fi и сотовой связи для делегирования реконнекта ядру
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)]
    private sealed class VpnNetworkCallback : ConnectivityManager.NetworkCallback
    {
        public volatile bool IsActive = true;
        private long _lastActiveNetworkId = -1;
        private long _lastReconnectTicks;
        private const long MinRoamingDebounceMs = 1000;

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
        public VpnNetworkCallback() { }

        [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
        public VpnNetworkCallback(IntPtr handle, global::Android.Runtime.JniHandleOwnership transfer) : base(handle, transfer) { }

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
                var cm = (ConnectivityManager?)global::Android.App.Application.Context.GetSystemService(ConnectivityService);
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

                if (_lastActiveNetworkId != -1 && _lastActiveNetworkId != netId)
                {
                    var now = System.Environment.TickCount64;
                    var elapsed = now - Volatile.Read(ref _lastReconnectTicks);

                    if (elapsed < MinRoamingDebounceMs)
                    {
                        _lastActiveNetworkId = netId;
                        return;
                    }

                    _lastActiveNetworkId = netId;
                    Volatile.Write(ref _lastReconnectTicks, now);
                    AppLogger.Log($"[NETWORK ROAMING] Physical network changed to {netId}. Delegating seamless reconnect to engine.");
                    if (AndroidVpnService.Instance?.CurrentState is AppVpnState.Connected or AppVpnState.Reconnecting)
                    {
                        AndroidVpnService.Instance.TriggerImmediateReconnect();
                    }
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
                var cm = (ConnectivityManager?)global::Android.App.Application.Context.GetSystemService(ConnectivityService);
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

    private void HandleEngineDrop() =>
        AndroidVpnService.Instance.HandleEngineDrop();

    private void CreateNotificationChannel()
    {
        using var channel = new NotificationChannel(ChannelId, "Obxodka VPN Status", NotificationImportance.Low)
        {
            Description = "Показывает статус подключения"
        };
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
        _ = builder.SetSmallIcon(Maui.Resource.Drawable.notification_icon);
        _ = builder.SetOngoing(true);
        _ = builder.SetContentIntent(pendingIntent);

        return builder.Build()!;
    }

    //инжекция входящего пакета от сервера в сетевой стек android
    private void InjectPacketToAndroid(byte[] packet, int length)
    {
        if (!_isTunRunning || _vpnCts is null || _vpnCts.IsCancellationRequested)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_downstreamChannel.Writer.TryWrite((packet, length)))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    private readonly Lock _tunLock = new();
    private string? _currentTunIp;
    private volatile bool _isTunRunning;

    //инициализация и подъем виртуального интерфейса tun0 с настройками dns из ядра
    public bool EstablishTun()
    {
        lock (_tunLock)
        {
            try
            {
                var ip = OctopusEngine.Current.AssignedIp;
                if (string.IsNullOrEmpty(ip))
                {
                    return false;
                }

                if (_tunInterface != null && _currentTunIp == ip && _isTunRunning && _txThread is { IsAlive: true } && _rxThread is { IsAlive: true })
                {
                    AppLogger.Log($"[VPN ESTABLISH] TUN already active for {ip}/32 with healthy worker threads. Preserving tunnel interface.");
                    return true;
                }

                _isTunRunning = false;
                _currentTunIp = null;

                try
                {
                    _vpnCts?.Cancel();
                }
                catch { }

                try
                {
                    _tunInterface?.Close();
                }
                catch { }

                _tunInterface = null;

                while (_downstreamChannel.Reader.TryRead(out var stale))
                {
                    ArrayPool<byte>.Shared.Return(stale.buffer);
                }

                try
                {
                    _vpnCts?.Dispose();
                }
                catch { }

                _vpnCts = new CancellationTokenSource();
                var ct = _vpnCts.Token;

                using var builder = new Builder(this);
                _ = builder
                    .SetSession("Obxodka")
                    .AddAddress(ip, 32)
                    .SetMtu(Math.Min(SmartMtuEngine.CurrentMtu, 1400))
                    .SetBlocking(true)
                    .AddRoute("0.0.0.0", 0);

                var ip6 = OctopusEngine.Current.AssignedIpV6;
                var effectiveV6 = (!string.IsNullOrEmpty(ip6) && IPAddress.TryParse(ip6, out var parsedV6) && parsedV6.AddressFamily == AddressFamily.InterNetworkV6)
                    ? ip6
                    : "fd00::2";

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

                foreach (var dns6 in DnsDefaults.TunnelDnsServersIPv6)
                {
                    try
                    {
                        _ = builder.AddDnsServer(dns6);
                    }
                    catch { }
                }

                var bypassed = new AppManager().GetBypassedPackages();
                foreach (var pkg in bypassed)
                {
                    try
                    {
                        _ = builder.AddDisallowedApplication(pkg);
                    }
                    catch { }
                }

                try
                {
                    _ = builder.AddDisallowedApplication(PackageName ?? "com.octocore.obxodka");
                }
                catch { }

                _tunInterface = builder.Establish();
                if (_tunInterface is { FileDescriptor: not null })
                {
                    AcquireWakeLock();
                    _currentTunIp = ip;
                    _isTunRunning = true;

                    var tunFd = _tunInterface.Fd;

                    _txThread = new Thread(() => ProcessTraffic(tunFd, ct))
                    {
                        IsBackground = true,
                        Priority = System.Threading.ThreadPriority.Highest,
                        Name = "AndroidTunReader"
                    };
                    _txThread.Start();

                    _rxThread = new Thread(() => ProcessDownstreamTraffic(tunFd, ct))
                    {
                        IsBackground = true,
                        Priority = System.Threading.ThreadPriority.Highest,
                        Name = "AndroidTunWriter"
                    };
                    _rxThread.Start();

                    AppLogger.Log($"[VPN ESTABLISH] TUN successfully established for {ip}/32 (MTU {Math.Min(SmartMtuEngine.CurrentMtu, 1400)})");
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
    private void ProcessDownstreamTraffic(int tunFd, CancellationToken ct)
    {
        var reader = _downstreamChannel.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                EnsureWakeLockHeld();
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        unsafe
                        {
                            fixed (byte* ptr = item.buffer)
                            {
                                var written = write(tunFd, ptr, (nuint)item.length);
                                if (written < 0)
                                {
                                    var errno = Marshal.GetLastPInvokeError();
                                    if (errno == 4)
                                    {
                                        _ = write(tunFd, ptr, (nuint)item.length);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogError($"[TUN WRITE ERROR] {ex.Message}");
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(item.buffer);
                    }
                }

                if (ct.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    if (!reader.WaitToReadAsync(ct).AsTask().GetAwaiter().GetResult())
                    {
                        break;
                    }
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
                catch (AggregateException ae) when (ae.InnerException is System.OperationCanceledException)
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
            _isTunRunning = false;
        }
    }

    //поток чтения исходящих пакетов устройств из tun0 и отправка в очередь octopus
    private void ProcessTraffic(int tunFd, CancellationToken ct)
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
                            bytesRead = read(tunFd, ptr, 16384);
                        }
                    }
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
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
                if (OctopusEngine.Current.IsServerDestination(buffer.AsSpan(0, length), length))
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    continue;
                }

                var sinkholeResp = DnsAdBlocker.ProcessPacket(buffer, length);
                if (sinkholeResp is not null)
                {
                    var copy = ArrayPool<byte>.Shared.Rent(sinkholeResp.Length);
                    Buffer.BlockCopy(sinkholeResp, 0, copy, 0, sinkholeResp.Length);
                    if (!_downstreamChannel.Writer.TryWrite((copy, sinkholeResp.Length)))
                    {
                        ArrayPool<byte>.Shared.Return(copy);
                    }
                    ArrayPool<byte>.Shared.Return(buffer);
                    continue;
                }

                _ = OctopusEngine.Current.SendPacketFromPoolAsync(buffer, length);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[TUN PROCESS TRAFFIC ERROR] {ex.Message}");
        }
        finally
        {
            _isTunRunning = false;
        }
    }

    //полная остановка системного туннеля и освобождение ресурсов дескриптора
    public void StopNativeVpn()
    {
        IsActive = false;
        _currentTunIp = null;
        _isTunRunning = false;

        OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;

        var cts = _vpnCts;
        _vpnCts = null;

        var tunIf = _tunInterface;
        _tunInterface = null;

        try
        {
            cts?.Cancel();
        }
        catch { }

        try
        {
            tunIf?.Close();
        }
        catch { }

        ReleaseWakeLock();
        UnregisterNetworkCallback();

        _ = Task.Run(() =>
        {
            try
            {
                cts?.Dispose();
                tunIf?.Dispose();
            }
            catch { }
        });

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
}
