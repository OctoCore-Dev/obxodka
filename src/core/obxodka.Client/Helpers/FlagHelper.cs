namespace obxodka.Helpers;

//преобразование unicode эмодзи флагов в iso коды и url для cdn
public static class FlagHelper
{
    //преобразование эмодзи флага в двухбуквенный код страны
    public static string? FlagEmojiToCountryCode(string? emoji)
    {
        if (string.IsNullOrWhiteSpace(emoji))
        {
            return null;
        }

        var trimmed = emoji.Trim();
        if (trimmed.Length == 2 && char.IsLetter(trimmed[0]) && char.IsLetter(trimmed[1]))
        {
            return trimmed.ToLowerInvariant();
        }

        if (trimmed.Length < 4 || !char.IsSurrogatePair(trimmed, 0) || !char.IsSurrogatePair(trimmed, 2))
        {
            return null;
        }

        var cp1 = char.ConvertToUtf32(trimmed, 0);
        var cp2 = char.ConvertToUtf32(trimmed, 2);

        if (cp1 is >= 0x1F1E6 and <= 0x1F1FF && cp2 is >= 0x1F1E6 and <= 0x1F1FF)
        {
            var c1 = (char)('a' + (cp1 - 0x1F1E6));
            var c2 = (char)('a' + (cp2 - 0x1F1E6));
            return $"{c1}{c2}";
        }

        return null;
    }

    //генерация ссылки на флаг страны на cdn
    public static string? GetFlagImageUrl(string? emoji)
    {
        var code = FlagEmojiToCountryCode(emoji);
        return !string.IsNullOrEmpty(code)
            ? $"https://flagcdn.com/w80/{code}.png"
            : null;
    }
}
