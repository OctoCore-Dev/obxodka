using Android.Content;
using Android.Content.Res;
using Android.OS;
using Android.Provider;

namespace obxodka.Platforms.Android;

//предоставление системных сведений об устройстве android (модель, производитель, id, тип)
[SupportedOSPlatform("android29.0")]
public sealed class AndroidDeviceInfoService(Context context) : IDeviceInfoService
{
    private readonly Context _context = context;

    //уникальный анонимный идентификатор устройства android id
    public string DeviceId =>
        Settings.Secure.GetString(_context.ContentResolver, Settings.Secure.AndroidId) ?? string.Empty;

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
            catch { }

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

    //версия операционной системы android
    public string VersionString => Build.VERSION.Release ?? string.Empty;

    //имя платформы
    public string Platform => "Android";

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

            var type = config.UiMode & UiMode.TypeMask;
            return type == UiMode.TypeTelevision
                ? AppDeviceIdiom.TV
                : type == UiMode.TypeWatch
                ? AppDeviceIdiom.Watch
                : type == UiMode.TypeCar
                ? AppDeviceIdiom.Car
                : type == UiMode.TypeDesk ? AppDeviceIdiom.Desktop : IsTablet(config) ? AppDeviceIdiom.Tablet : AppDeviceIdiom.Phone;
        }
    }

    //проверка является ли устройство планшетом по ширине экрана (>= 600dp)
    private static bool IsTablet(Configuration config) =>
        config.SmallestScreenWidthDp >= 600 ||
        (config.ScreenLayout & ScreenLayout.SizeMask) >= ScreenLayout.SizeLarge;

    //определение физического устройства или виртуального эмулятора
    public AppDeviceType DeviceType =>
        (Build.Fingerprint?.Contains("generic", StringComparison.OrdinalIgnoreCase) == true) ||
        (Build.Fingerprint?.Contains("emulator", StringComparison.OrdinalIgnoreCase) == true)
            ? AppDeviceType.Virtual
            : AppDeviceType.Physical;
}
