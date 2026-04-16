namespace obxodka;
[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : MauiAppCompatActivity
{
    private const int VpnRequestCode = 1001;
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        RequestIgnoreBatteryOptimizations();
    }
    public void RequestIgnoreBatteryOptimizations()
    {
        try
        {
            var pm = (PowerManager?)GetSystemService(PowerService);
            if (pm != null && !pm.IsIgnoringBatteryOptimizations(PackageName))
            {
                var intent = new Intent(Android.Provider.Settings.ActionRequestIgnoreBatteryOptimizations);
                intent.SetData(Android.Net.Uri.Parse("package:" + PackageName));
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
        var activity = Platform.CurrentActivity as MainActivity;
        if (activity == null) return;
        var vpnIntent = VpnService.Prepare(activity);
        if (vpnIntent != null)
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
                System.Diagnostics.Debug.WriteLine("[ANDROID VPN] Юзер нажал отмену в системном окне.");
            }
        }
    }
    private static void StartActualService(Android.Content.Context context)
    {
        var intent = new Intent(context, typeof(Platforms.Android.Services.ObxodkaVpnService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }
}