namespace obxodka.Converters;

public sealed class DeviceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return DeviceClassifier.Classify(value as string) switch
        {
            DeviceKind.Desktop => FluentIcons.Desktop24,
            DeviceKind.Phone => FluentIcons.Phone24,
            DeviceKind.Unknown => FluentIcons.PhoneDesktop24,
            _ => FluentIcons.PhoneDesktop24
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
