using Android.App;
using Android.Content;
using Android.Net;
using Android.OS;
using AndroidX.Core.App;
using Java.IO;
namespace obxodka.Platforms.Android;

[Service(Name = "obxodka.OctopusVpnService", Permission = "android.permission.BIND_VPN_SERVICE", Exported = false)]
[SupportedOSPlatform("android30.0")]
public class OctopusVpnService : VpnService, IDisposable
{
    private ParcelFileDescriptor? _tunInterface;
    private FileOutputStream? _tunOutputStream;
    private CancellationTokenSource? _vpnCts;
    private const int NotificationId = 2026;
    private const string ChannelId = "obxodka_vpn_channel";
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            StopNativeVpn();
            return StartCommandResult.NotSticky;
        }
        if (intent?.Action == "START")
        {
            CreateNotificationChannel();
#pragma warning disable CA1422, CS0618
            StartForeground(NotificationId, CreateNotification());
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
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(ChannelId, "Obxodka VPN Status", NotificationImportance.Low)
            {
                Description = "Показывает статус подключения"
            };
            var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
            notificationManager?.CreateNotificationChannel(channel);
        }
    }
    private Notification CreateNotification()
    {
        var pendingIntent = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)), PendingIntentFlags.Immutable);
#pragma warning disable CS8602
        return new NotificationCompat.Builder(this, ChannelId)
            .SetContentTitle("Obxodka VPN")
            .SetContentText("Трафик защищен (Stealth Mode)")
            .SetSmallIcon(Resource.Mipmap.appicon)
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
                .AddRoute("::", 0)
                .AddDnsServer("8.8.8.8")
                .AddDnsServer("2001:4860:4860::8888");
            _ = builder.AddDisallowedApplication(ApplicationContext?.PackageName ?? string.Empty);
            _tunInterface = builder.Establish();
            if (_tunInterface != null)
            {
                _tunOutputStream = new FileOutputStream(_tunInterface.FileDescriptor);
                AndroidVpnService.Instance.ChangeState(AppVpnState.Connected);
                _ = Task.Run(() => ProcessTrafficAsync(_vpnCts.Token));
            }
        }
        catch
        {
            AndroidVpnService.Instance.SetError("Не удалось создать туннель");
            StopSelf();
        }
    }
    private async Task ProcessTrafficAsync(CancellationToken ct)
    {
        if (_tunInterface?.FileDescriptor == null)
        {
            return;
        }
#pragma warning disable CS0618, CA1422
        using var safeHandle = new Microsoft.Win32.SafeHandles.SafeFileHandle(_tunInterface.FileDescriptor.Handle, ownsHandle: false);
#pragma warning restore CS0618, CA1422
        using var stream = new FileStream(safeHandle, FileAccess.ReadWrite);
        var buffer = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var length = await stream.ReadAsync(buffer.AsMemory(), ct);
                if (length > 0)
                {
                    var packet = new byte[length];
                    Array.Copy(buffer, 0, packet, 0, length);
                    await OctopusEngine.Current.SendPacketAsync(packet);
                }
            }
            catch { break; }
        }
    }
    private void StopNativeVpn()
    {
        OctopusEngine.Current.OnPacketReceived -= InjectPacketToAndroid;
        OctopusEngine.Current.OnConnectionDropped -= HandleEngineDrop;
        _vpnCts?.Cancel();
        _vpnCts?.Dispose();
        _vpnCts = null;
        _tunOutputStream?.Close();
        _tunOutputStream?.Dispose();
        _tunOutputStream = null;
        _tunInterface?.Close();
        _tunInterface?.Dispose();
        _tunInterface = null;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
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
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            StopNativeVpn();
        }
        base.Dispose(disposing);
    }
}
