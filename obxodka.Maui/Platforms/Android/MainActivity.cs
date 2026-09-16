using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using AndroidX.Core.View;
using obxodka.Platforms.Android;

namespace obxodka;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[SupportedOSPlatform("android29.0")]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int VpnRequestCode = 1001;
    private const int VpnCustomRequestCode = 1337;
    private const int NotificationRequestCode = 1002;

    private static TaskCompletionSource<bool>? t_vpnPermTcs;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        _ = (Window?.DecorView?.Post(() =>
        {
            try
            {
                RequestNotificationPermission();
            }
            catch { }
        }));

        try
        {
            if (Window is { } window)
            {
                WindowCompat.SetDecorFitsSystemWindows(window, false);

                if (OperatingSystem.IsAndroidVersionAtLeast(29))
                {
#pragma warning disable CA1422
                    window.NavigationBarContrastEnforced = false;
                    window.StatusBarContrastEnforced = false;
                }

                if (!OperatingSystem.IsAndroidVersionAtLeast(35))
                {
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
            return insets;
        }
    }

    public static void UpdateSystemBarsTheme(Color? bgBaseColor)
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
    }

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
            System.Diagnostics.Debug.WriteLine($"[NOTIFICATION REQ ERROR] {ex.Message}");
        }
    }

    private void RequestIgnoreBatteryOptimizations()
    {
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
                _ = builder.SetCancelable(false);
                using var dialog = builder.Create();
                dialog?.Show();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[BATTERY REQ ERROR] {ex.Message}");
        }
    }

    public static void StartVpnService()
    {
        if (Platform.CurrentActivity is not MainActivity activity)
        {
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
            _ = (t_vpnPermTcs?.TrySetResult(resultCode == Result.Ok));
        }
    }

    private static void StartActualService(Context context)
    {
        var intent = new Intent(context, typeof(OctopusVpnService))
            .SetAction("START");
        _ = context.StartForegroundService(intent);
    }

    public static async Task<bool> RequestVpnPermissionAsync(Intent intent)
    {
        t_vpnPermTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var activity = Platform.CurrentActivity;
        activity?.StartActivityForResult(intent, VpnCustomRequestCode);
        return await t_vpnPermTcs.Task;
    }
}
