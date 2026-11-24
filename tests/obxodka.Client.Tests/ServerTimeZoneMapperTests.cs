namespace obxodka.Client.Tests;

//тесты сопоставления локаций и флагов vpn-серверов с системными часовыми поясами
public sealed class ServerTimeZoneMapperTests
{
    [Theory]
    [InlineData("Швеция, Стокгольм", "W. Europe Standard Time", "Europe/Stockholm", "SE")]
    [InlineData("Sweden, Stockholm", "W. Europe Standard Time", "Europe/Stockholm", "SE")]
    [InlineData("Германия, Франкфурт", "W. Europe Standard Time", "Europe/Berlin", "DE")]
    [InlineData("Germany • Frankfurt", "W. Europe Standard Time", "Europe/Berlin", "DE")]
    [InlineData("Нидерланды, Амстердам", "W. Europe Standard Time", "Europe/Amsterdam", "NL")]
    [InlineData("Finland, Helsinki", "FLE Standard Time", "Europe/Helsinki", "FI")]
    [InlineData("Финляндия (FI)", "FLE Standard Time", "Europe/Helsinki", "FI")]
    [InlineData("США, Нью-Йорк", "Eastern Standard Time", "America/New_York", "US")]
    [InlineData("USA, Los Angeles", "Pacific Standard Time", "America/Los_Angeles", "US")]
    [InlineData("US • Dallas", "Central Standard Time", "America/Chicago", "US")]
    [InlineData("Великобритания, Лондон", "GMT Standard Time", "Europe/London", "GB")]
    [InlineData("Япония, Токио", "Tokyo Standard Time", "Asia/Tokyo", "JP")]
    [InlineData("Сингапур", "Singapore Standard Time", "Asia/Singapore", "SG")]
    public void TryResolve_MapsKnownLocations_ToExpectedTimeZone(
        string location,
        string expectedWindowsId,
        string expectedIanaId,
        string expectedCode)
    {
        var ok = ServerTimeZoneMapper.TryResolve(location, out var info);

        Assert.True(ok);
        Assert.Equal(expectedWindowsId, info.WindowsId);
        Assert.Equal(expectedIanaId, info.IanaId);
        Assert.Equal(expectedCode, info.CountryCode);
    }

    [Theory]
    [InlineData("🇸🇪", "W. Europe Standard Time", "Europe/Stockholm")]
    [InlineData("🇩🇪", "W. Europe Standard Time", "Europe/Berlin")]
    [InlineData("🇳🇱", "W. Europe Standard Time", "Europe/Amsterdam")]
    [InlineData("🇫🇮", "FLE Standard Time", "Europe/Helsinki")]
    [InlineData("🇺🇸", "Eastern Standard Time", "America/New_York")]
    public void TryResolve_FallsBackToFlagEmoji_WhenLocationIsGeneric(
        string flag,
        string expectedWindowsId,
        string expectedIanaId)
    {
        var ok = ServerTimeZoneMapper.TryResolve("Cloud Node #1", out var info, flag);

        Assert.True(ok);
        Assert.Equal(expectedWindowsId, info.WindowsId);
        Assert.Equal(expectedIanaId, info.IanaId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Direct Server Node")]
    [InlineData("Inside Main Relay")]
    public void TryResolve_DoesNotFalseMatchSubstringsOrEmptyLocations(string? location)
    {
        var ok = ServerTimeZoneMapper.TryResolve(location, out _);
        Assert.False(ok);
    }
}
