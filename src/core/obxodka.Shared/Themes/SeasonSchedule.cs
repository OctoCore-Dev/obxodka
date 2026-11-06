namespace obxodka.Shared.Themes;

//календарное расписание сезонных тем оформления
public static class SeasonSchedule
{
    //сезонные периоды приложения
    public enum Season
    {
        Standard,
        Halloween,
        NewYear
    }

    //проверка попадания даты в сезон хэллоуина
    public static bool IsHalloweenSeason(DateTime? targetDate = null)
    {
        var now = targetDate ?? DateTime.UtcNow;
        return (now.Month == 10 && now.Day >= 15) ||
                (now.Month == 11 && now.Day <= 5);
    }

    //проверка попадания даты в новогодний сезон
    public static bool IsNewYearSeason(DateTime? targetDate = null)
    {
        var now = targetDate ?? DateTime.UtcNow;
        return (now.Month == 12 && now.Day >= 15) ||
                (now.Month == 1 && now.Day <= 10);
    }

    //определение текущего активного сезона
    public static Season GetCurrentSeason(DateTime? targetDate = null)
    {
        return IsHalloweenSeason(targetDate)
            ? Season.Halloween
            : IsNewYearSeason(targetDate)
                ? Season.NewYear
                : Season.Standard;
    }

    //получение идентификатора сезонной темы
    public static string GetSeasonalThemeId(DateTime? targetDate = null) => GetCurrentSeason(targetDate) switch
    {
        Season.Halloween => "halloween",
        Season.NewYear => "new_year",
        Season.Standard => "default",
        _ => "default"
    };
}
