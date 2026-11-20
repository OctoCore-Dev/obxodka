namespace obxodka.Helpers;

//тип устройства для иконки fluent ui
public enum DeviceKind
{
    Unknown = 0,
    Desktop = 1,
    Phone = 2
}

//определение типа устройства по названию с simd поиском searchvalues
public static class DeviceClassifier
{
    private static readonly SearchValues<string> s_desktopKeywords = SearchValues.Create(
        ["windows", "desktop", "laptop", "pc", "mac", "imac", "macbook", "linux", "ubuntu", "debian", "fedora"],
        StringComparison.OrdinalIgnoreCase);

    private static readonly SearchValues<string> s_phoneKeywords = SearchValues.Create(
        [
            "iphone", "ipad", "ios", "android", "samsung", "pixel", "phone", "xiaomi", "redmi", "poco", "huawei", "honor", "oppo", "vivo",
            "oneplus", "realme"
        ],
        StringComparison.OrdinalIgnoreCase);

    //категория устройства пк телефон или неизвестно
    public static DeviceKind Classify(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return DeviceKind.Unknown;
        }

        var span = deviceName.AsSpan();

        return span.ContainsAny(s_desktopKeywords)
            ? DeviceKind.Desktop
            : span.ContainsAny(s_phoneKeywords)
                ? DeviceKind.Phone
                : DeviceKind.Unknown;
    }
}
