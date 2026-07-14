using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net;
using Android.OS;
using obxodka.Platforms.Android;
namespace obxodka;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[SupportedOSPlatform("android30.0")]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int VpnRequestCode = 1001;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestIgnoreBatteryOptimizations();
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
        {
            AndroidX.Core.View.WindowCompat.SetDecorFitsSystemWindows(Window!, false);
#pragma warning disable CA1422, CS0618
            Window!.SetStatusBarColor(Android.Graphics.Color.Transparent);
            Window!.SetNavigationBarColor(Android.Graphics.Color.Transparent);
#pragma warning restore CA1422, CS0618
        }
    }
    private void RequestIgnoreBatteryOptimizations()
    {
        try
        {
            if (GetSystemService(PowerService) is PowerManager pm && !pm.IsIgnoringBatteryOptimizations(PackageName))
            {
                var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations)
                    .SetData(Android.Net.Uri.Parse("package:" + PackageName));
                StartActivity(intent);
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
        var vpnIntent = VpnService.Prepare(activity);
        if (vpnIntent != null)
        {
#pragma warning disable CS0618
            activity.StartActivityForResult(vpnIntent, VpnRequestCode);
#pragma warning restore CS0618
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
        else if (requestCode == 1337)
        {
            _ = (t_vpnPermTcs?.TrySetResult(resultCode == Result.Ok));
        }
    }
    private static void StartActualService(Context context)
    {
        var intent = new Intent(context, typeof(OctopusVpnService))
            .SetAction("START");
        _ = Build.VERSION.SdkInt >= BuildVersionCodes.O
            ? context.StartForegroundService(intent)
            : context.StartService(intent);
    }
    private static TaskCompletionSource<bool>? t_vpnPermTcs;
    public static async Task<bool> RequestVpnPermissionAsync(Intent intent)
    {
        t_vpnPermTcs = new TaskCompletionSource<bool>();
        var activity = Platform.CurrentActivity;
        activity?.StartActivityForResult(intent, 1337);
        return await t_vpnPermTcs.Task;
    }
}
