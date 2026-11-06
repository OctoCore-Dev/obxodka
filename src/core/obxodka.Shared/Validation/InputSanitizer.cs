using System.Text;
using System.Text.RegularExpressions;

namespace obxodka.Shared.Validation;

//валидатор и санитайзер пользовательского ввода и байтов изображений
public static partial class InputSanitizer
{
    private static readonly string[] s_dangerousTokens =
    [
        "<script",
        "</script",
        "<iframe",
        "<object",
        "<embed",
        "<svg",
        "<math",
        "<link",
        "<meta",
        "<base",
        "<form",
        "javascript:",
        "vbscript:",
        "data:text/html",
        "data:image/svg",
        "onerror=",
        "onload=",
        "onclick=",
        "onmouseover=",
        "onfocus=",
        "onanimationstart=",
        "ontoggle=",
        "document.cookie",
        "window.location",
        "eval("
    ];

    private static readonly string[] s_polyglotSignatures =
    [
        "<script",
        "<html",
        "<svg",
        "<iframe",
        "<?php",
        "javascript:",
        "onerror=",
        "onload="
    ];

    [GeneratedRegex(@"<[^>]*>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"(\r?\n){4,}", RegexOptions.CultureInvariant)]
    private static partial Regex ExcessiveNewlinesRegex();

    //проверяет наличие потенциально опасных xss токенов
    public static bool ContainsDangerousPayload(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        foreach (var token in s_dangerousTokens)
        {
            if (input.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    //очищает текст от html тегов и опасных символов с ограничением длины
    public static string SanitizeText(string? input, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(input) || maxLength <= 0)
        {
            return string.Empty;
        }

        var cleaned = HtmlTagRegex().Replace(input, string.Empty);

        foreach (var token in s_dangerousTokens)
        {
            cleaned = cleaned.Replace(token, string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        var sb = new StringBuilder(Math.Min(cleaned.Length, maxLength));
        foreach (var ch in cleaned)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                _ = sb.Append(ch);
                continue;
            }

            if (char.IsControl(ch))
            {
                continue;
            }

            if (ch is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or '<' or '>')
            {
                continue;
            }

            _ = sb.Append(ch);
        }

        var result = ExcessiveNewlinesRegex().Replace(sb.ToString(), "\n\n\n").Trim();
        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd();
        }

        return result;
    }

    //проверяет сигнатуру файла и отсутствие полиглот шелл скриптов
    public static bool ValidateImageBytes(ReadOnlySpan<byte> fileBytes, string extension)
    {
        if (fileBytes.Length < 12)
        {
            return false;
        }

        var ext = extension.ToLowerInvariant();
        var validHeader = ext switch
        {
            ".png" => fileBytes[0] == 0x89 &&
                      fileBytes[1] == 0x50 &&
                      fileBytes[2] == 0x4E &&
                      fileBytes[3] == 0x47 &&
                      fileBytes[4] == 0x0D &&
                      fileBytes[5] == 0x0A &&
                      fileBytes[6] == 0x1A &&
                      fileBytes[7] == 0x0A,
            ".jpg" or ".jpeg" => fileBytes[0] == 0xFF &&
                                 fileBytes[1] == 0xD8 &&
                                 fileBytes[2] == 0xFF,
            ".webp" => fileBytes[0] == 0x52 &&
                       fileBytes[1] == 0x49 &&
                       fileBytes[2] == 0x46 &&
                       fileBytes[3] == 0x46 &&
                       fileBytes[8] == 0x57 &&
                       fileBytes[9] == 0x45 &&
                       fileBytes[10] == 0x42 &&
                       fileBytes[11] == 0x50,
            _ => false
        };

        if (!validHeader)
        {
            return false;
        }

        var sampleLen = Math.Min(fileBytes.Length, 16384);
        var asciiSample = Encoding.Latin1.GetString(fileBytes[..sampleLen]);
        foreach (var sig in s_polyglotSignatures)
        {
            if (asciiSample.Contains(sig, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
