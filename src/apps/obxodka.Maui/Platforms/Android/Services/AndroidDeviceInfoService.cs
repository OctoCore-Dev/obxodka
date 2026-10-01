using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Provider;

namespace obxodka.Maui.Platforms.Android.Services;

[SupportedOSPlatform("android29.0")]
public sealed class AndroidDeviceInfoService(Context context) : IDeviceInfoService
{
    private readonly Context _context = context;

    public string DeviceId =>
        Settings.Secure.GetString(_context.ContentResolver, Settings.Secure.AndroidId) ?? string.Empty;

    public string Model => Build.Model ?? string.Empty;

    public string Manufacturer => Build.Manufacturer ?? string.Empty;

    public string Name => Build.Device ?? string.Empty;

    public string VersionString => Build.VERSION.Release ?? string.Empty;

    public string Platform => "Android";

    public AppDeviceIdiom Idiom
    {
        get
        {
            var uiMode = _context.Resources?.Configuration?.UiMode & UiMode.TypeMask;
            return uiMode switch
            {
                UiMode.TypeNormal => AppDeviceIdiom.Phone,
                UiMode.TypeDesk => AppDeviceIdiom.Desktop,
                UiMode.TypeCar => AppDeviceIdiom.Car,
                UiMode.TypeTelevision => AppDeviceIdiom.TV,
                UiMode.TypeAppliance => AppDeviceIdiom.Unknown,
                UiMode.TypeWatch => AppDeviceIdiom.Watch,
                UiMode.TypeVrHeadset => AppDeviceIdiom.Unknown,
                UiMode.NightMask => AppDeviceIdiom.Phone,
                UiMode.NightNo => AppDeviceIdiom.Phone,
                UiMode.NightUndefined => AppDeviceIdiom.Phone,
                UiMode.NightYes => AppDeviceIdiom.Phone,
                UiMode.TypeMask => AppDeviceIdiom.Phone,
                null => AppDeviceIdiom.Unknown,
                _ => AppDeviceIdiom.Phone
            };
        }
    }

    public AppDeviceType DeviceType =>
        (Build.Fingerprint?.Contains("generic", StringComparison.OrdinalIgnoreCase) == true) ||
        (Build.Fingerprint?.Contains("emulator", StringComparison.OrdinalIgnoreCase) == true)
            ? AppDeviceType.Virtual
            : AppDeviceType.Physical;
}
