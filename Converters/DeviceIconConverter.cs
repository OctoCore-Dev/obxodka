namespace obxodka.Converters;

public class DeviceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = (value as string)?.ToLowerInvariant() ?? "";

        return name.Contains("windows") || name.Contains("desktop") || name.Contains("laptop") || name.Contains("pc")
            ? FluentIcons.Desktop24
            : name.Contains("mac") || name.Contains("imac") || name.Contains("macbook")
            ? FluentIcons.Desktop24
            : name.Contains("iphone") || name.Contains("ipad") || name.Contains("ios")
            ? FluentIcons.Phone24
            : name.Contains("android") || name.Contains("samsung") || name.Contains("pixel")
            ? FluentIcons.Phone24
            : FluentIcons.PhoneDesktop24;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
