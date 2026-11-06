namespace obxodka.Helpers;

//форматирование байтов трафика для графиков и интерфейса
public static class FormatHelper
{
    private static readonly string[] s_suffixes = ["B", "KB", "MB", "GB", "TB"];

    //перевод сырых байтов в читаемый вид мегабайты гигабайты
    public static string FormatBytes(double bytes)
    {
        if (bytes <= 0 || double.IsNaN(bytes) || double.IsInfinity(bytes))
        {
            return "0 B";
        }

        int i;
        var d = bytes;
        for (i = 0; i < s_suffixes.Length - 1 && bytes >= 1024; i++, bytes /= 1024)
        {
            d = bytes / 1024.0;
        }

        return $"{d:0.##} {s_suffixes[i]}";
    }
}
