using System.Text;

namespace obxodka.Platforms.Windows;

//парсинг ярлыков windows shell link (.lnk) для извлечения путей
public sealed partial class AppManager
{
    //быстрое эвристическое извлечение целевого exe из структуры .lnk файла
    private static bool TryExtractExeFromLnk(string lnkPath, out string exePath)
    {
        exePath = string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            if (bytes.Length < 76 || bytes[0] != 0x4C)
            {
                return false;
            }

            var asciiText = Encoding.Default.GetString(bytes);
            var matches = FindDriveExePaths(asciiText);
            if (matches.Count > 0)
            {
                exePath = matches[^1];
                return true;
            }

            var utf16Text = Encoding.Unicode.GetString(bytes);
            matches = FindDriveExePaths(utf16Text);
            if (matches.Count > 0)
            {
                exePath = matches[^1];
                return true;
            }
        }
        catch { }

        return false;
    }

    //поиск путей с буквами дисков и расширением .exe в текстовом представлении
    private static List<string> FindDriveExePaths(string rawText)
    {
        var results = new List<string>();
        var idx = 0;
        while (idx + 6 < rawText.Length)
        {
            var colonSlash = rawText.IndexOf(@":\", idx, StringComparison.Ordinal);
            if (colonSlash <= 0)
            {
                break;
            }

            var driveChar = rawText[colonSlash - 1];
            if (!char.IsAsciiLetter(driveChar))
            {
                idx = colonSlash + 2;
                continue;
            }

            var start = colonSlash - 1;
            var end = start;
            while (end < rawText.Length && rawText[end] >= 32 && rawText[end] != '"' && rawText[end] != '<' && rawText[end] != '>' && rawText[end] != '|')
            {
                end++;
            }

            var candidate = rawText[start..end];
            var exePos = candidate.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exePos > 0)
            {
                var fullCandidate = candidate[..(exePos + 4)];
                if (!fullCandidate.Contains("Windows\\Installer", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullCandidate))
                {
                    results.Add(fullCandidate);
                }
            }

            idx = colonSlash + 2;
        }

        return results;
    }
}
