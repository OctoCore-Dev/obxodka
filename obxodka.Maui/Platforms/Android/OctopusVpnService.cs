using System.Buffers;
using System.Threading.Channels;
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

    private readonly Channel<(byte[] buffer, int length)> _downstreamChannel =
        Channel.CreateUnbounded<(byte[] buffer, int length)>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

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
                var cm = (ConnectivityManager?)GetSystemService(ConnectivityService);
                cm?.UnregisterNetworkCallback(_networkCallback);
                _networkCallback.Dispose();
                _networkCallback = null;
            }
        }
        catch { }
    }

    private sealed class VpnNetworkCallback : ConnectivityManager.NetworkCallback
    {
        private long _lastActiveNetworkId = -1;

        public override void OnAvailable(Network network)
        {
            base.OnAvailable(network);
            var cm = (ConnectivityManager?)global::Android.App.Application.Context.GetSystemService(ConnectivityService);
            var caps = cm?.GetNetworkCapabilities(network);
            if (caps is null || caps.HasTransport(TransportType.Vpn))
            {
                return;
            }

            var netId = network.NetworkHandle;
            System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Physical network available: {network} (Handle: {netId})");

            if (_lastActiveNetworkId != -1 && _lastActiveNetworkId != netId)
            {
                System.Diagnostics.Debug.WriteLine($"[NETWORK ROAMING] Active network changed from {_lastActiveNetworkId} to {netId}. Instant roaming reconnect!");
                if (AndroidVpnService.Instance.CurrentState is AppVpnState.Connected or AppVpnState.Reconnecting)
                {
                    AndroidVpnService.Instance.TriggerImmediateReconnect();
                }
            }
            _lastActiveNetworkId = netId;
        }

        public override void OnCapabilitiesChanged(Network network, NetworkCapabilities capabilities)
        {
            base.OnCapabilitiesChanged(network, capabilities);
            if (capabilities.HasTransport(TransportType.Vpn))
            {
                return;
            }

            if (capabilities.HasCapability(NetCapability.Internet))
            {
                _lastActiveNetworkId = network.NetworkHandle;
            }
        }

        public override void OnLost(Network network)
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
                if (AndroidVpnService.Instance.CurrentState == AppVpnState.Connected)
                {
                    AndroidVpnService.Instance.TriggerImmediateReconnect();
                }
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
                .SetMtu(1360)
                .SetBlocking(true)
                .AddRoute("0.0.0.0", 0);

            _ = builder.AddDnsServer("1.1.1.1");
            _ = builder.AddDnsServer("1.0.0.1");
            _ = builder.AddDnsServer("8.8.8.8");
            _ = builder.AddDnsServer("8.8.4.4");

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

                var txThread = new Thread(() => ProcessTraffic(_vpnCts.Token))
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.Highest,
                    Name = "AndroidTunReader"
                };
                txThread.Start();

                var rxThread = new Thread(() => ProcessDownstreamTraffic(_vpnCts.Token))
                {
                    IsBackground = true,
                    Priority = System.Threading.ThreadPriority.Highest,
                    Name = "AndroidTunWriter"
                };
                rxThread.Start();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VPN ESTABLISH ERROR] {ex.Message}");
            AndroidVpnService.Instance.SetError("Не удалось создать туннель");
            StopSelf();
        }
    }

    private void ProcessDownstreamTraffic(CancellationToken ct)
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
                        _tunOutputStream?.Write(item.buffer, 0, item.length);
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

                if (reader.WaitToReadAsync(ct).AsTask().Result)
                {
                    continue;
                }
            }
        }
        catch { }
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
                    var sinkholeResp = DnsAdBlocker.ProcessPacket(buffer, length, useAdblock: true);
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
