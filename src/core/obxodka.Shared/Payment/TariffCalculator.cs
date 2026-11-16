namespace obxodka.Models;

//результат расчета часов и бонусов по тарифу
public readonly record struct TariffBreakdown(
    int TotalHours,
    int BaseHours,
    int BonusHours,
    int BonusPercent,
    decimal EffectiveRubAmount);

//единый источник истины для валют, лимитов, методов оплаты и расчета часов
public static class TariffCalculator
{
    public const string MethodCard = "CARD";
    public const string MethodSbp = "SBP";
    public const string MethodPayPal = "PAYPAL";
    public const string MethodApplePay = "APPLE_PAY";

    //нормализует код валюты и проверяет поддержку
    public static string? NormalizeCurrency(string? currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
        {
            return "RUB";
        }

        var normalized = currency.Trim().ToUpperInvariant();
        return normalized is "RUB" or "USD" or "EUR" ? normalized : null;
    }

    //нормализует способ оплаты и проверяет совместимость с валютой
    public static string? NormalizePaymentMethod(string? method, string currency)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            return MethodCard;
        }

        var normalized = method.Trim().ToUpperInvariant().Replace('-', '_');
        if (normalized == "APPLEPAY")
        {
            normalized = MethodApplePay;
        }

        return currency switch
        {
            "RUB" => normalized is MethodCard or MethodSbp ? normalized : null,
            "USD" or "EUR" => normalized is MethodCard or MethodPayPal or MethodApplePay ? normalized : null,
            _ => null
        };
    }

    //проверяет допустимые границы суммы для выбранной валюты
    public static bool IsValidAmount(decimal amount, string currency) => currency switch
    {
        "RUB" => amount is >= 50m and <= 50000m,
        "USD" or "EUR" => amount is >= 5m and <= 10000m,
        _ => false
    };

    //возвращает отображаемый символ валюты
    public static string GetCurrencySymbol(string? currency) => currency?.Trim().ToUpperInvariant() switch
    {
        "USD" => "$",
        "EUR" => "€",
        _ => "₽"
    };

    //рассчитывает итоговые часы, базу и бонус по единой сетке
    public static TariffBreakdown Calculate(decimal amount, string? currency = "RUB")
    {
        if (amount <= 0m)
        {
            return default;
        }

        var normalizedCurrency = NormalizeCurrency(currency) ?? "RUB";
        var effectiveRub = normalizedCurrency is "USD" or "EUR" ? amount * 100m : amount;
        var baseHours = Math.Max(0, (int)Math.Round(effectiveRub, MidpointRounding.AwayFromZero));

        var totalHours = (int)effectiveRub switch
        {
            99 or 100 => 100,
            249 or 250 => 260,
            300 => 350,
            500 => 750,
            1000 => 2200,
            2000 => 4700,
            5000 => 12000,
            >= 5000 => (int)(effectiveRub * 2.4m),
            >= 2000 => (int)(effectiveRub * 2.35m),
            >= 1000 => (int)(effectiveRub * 2.2m),
            >= 500 => (int)(effectiveRub * 1.5m),
            >= 300 => (int)(effectiveRub * 1.17m),
            _ => (int)effectiveRub
        };

        var bonusHours = Math.Max(0, totalHours - baseHours);
        var bonusPercent = baseHours > 0
            ? (int)Math.Round(bonusHours * 100m / baseHours, MidpointRounding.AwayFromZero)
            : 0;

        return new TariffBreakdown(totalHours, baseHours, bonusHours, bonusPercent, effectiveRub);
    }
}
