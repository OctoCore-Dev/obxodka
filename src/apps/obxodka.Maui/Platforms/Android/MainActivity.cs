using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using Android.Provider;
using AndroidX.Core.View;
using Object = Java.Lang.Object;
using Uri = Android.Net.Uri;
using View = Android.Views.View;

namespace obxodka;

//главная активность android приложения с обработкой оконных отступов и разрешений vpn
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout |
                           ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
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
[MetaData("android.app.shortcuts", Resource = "@xml/shortcuts")]
[SupportedOSPlatform("android30.0")]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int VpnRequestCode = 1001;
    private const int VpnCustomRequestCode = 1337;
    private const int NotificationRequestCode = 1002;

    private static TaskCompletionSource<bool>? s_vpnPermTcs;

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "WindowInsetsHandler is retained by Android View hierarchy")]
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        try
        {
            if (Window is { } earlyWindow)
            {
                WindowCompat.SetDecorFitsSystemWindows(earlyWindow, false);

                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
#pragma warning disable CA1422
                    earlyWindow.NavigationBarContrastEnforced = false;
                    earlyWindow.StatusBarContrastEnforced = false;
#pragma warning restore CA1422
                }
            }
        }
        catch
        {
            // ignored
        }

        base.OnCreate(savedInstanceState);

        AndroidBackgroundVpnController.EnsureStateHooked(ApplicationContext ?? this);

        if (Intent?.DataString is { Length: > 0 } initialUri)
        {
            App.HandleDeepLink(initialUri);
        }

        DispatchBackgroundIntentAction(Intent);

        _ = Window?.DecorView.Post(() =>
        {
            try
            {
                RequestNotificationPermission();
            }
            catch
            {
                // ignored
            }
        });

        try
        {
            if (Window is not { } window)
            {
                return;
            }

            WindowCompat.SetDecorFitsSystemWindows(window, false);

            if (!OperatingSystem.IsAndroidVersionAtLeast(35))
            {
                window.SetStatusBarColor(Android.Graphics.Color.Transparent);
                window.SetNavigationBarColor(Android.Graphics.Color.Transparent);
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
        catch
        {
            // ignored
        }
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Intent = intent;
        if (intent?.DataString is { Length: > 0 } dataUri)
        {
            App.HandleDeepLink(dataUri);
        }

        DispatchBackgroundIntentAction(intent);
    }

    //передача фонового действия из плитки шторки или голосового ассистента в главное окно
    private static void DispatchBackgroundIntentAction(Intent? intent)
    {
        var rawAction = intent?.GetStringExtra(AndroidBackgroundVpnController.ExtraBackgroundAction);
        var action = AndroidBackgroundVpnController.ParseBackgroundAction(rawAction);
        if (action != BackgroundVpnActionKind.None)
        {
            App.HandleBackgroundAction(action);
        }
    }

    //динамическое обновление контрастности иконок статус бара и навигации под тему оформления
    public static void UpdateSystemBarsTheme(Color? bgBaseColor) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                if (Platform.CurrentActivity?.Window is not { } window)
                {
                    return;
                }

                var controller = WindowCompat.GetInsetsController(window, window.DecorView);
                if (controller is null)
                {
                    return;
                }

                var isLight = bgBaseColor != null
                    ? bgBaseColor.Red * 0.299 + bgBaseColor.Green * 0.587 + bgBaseColor.Blue * 0.114 > 0.55
                    : Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light;
                controller.AppearanceLightStatusBars = isLight;
                controller.AppearanceLightNavigationBars = isLight;
            }
            catch
            {
                // ignored
            }
        });

    //запрос разрешения на отправку уведомлений android 13+
    private void RequestNotificationPermission()
    {
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(33) &&
                CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                RequestPermissions([Manifest.Permission.PostNotifications], NotificationRequestCode);
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[NOTIFICATION REQ ERROR] {ex.Message}");
        }
    }

    //запрос на отключение оптимизации батареи для стабильной работы туннеля в фоне через neo-диалог
    private void RequestIgnoreBatteryOptimizations()
    {
        if (IsFinishing || IsDestroyed)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
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

                        var accepted = await NeoAlert.ShowConfirmAsync(
                            "Фоновая работа и батарея",
                            "Для стабильной работы VPN-соединения и защиты от разрывов при выключенном экране необходимо разрешить работу приложения без ограничений расхода батареи.\n\nРазрешить сейчас?",
                            "Разрешить",
                            "Позже");

                        if (accepted && !IsFinishing && !IsDestroyed)
                        {
                            using var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations)
                                .SetData(Uri.Parse($"package:{pkgName}"));
                            StartActivity(intent);
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogError($"[BATTERY REQ ERROR] {ex.Message}");
                }
            }
            catch
            {
                // ignored
            }
        });
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
            StartActualService(Android.App.Application.Context);
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

    protected override void OnDestroy()
    {
        _ = Interlocked.Exchange(ref s_vpnPermTcs, null)?.TrySetResult(false);
        base.OnDestroy();
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        switch (requestCode)
        {
            case VpnRequestCode when resultCode == Result.Ok:
                StartActualService(this);
                break;
            case VpnRequestCode:
                AndroidVpnService.Orchestrator.ReportError("Пользователь отклонил запрос на подключение");
                break;
            case VpnCustomRequestCode:
                _ = Interlocked.Exchange(ref s_vpnPermTcs, null)?.TrySetResult(resultCode == Result.Ok);
                break;
        }
    }

    internal static void StartActualService(Context context)
    {
        using var intent = new Intent(context, typeof(OctopusVpnService));
        _ = intent.SetAction("START");
        _ = context.StartForegroundService(intent);
    }

    //асинхронный запрос разрешения на vpn с защитой от отсутствия активности и отмены токена
    public static async Task<bool> RequestVpnPermissionAsync(Intent intent, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var activity = Platform.CurrentActivity;
        if (activity is null || activity.IsFinishing || activity.IsDestroyed)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Interlocked.Exchange(ref s_vpnPermTcs, tcs)?.TrySetResult(false);

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
            _ = Interlocked.CompareExchange(ref s_vpnPermTcs, null, tcs);
            return false;
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _ = Interlocked.CompareExchange(ref s_vpnPermTcs, null, tcs);
            return false;
        }
        catch (System.OperationCanceledException)
        {
            _ = Interlocked.CompareExchange(ref s_vpnPermTcs, null, tcs);
            throw;
        }
    }

    //обработчик отступов системных панелей для корректного отображения edge to edge с сохранением ime-инсетов клавиатуры
    private sealed class WindowInsetsHandler : Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(View? v, WindowInsetsCompat? insets)
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

            //обнуляем только системные панели и вырез, оставляя инсеты клавиатуры (ime) нетронутыми
            using var insetsBuilder = new WindowInsetsCompat.Builder(insets);
            _ = insetsBuilder.SetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout(),
                AndroidX.Core.Graphics.Insets.None);
            return insetsBuilder.Build() ?? WindowInsetsCompat.Consumed;
        }
    }
}
