namespace obxodka.Helpers;

public enum DeviceKind
{
    Unknown = 0,
    Desktop = 1,
    Phone = 2
}

public static class DeviceClassifier
{
    private static readonly string[] t_desktopKeywords = ["windows", "desktop", "laptop", "pc", "mac", "imac", "macbook"];
    private static readonly string[] t_phoneKeywords = ["iphone", "ipad", "ios", "android", "samsung", "pixel", "phone"];

    public static DeviceKind Classify(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return DeviceKind.Unknown;
        }

        var span = deviceName.AsSpan();

        return ContainsAny(span, t_desktopKeywords)
            ? DeviceKind.Desktop
            : ContainsAny(span, t_phoneKeywords)
            ? DeviceKind.Phone
            : DeviceKind.Unknown;
    }

    private static bool ContainsAny(ReadOnlySpan<char> source, string[] keywords)
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
