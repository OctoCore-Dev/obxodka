namespace obxodka.Helpers;

//тип устройства для иконки fluent ui
public enum DeviceKind
{
    Unknown = 0,
    Desktop = 1,
    Phone = 2
}

//определение типа устройства по названию для выбора иконки в интерфейсе
public static class DeviceClassifier
{
    private static readonly string[] s_desktopKeywords = ["windows", "desktop", "laptop", "pc", "mac", "imac", "macbook", "linux", "ubuntu", "debian", "fedora"];
    private static readonly string[] s_phoneKeywords = ["iphone", "ipad", "ios", "android", "samsung", "pixel", "phone", "xiaomi", "redmi", "poco", "huawei", "honor", "oppo", "vivo", "oneplus", "realme"];

    //категория устройства пк телефон или неизвестно
    public static DeviceKind Classify(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return DeviceKind.Unknown;
        }

        var span = deviceName.AsSpan();

        return ContainsAny(span, s_desktopKeywords)
            ? DeviceKind.Desktop
            : ContainsAny(span, s_phoneKeywords)
            ? DeviceKind.Phone
            : DeviceKind.Unknown;
    }

    //проверка наличия ключевых слов в строке
    private static bool ContainsAny(ReadOnlySpan<char> source, ReadOnlySpan<string> keywords)
    {
        foreach (var keyword in keywords)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
