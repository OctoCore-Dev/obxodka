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

    private static string? GetPlatformId() =>
        string.IsNullOrWhiteSpace(DeviceInfo.Current.DeviceId) ? null : DeviceInfo.Current.DeviceId;
}
