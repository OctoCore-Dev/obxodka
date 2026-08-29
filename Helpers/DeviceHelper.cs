namespace obxodka.Helpers;

internal static class DeviceHelper
{
    public static string Hwid
    {
        get => field ??= ResolveHwid();
        private set;
    }

    public static string GetHwid() => Hwid;

    public static string DeviceName => $"{DeviceInfo.Current.Platform} | {DeviceInfo.Current.Name}";
    public static string GetDeviceName() => DeviceName;

    private static string ResolveHwid()
    {
        if (GetPlatformId() is { Length: > 0 } platformId)
        {
            return platformId;
        }

        var cached = Preferences.Default.Get("hwid_cache", string.Empty);
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var fallback = Guid.NewGuid().ToString("N");
        Preferences.Default.Set("hwid_cache", fallback);
        return fallback;
    }

    private static string? GetPlatformId()
    {
#if WINDOWS
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") is string guid ? guid.Trim(['{', '}', ' ']) : null;
        }
        catch
        {
            return null;
        }
#elif ANDROID
        try
        {
            var id = Android.Provider.Settings.Secure.GetString(
                Platform.AppContext.ContentResolver,
                Android.Provider.Settings.Secure.AndroidId);

            return id is not (null or "" or "9774d56d682e549c") ? id : null;
        }
        catch
        {
            return null;
        }
#elif IOS || MACCATALYST
        try
        {
            return OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
                ? UIKit.UIDevice.CurrentDevice.IdentifierForVendor?.AsString()
                : null;
        }
        catch
        {
            return null;
        }
#else
        return null;
#endif
    }
}
