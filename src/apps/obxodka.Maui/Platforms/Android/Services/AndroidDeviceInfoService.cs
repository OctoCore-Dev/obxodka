using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Provider;

namespace obxodka.Services;

//предоставление системных сведений об устройстве android (модель, производитель, id, тип)
[SupportedOSPlatform("android30.0")]
public sealed class AndroidDeviceInfoService(Context context) : IDeviceInfoService
{
    private readonly Context _context = context;

    //уникальный анонимный идентификатор устройства android id
    public string DeviceId =>
        _context.ContentResolver is { } resolver
            ? Settings.Secure.GetString(resolver, Settings.Secure.AndroidId) ?? string.Empty
            : string.Empty;

    //определение физического устройства или виртуального эмулятора
    public AppDeviceType DeviceType =>
        Build.Fingerprint?.Contains("generic", StringComparison.OrdinalIgnoreCase) == true ||
        Build.Fingerprint?.Contains("emulator", StringComparison.OrdinalIgnoreCase) == true
            ? AppDeviceType.Virtual
            : AppDeviceType.Physical;

    //версия операционной системы android
    public string VersionString => Build.VERSION.Release ?? string.Empty;

    //имя платформы
    public string Platform => "Android";

    //модель устройства (например sm-s918b или pixel 8)
    public string Model => Build.Model ?? string.Empty;

    //производитель устройства (samsung, google, xiaomi)
    public string Manufacturer => Build.Manufacturer ?? string.Empty;

    //человекочитаемое имя устройства (пользовательское имя или производитель + модель)
    public string Name
    {
        get
        {
            try
            {
                var customName = Settings.Global.GetString(_context.ContentResolver, "device_name");
                if (!string.IsNullOrWhiteSpace(customName))
                {
                    return customName.Trim();
                }

                var btName = Settings.Secure.GetString(_context.ContentResolver, "bluetooth_name");
                if (!string.IsNullOrWhiteSpace(btName))
                {
                    return btName.Trim();
                }
            }
            catch
            {
                // ignored
            }

            var manufacturer = Build.Manufacturer?.Trim() ?? string.Empty;
            var model = Build.Model?.Trim() ?? string.Empty;

            return string.IsNullOrWhiteSpace(manufacturer)
                ? string.IsNullOrWhiteSpace(model) ? "Android Device" : model
                : string.IsNullOrWhiteSpace(model)
                    ? manufacturer
                    : model.StartsWith(manufacturer, StringComparison.OrdinalIgnoreCase)
                        ? model
                        : $"{manufacturer} {model}";
        }
    }

    //форм-фактор устройства (телефон, планшет, телевизор, пк/dex, часы, авто)
    public AppDeviceIdiom Idiom
    {
        get
        {
            var config = _context.Resources?.Configuration;
            if (config is null)
            {
                return AppDeviceIdiom.Unknown;
            }

            var type = (UiMode)((int)UiMode.TypeMask & (int)config.UiMode);
            return type switch
            {
                UiMode.TypeTelevision => AppDeviceIdiom.Tv,
                UiMode.TypeWatch => AppDeviceIdiom.Watch,
                UiMode.TypeCar => AppDeviceIdiom.Car,
                UiMode.TypeDesk => AppDeviceIdiom.Desktop,
                _ => IsTablet(config) ? AppDeviceIdiom.Tablet : AppDeviceIdiom.Phone
            };
        }
    }

    //проверка является ли устройство планшетом по ширине экрана (>= 600dp)
    private static bool IsTablet(Configuration config) =>
        config.SmallestScreenWidthDp >= 600 ||
        ((int)config.ScreenLayout & (int)ScreenLayout.SizeMask) >= (int)ScreenLayout.SizeLarge;
}
