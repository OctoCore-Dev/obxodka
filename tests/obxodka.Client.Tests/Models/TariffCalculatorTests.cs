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
}
