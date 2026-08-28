namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class ConvertersTests
{
    private readonly DeviceIconConverter _converter = new();

    [Theory]
    [InlineData("Windows 11 PC", FluentIcons.Desktop24)]
    [InlineData("My Gaming Desktop", FluentIcons.Desktop24)]
    [InlineData("MacBook Pro M2", FluentIcons.Desktop24)]
    [InlineData("Samsung Galaxy S24", FluentIcons.Phone24)]
    [InlineData("iPhone 15 Pro", FluentIcons.Phone24)]
    [InlineData("Google Pixel 8", FluentIcons.Phone24)]
    [InlineData("Custom Device", FluentIcons.PhoneDesktop24)]
    [InlineData("", FluentIcons.PhoneDesktop24)]
    [InlineData(null, FluentIcons.PhoneDesktop24)]
    public void DeviceIconConverterReturnsCorrectIcon(string? deviceName, FluentIcons expectedIcon)
    {
        var result = _converter.Convert(deviceName, typeof(FluentIcons), null, CultureInfo.InvariantCulture);
        Assert.Equal(expectedIcon, result);
    }

    [Fact]
    public void ConvertBackReturnsNull()
    {
        var result = _converter.ConvertBack(FluentIcons.Desktop24, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Null(result);
    }
}
