namespace obxodka.Client.Tests;

[Trait("Category", "Unit")]
public sealed class TariffCalculatorTests
{
    [Theory]
    [InlineData(5, "USD", 750, 500, 250, 50)]
    [InlineData(10, "USD", 2200, 1000, 1200, 120)]
    [InlineData(15, "USD", 3300, 1500, 1800, 120)]
    [InlineData(20, "EUR", 4700, 2000, 2700, 135)]
    [InlineData(50, "EUR", 12000, 5000, 7000, 140)]
    [InlineData(99, "RUB", 100, 99, 1, 1)]
    [InlineData(300, "RUB", 350, 300, 50, 17)]
    [InlineData(500, "RUB", 750, 500, 250, 50)]
    [InlineData(1000, "RUB", 2200, 1000, 1200, 120)]
    public void Calculate_ReturnsUnifiedServerClientBreakdown(
        decimal amount,
        string currency,
        int expectedTotal,
        int expectedBase,
        int expectedBonus,
        int expectedPercent)
    {
        var result = TariffCalculator.Calculate(amount, currency);

        Assert.Equal(expectedTotal, result.TotalHours);
        Assert.Equal(expectedBase, result.BaseHours);
        Assert.Equal(expectedBonus, result.BonusHours);
        Assert.Equal(expectedPercent, result.BonusPercent);
    }

    [Theory]
    [InlineData("sbp", "RUB", TariffCalculator.MethodSbp)]
    [InlineData("CARD", "RUB", TariffCalculator.MethodCard)]
    [InlineData("paypal", "USD", TariffCalculator.MethodPayPal)]
    [InlineData("applepay", "EUR", TariffCalculator.MethodApplePay)]
    [InlineData("sbp", "USD", null)]
    [InlineData("paypal", "RUB", null)]
    public void NormalizePaymentMethod_EnforcesCurrencyCompatibility(
        string method,
        string currency,
        string? expected)
    {
        var actual = TariffCalculator.NormalizePaymentMethod(method, currency);
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(TariffCalculator.GooglePlaySku100, 100, 99, 0, false)]
    [InlineData(TariffCalculator.GooglePlaySku250, 260, 250, 4, false)]
    [InlineData(TariffCalculator.GooglePlaySku300, 350, 300, 17, true)]
    [InlineData(TariffCalculator.GooglePlaySku500, 750, 500, 50, true)]
    [InlineData(TariffCalculator.GooglePlaySku1000, 2200, 1000, 120, true)]
    [InlineData(TariffCalculator.GooglePlaySku2000, 4700, 2000, 135, false)]
    [InlineData(TariffCalculator.GooglePlaySku5000, 12000, 5000, 140, false)]
    public void TryGetGooglePlayProduct_ResolvesCompleteCatalog(
        string productId,
        int expectedHours,
        decimal expectedRubAmount,
        int expectedBonusPercent,
        bool expectedBaseTier)
    {
        var found = TariffCalculator.TryGetGooglePlayProduct(productId, out var spec);

        Assert.True(found);
        Assert.Equal(productId, spec.ProductId);
        Assert.Equal(expectedHours, spec.Hours);
        Assert.Equal(expectedRubAmount, spec.ReferenceRubAmount);
        Assert.Equal(expectedBonusPercent, spec.BonusPercent);
        Assert.Equal(expectedBaseTier, spec.IsBaseTier);
    }

    [Fact]
    public void TryGetGooglePlayProduct_ReturnsFalseForUnknownSku()
    {
        var found = TariffCalculator.TryGetGooglePlayProduct("obxodka_unknown_tier", out _);
        Assert.False(found);
        Assert.Equal(7, TariffCalculator.GetGooglePlayProductIds().Length);
    }
}
