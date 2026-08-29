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

#pragma warning disable CA2213
    private ParcelFileDescriptor? _tunInterface;
    private FileInputStream? _tunInputStream;
    private FileOutputStream? _tunOutputStream;
    private CancellationTokenSource? _vpnCts;
#pragma warning restore CA2213
    private PowerManager.WakeLock? _wakeLock;

    private void AcquireWakeLock()
    {
        if (_wakeLock is null)
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "obxodka::VpnWakeLock");
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
            FechsueTransport.OnSocketCreated = sock =>
            {
                try
                {
                    _ = Protect((int)sock.Handle);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FECHSUE PROTECT ERROR] {ex.Message}");
                }
            };
            GrpcTransport.OnSocketCreated = sock =>
            {
                try
                {
                    _ = Protect((int)sock.Handle);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[GRPC PROTECT ERROR] {ex.Message}");
                }
            };

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

            if (!string.IsNullOrEmpty(OctopusEngine.Current.AssignedIp))
            {
                EstablishTun();
            }
        }

        return StartCommandResult.Sticky;
    }

    private ConnectivityManager.NetworkCallback? _networkCallback;

    private void RegisterNetworkCallback()
    {
        try
        {
            var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (cm is not null)
            {
                using var builder = new NetworkRequest.Builder();
                var request = builder
                    .AddCapability(NetCapability.Internet)?
                    .Build();

                if (request is not null)
                {
                    _networkCallback = new VpnNetworkCallback(Protect);
                    cm.RegisterNetworkCallback(request, _networkCallback);
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
                var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
                cm?.UnregisterNetworkCallback(_networkCallback);
                _networkCallback.Dispose();
                _networkCallback = null;
            }
        }
        catch { }
    }

    private sealed class VpnNetworkCallback(Func<int, bool> protectAction) : ConnectivityManager.NetworkCallback
    {
        private readonly Func<int, bool> _protectAction = protectAction;

        public override void OnAvailable(Network network)
        {
            base.OnAvailable(network);
            System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Network changed/available: {network}. Re-protecting sockets.");

            OctopusEngine.Current.ProtectTransportSockets(sock =>
            {
                try
                {
                    _ = _protectAction((int)sock.Handle);
                }
                catch { }
            });

            if (AndroidVpnService.Instance.CurrentState == AppVpnState.Reconnecting)
            {
                AndroidVpnService.Instance.TriggerImmediateReconnect();
            }
        }

        public override void OnLost(Network network)
        {
            base.OnLost(network);
            System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Network lost: {network}");
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
        _ = builder.SetSmallIcon(Resource.Drawable.notification_icon);
        _ = builder.SetOngoing(true);
        _ = builder.SetContentIntent(pendingIntent);

        return builder.Build()!;
    }

    private void InjectPacketToAndroid(byte[] packet, int length)
    {
        if (_tunOutputStream is null)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        try
        {
            _tunOutputStream.Write(packet, 0, length);
        }
        catch (ObjectDisposedException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN ERROR] TUN stream disposed: {ex.Message}");
            AndroidVpnService.Instance.SetError("VPN tunnel closed unexpectedly");
        }
        catch (System.IO.IOException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN ERROR] IO error during packet injection: {ex.Message}");
            AndroidVpnService.Instance.SetError("Network error in VPN tunnel");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN ERROR] Unexpected error: {ex.GetType().Name} - {ex.Message}");
            AndroidVpnService.Instance.SetError($"VPN error: {ex.GetType().Name}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public void EstablishTun()
    {
        try
        {
            var ip = OctopusEngine.Current.AssignedIp;
            if (string.IsNullOrEmpty(ip))
            {
                return;
            }

            _vpnCts = new CancellationTokenSource();
            using var builder = new Builder(this);
            _ = builder
                .SetSession("Obxodka")
                .AddAddress(ip, 10)
                .AddAddress("fd00::2", 128)
                .SetMtu(1420)
                .SetBlocking(true)
                .AddRoute("0.0.0.0", 0)
                .AddRoute("::", 0);

            var useAdBlock = Preferences.Default.Get("use_adblock_dns", true);
            if (useAdBlock)
            {
                _ = builder.AddDnsServer("94.140.14.14");
                _ = builder.AddDnsServer("94.140.15.15");
                _ = builder.AddDnsServer("2a10:50c0::ad1:ff");
                _ = builder.AddDnsServer("2a10:50c0::ad2:ff");
            }
            else
            {
                _ = builder.AddDnsServer("1.1.1.1");
                _ = builder.AddDnsServer("1.0.0.1");
                _ = builder.AddDnsServer("8.8.8.8");
                _ = builder.AddDnsServer("8.8.4.4");
                _ = builder.AddDnsServer("2001:4860:4860::8888");
                _ = builder.AddDnsServer("2001:4860:4860::8844");
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
                _tunOutputStream = new FileOutputStream(_tunInterface.FileDescriptor);
                AndroidVpnService.Instance.ChangeState(AppVpnState.Connected);

                var thread = new Thread(() => ProcessTraffic(_vpnCts.Token))
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.Highest,
                    Name = "AndroidTunReader"
                };
                thread.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN ESTABLISH ERROR] {ex.Message}");
            AndroidVpnService.Instance.SetError("Не удалось создать туннель");
            StopSelf();
        }
    }

    private void ProcessTraffic(CancellationToken ct)
    {
        if (_tunInterface?.FileDescriptor is null)
        {
            return;
        }

        FileInputStream? inputStream = null;
        try
        {
            inputStream = new FileInputStream(_tunInterface.FileDescriptor);
            _tunInputStream = inputStream;

            while (!ct.IsCancellationRequested)
            {
                var buffer = ArrayPool<byte>.Shared.Rent(16384);
                var length = inputStream.Read(buffer);

                if (length < 0)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    break;
                }

                if (length > 0)
                {
                    // L3 Zero-Latency DNS Sinkhole for telemetry, spyware & trackers
                    var sinkholeResp = DnsAdBlocker.ProcessPacket(buffer, length, useAdblock: true);
                    if (sinkholeResp is not null)
                    {
                        try
                        {
                            _tunOutputStream?.Write(sinkholeResp, 0, sinkholeResp.Length);
                        }
                        catch { }
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
        catch { }
        finally
        {
            try
            {
                inputStream?.Close();
                inputStream?.Dispose();
            }
            catch { }

            _tunInputStream = null;
        }
    }

    public void StopNativeVpn()
    {
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
