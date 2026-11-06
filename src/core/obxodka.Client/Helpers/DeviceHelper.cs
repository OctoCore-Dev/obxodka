namespace obxodka.Helpers;

//идентификация устройства и получение аппаратного id
public static class DeviceHelper
{
    //уникальный аппаратный идентификатор устройства
    public static string Hwid
    {
        get => field ??= ResolveHwid();
        private set;
    }

    //человекочитаемое имя устройства и платформы
    public static string DeviceName => $"{PlatformServices.DeviceInfo.Platform} | {PlatformServices.DeviceInfo.Name}";

    //вычисление или чтение сохраненного hwid
    private static string ResolveHwid()
    {
        var platformId = GetPlatformId();
        if (IsValidPlatformId(platformId))
        {
            return platformId!;
        }

        var cached = PlatformServices.Preferences.Get(AppPreferenceKeys.HwidCache, string.Empty);
        if (IsValidPlatformId(cached))
        {
            return cached;
        }

        var fallback = Guid.NewGuid().ToString("N");
        PlatformServices.Preferences.Set(AppPreferenceKeys.HwidCache, fallback);
        return fallback;
    }

    //проверка валидности hwid защита от фейковых bios uuid из нулей или ffff
    private static bool IsValidPlatformId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length < 8)
        {
            return false;
        }

        var clean = id.Replace("-", "", StringComparison.Ordinal).Trim();
        var allSame = true;
        var first = clean[0];
        foreach (var c in clean)
        {
            if (c != first)
            {
                allSame = false;
                break;
            }
        }

        return !allSame || (first != '0' && first != 'f' && first != 'F');
    }

    //получение идентификатора платформы из служб устройства
    private static string? GetPlatformId() =>
        string.IsNullOrWhiteSpace(PlatformServices.DeviceInfo.DeviceId) ? null : PlatformServices.DeviceInfo.DeviceId;
}
