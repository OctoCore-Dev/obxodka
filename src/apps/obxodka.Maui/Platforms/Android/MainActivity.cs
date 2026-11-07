using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.View;
using obxodka.Platforms.Android;

namespace obxodka;

//главная активность android приложения с обработкой оконных отступов и разрешений vpn
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "obxodka",
    DataHost = "login")]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "https",
    DataHost = "qr",
    DataPathPrefix = "/qr")]
[SupportedOSPlatform("android29.0")]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int VpnRequestCode = 1001;
    private const int VpnCustomRequestCode = 1337;
    private const int NotificationRequestCode = 1002;

    private static TaskCompletionSource<bool>? s_vpnPermTcs;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "WindowInsetsHandler is retained by Android View hierarchy")]
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        try
        {
            if (Window is { } earlyWindow)
            {
                WindowCompat.SetDecorFitsSystemWindows(earlyWindow, false);

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
#pragma warning disable CA1422
                    earlyWindow.NavigationBarContrastEnforced = false;
                    earlyWindow.StatusBarContrastEnforced = false;
#pragma warning restore CA1422
                }
            }
        }
        catch { }

        base.OnCreate(savedInstanceState);

        if (Intent?.DataString is { Length: > 0 } initialUri)
        {
            App.HandleDeepLink(initialUri);
        }

        _ = Window?.DecorView?.Post(() =>
        {
            try
            {
                RequestNotificationPermission();
            }
            catch { }
        });

        try
        {
            if (Window is { } window)
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(35))
                {
#pragma warning disable CA1422
                    window.SetStatusBarColor(Android.Graphics.Color.Transparent);
                    window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
#pragma warning restore CA1422
                }

                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                if (controller is not null)
                {
                    var isLight = Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light;
                    controller.AppearanceLightStatusBars = isLight;
                    controller.AppearanceLightNavigationBars = isLight;
                }

                ViewCompat.SetOnApplyWindowInsetsListener(window.DecorView, new WindowInsetsHandler());
            }
        }
        catch { }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.DataString is { Length: > 0 } dataUri)
        {
            App.HandleDeepLink(dataUri);
        }
    }

    //обработчик отступов системных панелей для корректного отображения edge to edge
    private sealed class WindowInsetsHandler : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(Android.Views.View? v, WindowInsetsCompat? insets)
        {
            if (insets is null)
            {
                return insets;
            }

            var systemBars = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            var density = Platform.AppContext.Resources?.DisplayMetrics?.Density ?? 1.0f;
            var topDp = (systemBars?.Top ?? 0) / density;
            var bottomDp = (systemBars?.Bottom ?? 0) / density;

            SafeAreaHelper.NotifyInsetsChanged(topDp, bottomDp);

            return v is not null ? ViewCompat.OnApplyWindowInsets(v, insets) : insets;
        }
    }

    //динамическое обновление контрастности иконок статус бара и навигации под тему оформления
    public static void UpdateSystemBarsTheme(Color? bgBaseColor) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Platform.CurrentActivity?.Window is { } window)
                {
                    var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                    if (controller is not null)
                    {
                        var isLight = bgBaseColor != null
                            ? ((bgBaseColor.Red * 0.299) + (bgBaseColor.Green * 0.587) + (bgBaseColor.Blue * 0.114)) > 0.55
                            : Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light;
                        controller.AppearanceLightStatusBars = isLight;
                        controller.AppearanceLightNavigationBars = isLight;
                    }
                }
            }
            catch { }
        });

    //запрос разрешения на отправку уведомлений android 13+
    private void RequestNotificationPermission()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                CheckSelfPermission(Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                RequestPermissions([Android.Manifest.Permission.PostNotifications], NotificationRequestCode);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[NOTIFICATION REQ ERROR] {ex.Message}");
        }
    }

    //запрос на отключение оптимизации батареи для стабильной работы туннеля в фоне
    private void RequestIgnoreBatteryOptimizations()
    {
        //маршалинг диалога на главный поток андроид для защиты от looper исключения
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(RequestIgnoreBatteryOptimizations);
            return;
        }

        if (IsFinishing || IsDestroyed)
        {
            return;
        }

        try
        {
            if (PackageName is { Length: > 0 } pkgName &&
                GetSystemService(PowerService) is PowerManager pm &&
                !pm.IsIgnoringBatteryOptimizations(pkgName))
            {
                if (Preferences.Default.Get("HasRequestedBatteryOpt", false))
                {
                    return;
                }

                Preferences.Default.Set("HasRequestedBatteryOpt", true);

                using var builder = new AlertDialog.Builder(this);
                _ = builder.SetTitle("Внимание: Батарея");
                _ = builder.SetMessage("Для стабильной работы VPN-соединения необходимо отключить экономию заряда батареи для нашего приложения. Иначе система может прервать работу VPN в фоновом режиме.\n\nПожалуйста, нажмите «ОК», чтобы перейти в настройки и разрешить фоновую работу.");
                _ = builder.SetPositiveButton("ОК", (_, _) =>
                {
                    var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
                        .SetData(Android.Net.Uri.Parse($"package:{pkgName}"));
                    StartActivity(intent);
                });
                _ = builder.SetNegativeButton("Позже", (_, _) => { });
                _ = builder.SetCancelable(true);
                var dialog = builder.Create();
                if (dialog is not null)
                {
                    dialog.DismissEvent += (_, _) => dialog.Dispose();
                    dialog.Show();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[BATTERY REQ ERROR] {ex.Message}");
        }
    }

    //запуск системной службы vpn
    public static void StartVpnService()
    {
        if (!MainThread.IsMainThread)
        {
            MainThread.BeginInvokeOnMainThread(StartVpnService);
            return;
        }

        if (Platform.CurrentActivity is not MainActivity activity || activity.IsFinishing || activity.IsDestroyed)
        {
            StartActualService(Platform.AppContext);
            return;
        }

        activity.RequestIgnoreBatteryOptimizations();

        var vpnIntent = VpnService.Prepare(activity);
        if (vpnIntent is not null)
        {
            activity.StartActivityForResult(vpnIntent, VpnRequestCode);
        }
        else
        {
            StartActualService(activity);
        }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == VpnRequestCode)
        {
            if (resultCode == Result.Ok)
            {
                StartActualService(this);
            }
            else
            {
                AndroidVpnService.Instance.SetError("Пользователь отклонил запрос на подключение");
            }
        }
        else if (requestCode == VpnCustomRequestCode)
        {
            _ = s_vpnPermTcs?.TrySetResult(resultCode == Result.Ok);
        }
    }

    private static void StartActualService(Context context)
    {
        using var intent = new Intent(context, typeof(OctopusVpnService));
        _ = intent.SetAction("START");
        _ = context.StartForegroundService(intent);
    }

    //асинхронный запрос разрешения на vpn с защитой от отсутствия активности
    public static async Task<bool> RequestVpnPermissionAsync(Intent intent)
    {
        var activity = Platform.CurrentActivity;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        s_vpnPermTcs = tcs;

        var launched = await MainThread.InvokeOnMainThreadAsync(() =>
        {
            var current = Platform.CurrentActivity;
            if (current is null || current.IsFinishing || current.IsDestroyed)
            {
                return false;
            }

            try
            {
                current.StartActivityForResult(intent, VpnCustomRequestCode);
                return true;
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[VPN PERM ERROR] {ex.Message}");
                return false;
            }
        }).ConfigureAwait(false);

        if (!launched)
        {
            s_vpnPermTcs = null;
            return false;
        }

        return await tcs.Task.ConfigureAwait(false);
    }
}
