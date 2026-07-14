namespace obxodka.Helpers;

internal static class DeviceHelper
{
    private static string? t_cachedHwid;
    public static string GetHwid()
    {
        if (!string.IsNullOrEmpty(t_cachedHwid))
        {
            return t_cachedHwid;
        }
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            var guid = key?.GetValue("MachineGuid")?.ToString();
            t_cachedHwid = guid?.Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal).Trim();
        }
        catch
        {
        }
#elif ANDROID
        try
        {
            var context = Platform.AppContext;
            var androidId = Android.Provider.Settings.Secure.GetString(context.ContentResolver, Android.Provider.Settings.Secure.AndroidId);
            if (!string.IsNullOrEmpty(androidId) && androidId != "9774d56d682e549c")
            {
                t_cachedHwid = androidId;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWID ANDROID ERROR]: {ex.Message}");
        }
#elif IOS || MACCATALYST
        try
        {
#pragma warning disable CA1416
            var idfv = UIKit.UIDevice.CurrentDevice.IdentifierForVendor?.AsString();
#pragma warning restore CA1416
            if (!string.IsNullOrEmpty(idfv))
            {
                t_cachedHwid = idfv;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HWID IOS ERROR]: {ex.Message}");
        }
#endif
        if (string.IsNullOrEmpty(t_cachedHwid))
        {
            t_cachedHwid = Preferences.Default.Get("hwid_cache", string.Empty);
            if (string.IsNullOrEmpty(t_cachedHwid))
            {
                t_cachedHwid = Guid.NewGuid().ToString("N");
                Preferences.Default.Set("hwid_cache", t_cachedHwid);
            }
        }
        return t_cachedHwid;
    }
    public static string GetDeviceName() =>
        $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}";
}
