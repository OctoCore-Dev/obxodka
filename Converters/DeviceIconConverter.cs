namespace obxodka.Converters;

public sealed class DeviceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string name || string.IsNullOrWhiteSpace(name))
        {
            return FluentIcons.PhoneDesktop24;
        }

        var span = name.AsSpan();

        return ContainsAny(span, ["windows", "desktop", "laptop", "pc", "mac", "imac", "macbook"])
            ? FluentIcons.Desktop24
            : ContainsAny(span, ["iphone", "ipad", "ios", "android", "samsung", "pixel", "phone"])
            ? FluentIcons.Phone24
            : FluentIcons.PhoneDesktop24;
    }

    private static bool ContainsAny(ReadOnlySpan<char> source, params ReadOnlySpan<string> keywords)
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

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
