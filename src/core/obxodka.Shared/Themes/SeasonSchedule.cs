namespace obxodka.Shared.Themes;

public static class SeasonSchedule
{
    public enum Season
    {
        Standard,
        Halloween,
        NewYear
    }

    public static bool IsHalloweenSeason(DateTime? targetDate = null)
    {
        var now = targetDate ?? DateTime.UtcNow;
        return (now.Month == 10 && now.Day >= 15) ||
               (now.Month == 11 && now.Day <= 5);
    }

    public static bool IsNewYearSeason(DateTime? targetDate = null)
    {
        var now = targetDate ?? DateTime.UtcNow;
        return (now.Month == 12 && now.Day >= 15) ||
               (now.Month == 1 && now.Day <= 10);
    }

    public static Season GetCurrentSeason(DateTime? targetDate = null)
    {
        return IsHalloweenSeason(targetDate)
            ? Season.Halloween
            : IsNewYearSeason(targetDate)
                ? Season.NewYear
                : Season.Standard;
    }

    public static string GetSeasonalThemeId(DateTime? targetDate = null) => GetCurrentSeason(targetDate) switch
    {
        Season.Halloween => "halloween",
        Season.NewYear => "new_year",
        Season.Standard => "default",
        _ => "default"
    };
}
