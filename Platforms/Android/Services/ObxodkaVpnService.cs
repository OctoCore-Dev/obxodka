namespace obxodka.Platforms.Android.Services;
[Service(Permission = "android.permission.BIND_VPN_SERVICE", Exported = true, ForegroundServiceType = ForegroundService.TypeDataSync)]
[IntentFilter(new[] { "android.net.VpnService" })]
public sealed class ObxodkaVpnService : VpnService
{
    private ParcelFileDescriptor? _vpnInterface;
    private Java.Lang.Object? _commandServer;
    public static ObxodkaVpnService? Instance { get; private set; }
    public static bool IsVpnRunning { get; private set; } = false;
    public static event Action<bool>? NativeStateChanged;
    private ConnectivityManager.NetworkCallback? _networkCallback;
    public override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        RegisterNetworkCallback();
    }
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (IsVpnRunning) return StartCommandResult.Sticky;
        try
        {
            StartForegroundNotification();
            string linkPath = Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "current_vless.txt");
            if (!File.Exists(linkPath)) { StopSelf(); return StartCommandResult.Sticky; }
            string vlessLink = File.ReadAllText(linkPath).Trim();
            var builder = new Builder(this)
                .AddAddress("172.19.0.1", 30)
                .AddRoute("0.0.0.0", 0)
                .AddRoute("::", 0)
                .AddDnsServer("8.8.8.8")
                .AddDisallowedApplication(PackageName!)
                .SetSession("obxodka_singbox")
                .SetMtu(1500)
                .SetBlocking(false)
                .SetUnderlyingNetworks(null);
            string excludedAppsStr = Preferences.Get("ExcludedApps", "");
            if (!string.IsNullOrEmpty(excludedAppsStr))
            {
                var excludedApps = excludedAppsStr.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var appPackage in excludedApps)
                {
                    try
                    {
                        builder.AddDisallowedApplication(appPackage);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[VPN SPLIT] Ошибка добавления {appPackage}: {ex.Message}");
                    }
                }
            }
            _vpnInterface = builder.Establish();
            if (_vpnInterface == null) { StopSelf(); return StartCommandResult.Sticky; }
            int fd = _vpnInterface.Fd;
            try
            {
                var javaFd = _vpnInterface.FileDescriptor;
                int f = global::Android.Systems.Os.FcntlInt(javaFd, global::Android.Systems.OsConstants.FGetfd, 0);
                global::Android.Systems.Os.FcntlInt(javaFd, global::Android.Systems.OsConstants.FSetfd, f & ~global::Android.Systems.OsConstants.FdCloexec);
            }
            catch { }
            Task.Run(() =>
            {
                try
                {
                    StartEngine(vlessLink, fd);
                    IsVpnRunning = true;
                    NativeStateChanged?.Invoke(true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VPN FATAL] {ex.Message}");
                    StopSelf();
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN FATAL ERROR] {ex.Message}");
            StopSelf();
        }
        return StartCommandResult.Sticky;
    }
    private void StartForegroundNotification()
    {
        string channelId = "obxodka_vpn_channel";
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(channelId, "VPN Статус", NotificationImportance.Low);
            var manager = (NotificationManager?)GetSystemService(NotificationService);
            manager?.CreateNotificationChannel(channel);
        }
        var openAppIntent = new Intent(this, typeof(MainActivity));
        openAppIntent.AddFlags(ActivityFlags.SingleTop);
        var pendingOpenApp = PendingIntent.GetActivity(this, 0, openAppIntent, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
        var notification = new Notification.Builder(this, channelId)
            .SetContentTitle("🛡️ Obxodka VPN активен")
            .SetContentText("Трафик защищен. Нажмите для управления.")
            .SetSmallIcon(global::Android.Resource.Drawable.IcSecure)
            .SetContentIntent(pendingOpenApp)
            .SetOngoing(true)
            .Build();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            StartForeground(1001, notification, ForegroundService.TypeDataSync);
        else
            StartForeground(1001, notification);
    }
    public static void ForceStop()
    {
        IsVpnRunning = false;
        NativeStateChanged?.Invoke(false);
        var instance = Instance;
        if (instance != null)
        {
            try
            {
                var dummyBuilder = new Builder(instance);
                dummyBuilder.AddAddress("10.0.0.1", 32);
                dummyBuilder.AddRoute("0.0.0.0", 0);
                var dummyInterface = dummyBuilder.Establish();
                dummyInterface?.Close();
                dummyInterface?.Dispose();
            }
            catch { }
            try
            {
                instance._vpnInterface?.Close();
                instance._vpnInterface?.Dispose();
                instance._vpnInterface = null;
            }
            catch { }
            Task.Run(() =>
            {
                try
                {
                    if (instance._commandServer != null)
                    {
                        var closeMethod = instance._commandServer.Class.GetMethod("close");
                        closeMethod?.Invoke(instance._commandServer);
                        instance._commandServer = null;
                    }
                }
                catch { }
                instance.StopForeground(true);
                instance.StopSelf();
                Instance = null;
            });
        }
        else
        {
            var context = global::Android.App.Application.Context;
            context.StopService(new Intent(context, typeof(ObxodkaVpnService)));
        }
    }
    public override void OnDestroy()
    {
        IsVpnRunning = false;
        NativeStateChanged?.Invoke(false);
        UnregisterNetworkCallback();
        try
        {
            if (_commandServer != null)
            {
                var closeMethod = _commandServer.Class.GetMethod("close");
                closeMethod?.Invoke(_commandServer);
                _commandServer = null;
            }
        }
        catch { }
        try
        {
            if (_vpnInterface != null)
            {
                _vpnInterface.Close();
                _vpnInterface.Dispose();
                _vpnInterface = null;
            }
        }
        catch { }
        Instance = null;
        StopForeground(true);
        base.OnDestroy();
    }
    private void RegisterNetworkCallback()
    {
        try
        {
            var connectivityManager = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (connectivityManager == null) return;
            var request = new NetworkRequest.Builder()?.AddCapability(NetCapability.Internet)?.Build();
            if (request != null)
            {
                _networkCallback = new VpnNetworkCallback(this);
                connectivityManager.RegisterNetworkCallback(request, _networkCallback);
            }
        }
        catch { }
    }
    private void UnregisterNetworkCallback()
    {
        try
        {
            var connectivityManager = (ConnectivityManager?)GetSystemService(ConnectivityService);
            if (_networkCallback != null) connectivityManager?.UnregisterNetworkCallback(_networkCallback);
        }
        catch { }
    }
    public void ReloadSingboxEngine()
    {
        if (!IsVpnRunning || _commandServer == null) return;
        try
        {
            string linkPath = Path.Combine(global::System.Environment.GetFolderPath(global::System.Environment.SpecialFolder.LocalApplicationData), "current_vless.txt");
            if (!File.Exists(linkPath)) return;
            string vlessLink = File.ReadAllText(linkPath).Trim();
            int fd = _vpnInterface?.Fd ?? -1;
            if (fd == -1) return;
            string config = GenerateSingBoxConfig(vlessLink, fd);
            var classLoader = global::Android.App.Application.Context.ClassLoader!;
            var overrideClass = classLoader.LoadClass("io.nekohasekai.libbox.OverrideOptions")!;
            var overrideInstance = overrideClass.GetConstructor(Array.Empty<Java.Lang.Class>()).NewInstance();
            var startMethod = _commandServer.Class.GetMethod("startOrReloadService", classLoader.LoadClass("java.lang.String"), overrideClass);
            startMethod?.Invoke(_commandServer, new Java.Lang.String(config), overrideInstance);
        }
        catch (Exception ex) { Debug.WriteLine($"[VPN RELOAD ERROR] {ex.Message}"); }
    }
    private class VpnNetworkCallback : ConnectivityManager.NetworkCallback
    {
        private readonly ObxodkaVpnService _service;
        public VpnNetworkCallback(ObxodkaVpnService service) => _service = service;
        public override void OnAvailable(Network network)
        {
            if (IsVpnRunning) Task.Delay(1000).ContinueWith(_ => _service.ReloadSingboxEngine());
        }
    }
    private string GenerateSingBoxConfig(string link, int fd)
    {
        var uri = new Uri(link);
        var query = HttpUtility.ParseQueryString(uri.Query);
        string uuid = uri.UserInfo;
        string server = uri.Host;
        int port = uri.Port > 0 ? uri.Port : 443;
        string sni = query["sni"] ?? server;
        string pbk = query["pbk"] ?? "";
        string sid = query["sid"] ?? "";
        string fp = query["fp"] ?? "chrome";
        var config = new
        {
            log = new { level = "warn" },
            dns = new
            {
                servers = new[] {
                    new { tag = "google", address = "8.8.8.8", detour = "proxy" },
                    new { tag = "local", address = "local", detour = "direct" }
                },
                rules = new[] { new { outbound = "any", server = "local" } }
            },
            inbounds = new[] {
                new {
                    type = "tun", tag = "tun-in",
                    address = new[] { "172.19.0.1/30" },
                    mtu = 1500, stack = "gvisor",
                    sniff = true, auto_route = false, strict_route = false
                }
            },
            outbounds = new object[] {
                new {
                    type = "vless", tag = "proxy",
                    server = server, server_port = port,
                    uuid = uuid, flow = "",
                    tls = new {
                        enabled = true, server_name = sni,
                        utls = new { enabled = true, fingerprint = fp },
                        reality = new { enabled = true, public_key = pbk, short_id = sid }
                    }
                },
                new { type = "direct", tag = "direct" }
            },
            route = new
            {
                rules = new[] { new { protocol = "dns", outbound = "proxy" } },
                final = "proxy"
            }
        };
        return JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = false });
    }
    private void StartEngine(string vlessLink, int fd)
    {
        string config = GenerateSingBoxConfig(vlessLink, fd);
        var context = global::Android.App.Application.Context;
        var classLoader = context.ClassLoader!;
        var libboxClass = classLoader.LoadClass("io.nekohasekai.libbox.Libbox")!;
        try
        {
            var optionsClass = classLoader.LoadClass("io.nekohasekai.libbox.SetupOptions")!;
            var optionsInstance = optionsClass.GetConstructor(Array.Empty<Java.Lang.Class>()).NewInstance();
            string baseDir = context.FilesDir!.AbsolutePath;
            string tempDir = context.CacheDir!.AbsolutePath;
            var strClass = classLoader.LoadClass("java.lang.String")!;
            optionsClass.GetMethod("setBasePath", strClass)?.Invoke(optionsInstance, new Java.Lang.String(baseDir));
            optionsClass.GetMethod("setWorkingPath", strClass)?.Invoke(optionsInstance, new Java.Lang.String(baseDir));
            optionsClass.GetMethod("setTempPath", strClass)?.Invoke(optionsInstance, new Java.Lang.String(tempDir));
            libboxClass.GetMethod("setup", optionsClass)?.Invoke(null, optionsInstance);
        }
        catch { }
        var cshClass = classLoader.LoadClass("io.nekohasekai.libbox.CommandServerHandler")!;
        var cshProxy = Java.Lang.Reflect.Proxy.NewProxyInstance(classLoader, new Java.Lang.Class[] { cshClass }, new CommandServerHandlerImpl());
        var piClass = classLoader.LoadClass("io.nekohasekai.libbox.PlatformInterface")!;
        var piProxy = Java.Lang.Reflect.Proxy.NewProxyInstance(classLoader, new Java.Lang.Class[] { piClass }, new PlatformInterfaceImpl(fd));
        var newServerMethod = libboxClass.GetMethod("newCommandServer", cshClass, piClass);
        _commandServer = newServerMethod?.Invoke(null, (Java.Lang.Object)cshProxy!, (Java.Lang.Object)piProxy!);
        var overrideClass = classLoader.LoadClass("io.nekohasekai.libbox.OverrideOptions")!;
        var overrideInstance = overrideClass.GetConstructor(Array.Empty<Java.Lang.Class>()).NewInstance();
        var stringClass = classLoader.LoadClass("java.lang.String")!;
        var startMethod = _commandServer?.Class.GetMethod("startOrReloadService", stringClass, overrideClass);
        startMethod?.Invoke(_commandServer, new Java.Lang.String(config), overrideInstance);
    }
}