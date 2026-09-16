namespace obxodka.Helpers;

public static class FormatHelper
{
    private static readonly string[] t_suffixes = ["B", "KB", "MB", "GB", "TB"];

    public static string FormatBytes(double bytes)
    {
        int i;
        var d = bytes;
        for (i = 0; i < t_suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            d = bytes / 1024.0;
        }

        return $"{d:0.##} {t_suffixes[i]}";
    }
}
