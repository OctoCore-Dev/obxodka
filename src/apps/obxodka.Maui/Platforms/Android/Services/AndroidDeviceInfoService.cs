using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Provider;

namespace obxodka.Maui.Platforms.Android.Services;

[SupportedOSPlatform("android29.0")]
public sealed class AndroidDeviceInfoService(Context context) : IDeviceInfoService
{
    private readonly Context _context = context;

    public string DeviceId
    {
        get
        {
            var id = Settings.Secure.GetString(_context.ContentResolver, Settings.Secure.AndroidId);
            return id ?? string.Empty;
        }
    }

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
                UiMode.TypeTelevision => AppDeviceIdiom.TV,
                UiMode.TypeWatch => AppDeviceIdiom.Watch,
                UiMode.NightMask => throw new NotImplementedException(),
                UiMode.NightNo => throw new NotImplementedException(),
                UiMode.NightUndefined => throw new NotImplementedException(),
                UiMode.NightYes => throw new NotImplementedException(),
                UiMode.TypeAppliance => throw new NotImplementedException(),
                UiMode.TypeCar => throw new NotImplementedException(),
                UiMode.TypeDesk => throw new NotImplementedException(),
                UiMode.TypeMask => throw new NotImplementedException(),
                UiMode.TypeVrHeadset => throw new NotImplementedException(),
                null => throw new NotImplementedException(),
                _ => AppDeviceIdiom.Phone
            };
        }
    }

    public AppDeviceType DeviceType
    {
        get
        {
            var fingerprint = Build.Fingerprint?.ToLowerInvariant() ?? "";
            return fingerprint.Contains("generic") || fingerprint.Contains("emulator")
                ? AppDeviceType.Virtual
                : AppDeviceType.Physical;
        }
    }
}
