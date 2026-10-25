namespace obxodka.Helpers;

public static class TimeFormatHelper
{
    public static string FormatSeconds(long seconds, bool verbose = true)
    {
        if (seconds <= 0)
        {
            return verbose ? "0ч 00м 00с" : "00:00";
        }

        var ts = TimeSpan.FromSeconds(seconds);
        return verbose
            ? ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}ч {ts.Minutes:D2}м {ts.Seconds:D2}с"
                : $"{ts.Minutes:D2}м {ts.Seconds:D2}с"
            : ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
