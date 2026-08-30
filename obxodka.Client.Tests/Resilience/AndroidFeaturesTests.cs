namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public class AndroidFeaturesTests
{
    [Theory]
    [InlineData("com.android.chrome", true)]
    [InlineData("ru.sberbankmobile", true)]
    [InlineData("com.google.android.youtube", true)]
    [InlineData("", false)]
    [InlineData("invalid package with spaces", false)]
    public void ValidateSplitTunnelingPackageNames(string packageName, bool isValid)
    {
        var valid = !string.IsNullOrWhiteSpace(packageName) &&
                    !packageName.Contains(' ') &&
                    packageName.Contains('.');
        Assert.Equal(isValid, valid);
    }

    [Fact]
    public void GooglePurchasePayloadConstruction()
    {
        var req = new GooglePurchaseVerifyRequest(
            ProductId: "sub_premium_1month",
            PurchaseToken: "inapp_billing_google_token_sample_12345",
            OrderId: "GPA.1234-5678-9012-34567"
        );

        Assert.Equal("sub_premium_1month", req.ProductId);
        Assert.Equal("inapp_billing_google_token_sample_12345", req.PurchaseToken);
        Assert.StartsWith("GPA.", req.OrderId);
    }

    [Fact]
    public void GooglePurchaseResponseBalanceCalculation()
    {
        var resp = new GooglePurchaseVerifyResponse(
            Success: true,
            SecondsAdded: 2592000,
            NewBalance: 100,
            Message: "Подписка активирована"
        );

        Assert.True(resp.Success);
        Assert.Equal(2592000, resp.SecondsAdded);
        Assert.Equal(TimeSpan.FromDays(30).TotalSeconds, resp.SecondsAdded);
    }

    [Theory]
    [InlineData(1420, 1420)]
    [InlineData(1500, 1420)]
    [InlineData(1280, 1280)]
    [InlineData(1000, 1280)]
    public void SafeMobileMtuClamping(int rawMtu, int expectedSafeMtu)
    {
        var clamped = Math.Clamp(rawMtu, 1280, 1420);
        Assert.Equal(expectedSafeMtu, clamped);
    }

    [Fact]
    public void TrafficStatsSpeedCalculations()
    {
        var stats = new AppTrafficStats
        {
            DownloadSpeedBps = 10485760,
            UploadSpeedBps = 5242880
        };

        Assert.Equal(10485760, stats.DownloadSpeedBps);
        Assert.Equal(5242880, stats.UploadSpeedBps);
    }
}
