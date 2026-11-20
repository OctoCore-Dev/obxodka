namespace obxodka.Converters;

public sealed class DeviceIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var kind = DeviceClassifier.Classify(value as string);
        return kind != DeviceKind.Desktop
            ? kind == DeviceKind.Phone
                ? FluentIcons.Phone24
                : FluentIcons.PhoneDesktop24
            : FluentIcons.Desktop24;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => null;
}
