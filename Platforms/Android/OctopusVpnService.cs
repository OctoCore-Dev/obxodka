using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using Java.IO;
namespace obxodka.Platforms.Android;

[Service(Name = "obxodka.OctopusVpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync)]
[IntentFilter(["android.net.VpnService"])]
[SupportedOSPlatform("android29.0")]
public class OctopusVpnService : VpnService, IDisposable
{
#pragma warning disable CA2213
    private ParcelFileDescriptor? _tunInterface;
    private FileInputStream? _tunInputStream;
    private FileOutputStream? _tunOutputStream;
    private CancellationTokenSource? _vpnCts;
#pragma warning restore CA2213
    private const int NotificationId = 2026;
    private const string ChannelId = "obxodka_vpn_channel";
    public static OctopusVpnService? Instance { get; private set; }

    private PowerManager.WakeLock? _wakeLock;

    private void AcquireWakeLock()
    {
        if (_wakeLock == null)
        {
            var powerManager = (PowerManager?)GetSystemService(PowerService);
            _wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "obxodka::VpnWakeLock");
        }
        if (_wakeLock != null && !_wakeLock.IsHeld)
        {
            _wakeLock.Acquire();
        }
    }

    private void ReleaseWakeLock()
    {
        if (_wakeLock != null && _wakeLock.IsHeld)
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
            CreateNotificationChannel();
#pragma warning disable CA1422, CS0618
            StartForeground(NotificationId, CreateNotification(), global::Android.Content.PM.ForegroundService.TypeDataSync);
#pragma warning restore CA1422, CS0618
            StartNativeVpn();
            OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
            OctopusEngine.Current.OnPacketReceived += InjectPacketToAndroid;
            OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
            OctopusEngine.Current.OnConnectionDropped += HandleEngineDrop;
        }
        return StartCommandResult.Sticky;
    }
    private void HandleEngineDrop() => AndroidVpnService.Instance.HandleEngineDrop();
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
        var pendingIntent = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
#pragma warning disable CS8602
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Obxodka")
            .SetContentText("Трафик защищен (Stealth Mode)")
            .SetSmallIcon(Resource.Drawable.notification_icon)
            .SetOngoing(true)
            .SetContentIntent(pendingIntent)
            .Build()!;
#pragma warning restore CS8602
    }
    private void InjectPacketToAndroid(byte[] packet)
    {
        if (_tunOutputStream == null)
        {
            return;
        }
        try
        {
            _tunOutputStream.Write(packet);
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
    }
    private void StartNativeVpn()
    {
        try
        {
            _vpnCts = new CancellationTokenSource();
            var ip = OctopusEngine.Current.AssignedIp;
            var builder = new Builder(this)
                .SetSession("Obxodka")
                .AddAddress(ip, 10)
                .AddAddress("fd00::2", 128)
                .SetMtu(1350)
                .AddRoute("0.0.0.0", 0)
                .AddRoute("::", 0);

            var useAdBlock = Preferences.Default.Get("use_adblock_dns", false);
            _ = useAdBlock
                ? builder.AddDnsServer("94.140.14.14")
                       .AddDnsServer("94.140.15.15")
                       .AddDnsServer("2a10:50c0::ad1:ff")
                       .AddDnsServer("2a10:50c0::ad2:ff")
                : builder.AddDnsServer("8.8.8.8")
                       .AddDnsServer("8.8.4.4")
                       .AddDnsServer("2001:4860:4860::8888")
                       .AddDnsServer("2001:4860:4860::8844");

            var bypassed = new AppManager().GetBypassedPackages();
            foreach (var pkg in bypassed)
            {
                try
                { _ = builder.AddDisallowedApplication(pkg); }
                catch { }
            }

            try
            { _ = builder.AddDisallowedApplication(PackageName ?? "com.octocore.obxodka"); }
            catch { }

            _tunInterface = builder.Establish();
            if (_tunInterface != null)
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
        catch
        {
            AndroidVpnService.Instance.SetError("Не удалось создать туннель");
            StopSelf();
        }
    }
    private void ProcessTraffic(CancellationToken ct)
    {
        if (_tunInterface?.FileDescriptor == null)
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
        { cts?.Cancel(); }
        catch { }
        try
        { tunIn?.Close(); }
        catch { }
        try
        { tunOut?.Close(); }
        catch { }
        try
        { tunIf?.Close(); }
        catch { }

        ReleaseWakeLock();

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
                if (OperatingSystem.IsAndroidVersionAtLeast(33))
                {
#pragma warning disable CA1416
                    StopForeground(StopForegroundFlags.Remove);
#pragma warning restore CA1416
                }
                else
                {
#pragma warning disable CS0618, CA1422
                    StopForeground(true);
#pragma warning restore CS0618, CA1422
                }
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
        }
        base.Dispose(disposing);
    }
}
