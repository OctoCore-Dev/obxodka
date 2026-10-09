using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using Java.IO;

namespace obxodka.Platforms.Android;

[Service(
    Name = "obxodka.OctopusVpnService",
    Permission = "android.permission.BIND_VPN_SERVICE",
    Exported = false,
    ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeSpecialUse)]
[IntentFilter(["android.net.VpnService"])]
[SupportedOSPlatform("android29.0")]
public sealed partial class OctopusVpnService : VpnService, IDisposable
{
    private const int NotificationId = 2026;
    private const string ChannelId = "obxodka_vpn_channel";

    public static OctopusVpnService? Instance { get; private set; }
    public bool IsActive { get; private set; }
    public static TaskCompletionSource<OctopusVpnService>? ServiceReadyTcs { get; set; }

    static OctopusVpnService() => HookProtection();

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
    private FileInputStream? _tunInputStream;
    private FileOutputStream? _tunOutputStream;
    private CancellationTokenSource? _vpnCts;
    private Thread? _txThread;
    private Thread? _rxThread;
#pragma warning restore CA2213
    private PowerManager.WakeLock? _wakeLock;

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateUnbounded<(byte[] buffer, int length)>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });

    private void AcquireWakeLock()
    {
        if (_wakeLock is null)
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "obxodka::VpnWakeLock");
            _wakeLock?.SetReferenceCounted(false);
        }

        if (_wakeLock is { IsHeld: false })
        {
            _wakeLock.Acquire();
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
        _ = (ServiceReadyTcs?.TrySetResult(this));
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

    public static bool ProtectSocket(Socket? sock)
    {
        if (sock == null)
        {
            return false;
        }

        var service = Instance;
        if (service == null)
        {
            System.Diagnostics.Debug.WriteLine("[VPN PROTECT WARNING] Instance is null, socket cannot be protected yet!");
            return false;
        }

        try
        {
            var fd = (int)sock.Handle;
            var ok = service.Protect(fd);
            if (!ok)
            {
                System.Diagnostics.Debug.WriteLine($"[VPN PROTECT WARNING] service.Protect(fd={fd}) returned false!");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[VPN PROTECT] Socket fd={fd} successfully protected from VPN routing loop.");
            }
            return ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN PROTECT ERROR] {ex.Message}");
            return false;
        }
    }

    public static void HookProtection()
    {
        ObxodkaStreamTransport.OnSocketCreated = sock => ProtectSocket(sock);
        InSituDiagnosticsEngine.OnSocketCreated = sock => ProtectSocket(sock);
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            StopNativeVpn();
            return StartCommandResult.NotSticky;
        }

        if (intent?.Action == "START")
        {
            Instance = this;
            IsActive = true;
            HookProtection();

            RegisterNetworkCallback();
            CreateNotificationChannel();

            if (OperatingSystem.IsAndroidVersionAtLeast(34))
            {
                StartForeground(NotificationId, CreateNotification(), global::Android.Content.PM.ForegroundService.TypeSpecialUse);
            }
            else
            {
                StartForeground(NotificationId, CreateNotification());
            }

            OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
            OctopusEngine.Current.OnPacketReceived += InjectPacketToAndroid;
            OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
            OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;

            _ = (ServiceReadyTcs?.TrySetResult(this));
        }

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
            System.Diagnostics.Debug.WriteLine($"[NETWORK CALLBACK ERROR] {ex.Message}");
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

    [System.Diagnostics.CodeAnalysis.DynamicallyAccessedMembers(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All)]
    private sealed class VpnNetworkCallback : ConnectivityManager.NetworkCallback
    {
        public volatile bool IsActive = true;
        private long _lastActiveNetworkId = -1;
        private long _lastReconnectTicks;
        private int _consecutiveReconnects;

        private const long ReconnectCooldownMs = 3000;
        private const int MaxConsecutiveReconnects = 3;
        private const long ConsecutiveResetMs = 30000;

        [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
        public VpnNetworkCallback() { }

        [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(VpnNetworkCallback))]
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
                System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] {source}: Physical network available: {network} (Handle: {netId})");

                if (OperatingSystem.IsAndroidVersionAtLeast(22))
                {
                    _ = Instance?.SetUnderlyingNetworks([network]);
                }

                if (_lastActiveNetworkId != -1 && _lastActiveNetworkId != netId)
                {
                    var now = System.Environment.TickCount64;
                    var elapsed = now - Volatile.Read(ref _lastReconnectTicks);

                    if (elapsed > ConsecutiveResetMs)
                    {
                        Volatile.Write(ref _consecutiveReconnects, 0);
                    }

                    if (elapsed < ReconnectCooldownMs)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Skipping reconnect — cooldown active ({elapsed}ms < {ReconnectCooldownMs}ms)");
                        _lastActiveNetworkId = netId;
                        return;
                    }

                    var consecutive = Interlocked.Increment(ref _consecutiveReconnects);
                    if (consecutive > MaxConsecutiveReconnects)
                    {
                        System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Suppressing reconnect — {consecutive} consecutive reconnects, flapping avoided");
                        _lastActiveNetworkId = netId;
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Active network changed from {_lastActiveNetworkId} to {netId}. Roaming reconnect #{consecutive}");
                    _lastActiveNetworkId = netId;
                    Volatile.Write(ref _lastReconnectTicks, now);
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
                System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING ERROR] {ex.Message}");
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
                System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Physical network lost: {network} (Handle: {netId})");
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
                System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING ERROR] {ex.Message}");
            }
        }
    }

    private void HandleEngineDrop() =>
        AndroidVpnService.Instance.HandleEngineDrop();

    private void CreateNotificationChannel()
    {
        var channel = new NotificationChannel(ChannelId, "Obxodka VPN Status", NotificationImportance.Low)
        {
            Description = "Показывает статус подключения"
        };
        var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
        notificationManager?.CreateNotificationChannel(channel);
    }

    private Notification CreateNotification()
    {
        var pendingIntent = PendingIntent.GetActivity(
            this,
            0,
            new Intent(this, typeof(MainActivity)),
            PendingIntentFlags.Immutable);

        using var builder = new NotificationCompat.Builder(this, ChannelId);
        _ = builder.SetContentTitle("Obxodka");
        _ = builder.SetContentText("Трафик защищен (Stealth Mode)");
        _ = builder.SetSmallIcon(Maui.Resource.Drawable.notification_icon);
        _ = builder.SetOngoing(true);
        _ = builder.SetContentIntent(pendingIntent);

        return builder.Build()!;
    }

    private void InjectPacketToAndroid(byte[] packet, int length)
    {
        if (_tunOutputStream is null || _vpnCts is null || _vpnCts.IsCancellationRequested)
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
                    System.Diagnostics.Debug.WriteLine($"[VPN ESTABLISH] TUN already active for {ip}/32 with healthy worker threads. Preserving tunnel interface.");
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
                    _tunInputStream?.Close();
                }
                catch { }
                try
                {
                    _tunOutputStream?.Close();
                }
                catch { }
                try
                {
                    _tunInterface?.Close();
                }
                catch { }

                _tunInputStream = null;
                _tunOutputStream = null;
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
                    .SetMtu(NetworkDefaults.CurrentMtu)
                    .SetBlocking(true)
                    .AddRoute("0.0.0.0", 0);

                var ip6 = OctopusEngine.Current.AssignedIpV6;
                if (!string.IsNullOrEmpty(ip6) && IPAddress.TryParse(ip6, out var parsedV6) && parsedV6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    try
                    {
                        _ = builder.AddAddress(ip6, 128);
                        _ = builder.AddRoute("::", 0);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VPN V6 ROUTE ERROR] {ex.Message}");
                    }
                }

                _ = builder.AddDnsServer(NetworkDefaults.PrimaryDns);
                _ = builder.AddDnsServer("8.8.8.8");
                _ = builder.AddDnsServer(NetworkDefaults.SecondaryDns);
                try
                {
                    _ = builder.AddDnsServer("2606:4700:4700::1111");
                    _ = builder.AddDnsServer("2001:4860:4860::8888");
                }
                catch { }

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

                    var outStream = new FileOutputStream(_tunInterface.FileDescriptor);
                    var inStream = new FileInputStream(_tunInterface.FileDescriptor);
                    _tunOutputStream = outStream;
                    _tunInputStream = inStream;

                    _txThread = new Thread(() => ProcessTraffic(inStream, ct))
                    {
                        IsBackground = true,
                        Priority = System.Threading.ThreadPriority.Highest,
                        Name = "AndroidTunReader"
                    };
                    _txThread.Start();

                    _rxThread = new Thread(() => ProcessDownstreamTraffic(outStream, ct))
                    {
                        IsBackground = true,
                        Priority = System.Threading.ThreadPriority.Highest,
                        Name = "AndroidTunWriter"
                    };
                    _rxThread.Start();

                    System.Diagnostics.Debug.WriteLine($"[VPN ESTABLISH] TUN successfully established for {ip}/32 (MTU {NetworkDefaults.CurrentMtu})");
                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VPN ESTABLISH ERROR] {ex.Message}");
                AndroidVpnService.Instance.SetError("Не удалось создать туннель");
                StopSelf();
                return false;
            }
        }
    }

    private void ProcessDownstreamTraffic(FileOutputStream outputStream, CancellationToken ct)
    {
        var reader = _downstreamChannel.Reader;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                while (reader.TryRead(out var item))
                {
                    try
                    {
                        outputStream.Write(item.buffer, 0, item.length);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[TUN WRITE ERROR] {ex.Message}");
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
            System.Diagnostics.Debug.WriteLine($"[TUN DOWNSTREAM ERROR] {ex.Message}");
        }
        finally
        {
            _isTunRunning = false;
            try
            {
                outputStream.Close();
                outputStream.Dispose();
            }
            catch { }
        }
    }

    private void ProcessTraffic(FileInputStream inputStream, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(16384);
                int length;
                try
                {
                    length = inputStream.Read(buffer);
                }
                catch
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                if (length < 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                if (length > 0)
                {
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
                        _ = _downstreamChannel.Writer.TryWrite((copy, sinkholeResp.Length));
                        ArrayPool<byte>.Shared.Return(buffer);
                        continue;
                    }

                    _ = OctopusEngine.Current.SendPacketFromPoolAsync(buffer, length);
                }
                else
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[TUN PROCESS TRAFFIC ERROR] {ex.Message}");
        }
        finally
        {
            _isTunRunning = false;
            try
            {
                inputStream.Close();
                inputStream.Dispose();
            }
            catch { }
        }
    }

    public void StopNativeVpn()
    {
        IsActive = false;
        if (Instance == this)
        {
            Instance = null;
        }
        _currentTunIp = null;
        _isTunRunning = false;

        OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;

        var cts = _vpnCts;
        _vpnCts = null;

        var tunIn = _tunInputStream;
        _tunInputStream = null;

        var tunOut = _tunOutputStream;
        _tunOutputStream = null;

        var tunIf = _tunInterface;
        _tunInterface = null;

        try
        {
            cts?.Cancel();
        }
        catch { }

        try
        {
            tunIn?.Close();
        }
        catch { }

        try
        {
            tunOut?.Close();
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
                tunIn?.Dispose();
                tunOut?.Dispose();
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
                System.Diagnostics.Debug.WriteLine($"[VPN ERROR] Failed to stop foreground service: {ex.Message}");
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopNativeVpn();
            _vpnCts?.Dispose();
            _tunInputStream?.Dispose();
            _tunOutputStream?.Dispose();
            _tunInterface?.Dispose();
            _networkCallback?.Dispose();
        }

        base.Dispose(disposing);
    }
}
