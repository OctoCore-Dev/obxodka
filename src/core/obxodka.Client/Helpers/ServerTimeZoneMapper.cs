namespace obxodka.Helpers;

//сопоставление страны и локации vpn-сервера с системным и iana часовым поясом
public sealed record ServerTimeZoneInfo(
    string WindowsId,
    string IanaId,
    string CountryCode,
    string DisplayRegion);

//маппер локации активного сервера vpn в системные идентификаторы часовых поясов
public static class ServerTimeZoneMapper
{
    private static readonly ServerTimeZoneInfo s_sweden = new("W. Europe Standard Time", "Europe/Stockholm", "SE", "Швеция");
    private static readonly ServerTimeZoneInfo s_germany = new("W. Europe Standard Time", "Europe/Berlin", "DE", "Германия");
    private static readonly ServerTimeZoneInfo s_netherlands = new("W. Europe Standard Time", "Europe/Amsterdam", "NL", "Нидерланды");
    private static readonly ServerTimeZoneInfo s_finland = new("FLE Standard Time", "Europe/Helsinki", "FI", "Финляндия");
    private static readonly ServerTimeZoneInfo s_usEast = new("Eastern Standard Time", "America/New_York", "US", "США (Восток)");
    private static readonly ServerTimeZoneInfo s_usCentral = new("Central Standard Time", "America/Chicago", "US", "США (Центр)");
    private static readonly ServerTimeZoneInfo s_usWest = new("Pacific Standard Time", "America/Los_Angeles", "US", "США (Запад)");
    private static readonly ServerTimeZoneInfo s_uk = new("GMT Standard Time", "Europe/London", "GB", "Великобритания");
    private static readonly ServerTimeZoneInfo s_france = new("Romance Standard Time", "Europe/Paris", "FR", "Франция");
    private static readonly ServerTimeZoneInfo s_poland = new("Central European Standard Time", "Europe/Warsaw", "PL", "Польша");
    private static readonly ServerTimeZoneInfo s_switzerland = new("W. Europe Standard Time", "Europe/Zurich", "CH", "Швейцария");
    private static readonly ServerTimeZoneInfo s_austria = new("W. Europe Standard Time", "Europe/Vienna", "AT", "Австрия");
    private static readonly ServerTimeZoneInfo s_czechia = new("Central Europe Standard Time", "Europe/Prague", "CZ", "Чехия");
    private static readonly ServerTimeZoneInfo s_spain = new("Romance Standard Time", "Europe/Madrid", "ES", "Испания");
    private static readonly ServerTimeZoneInfo s_italy = new("W. Europe Standard Time", "Europe/Rome", "IT", "Италия");
    private static readonly ServerTimeZoneInfo s_norway = new("W. Europe Standard Time", "Europe/Oslo", "NO", "Норвегия");
    private static readonly ServerTimeZoneInfo s_denmark = new("Romance Standard Time", "Europe/Copenhagen", "DK", "Дания");
    private static readonly ServerTimeZoneInfo s_estonia = new("FLE Standard Time", "Europe/Tallinn", "EE", "Эстония");
    private static readonly ServerTimeZoneInfo s_latvia = new("FLE Standard Time", "Europe/Riga", "LV", "Латвия");
    private static readonly ServerTimeZoneInfo s_lithuania = new("FLE Standard Time", "Europe/Vilnius", "LT", "Литва");
    private static readonly ServerTimeZoneInfo s_turkey = new("Turkey Standard Time", "Europe/Istanbul", "TR", "Турция");
    private static readonly ServerTimeZoneInfo s_singapore = new("Singapore Standard Time", "Asia/Singapore", "SG", "Сингапур");
    private static readonly ServerTimeZoneInfo s_japan = new("Tokyo Standard Time", "Asia/Tokyo", "JP", "Япония");
    private static readonly ServerTimeZoneInfo s_canada = new("Eastern Standard Time", "America/Toronto", "CA", "Канада");
    private static readonly ServerTimeZoneInfo s_uae = new("Arabian Standard Time", "Asia/Dubai", "AE", "ОАЭ");

    private static readonly (string Keyword, ServerTimeZoneInfo Info)[] s_keywords =
    [
        //сначала более специфичные города сша, чтобы запад/центр не попадали в восток
        ("los angeles", s_usWest), ("лос-анджелес", s_usWest), ("seattle", s_usWest), ("сиэтл", s_usWest),
        ("san jose", s_usWest), ("silicon valley", s_usWest), ("california", s_usWest),
        ("dallas", s_usCentral), ("даллас", s_usCentral), ("chicago", s_usCentral), ("чикаго", s_usCentral),
        ("new york", s_usEast), ("нью-йорк", s_usEast), ("new jersey", s_usEast), ("ashburn", s_usEast),
        ("miami", s_usEast), ("майами", s_usEast), ("atlanta", s_usEast), ("virginia", s_usEast),
        ("united states", s_usEast), ("сша", s_usEast), ("америка", s_usEast),
        //европа
        ("stockholm", s_sweden), ("стокгольм", s_sweden), ("sweden", s_sweden), ("швеция", s_sweden),
        ("frankfurt", s_germany), ("франкфурт", s_germany), ("berlin", s_germany), ("берлин", s_germany),
        ("munich", s_germany), ("мюнхен", s_germany), ("falkenstein", s_germany), ("nuremberg", s_germany),
        ("germany", s_germany), ("германия", s_germany),
        ("amsterdam", s_netherlands), ("амстердам", s_netherlands), ("netherlands", s_netherlands),
        ("нидерланды", s_netherlands), ("голландия", s_netherlands), ("holland", s_netherlands),
        ("helsinki", s_finland), ("хельсинки", s_finland), ("finland", s_finland), ("финляндия", s_finland),
        ("london", s_uk), ("лондон", s_uk), ("united kingdom", s_uk), ("великобритания", s_uk), ("англия", s_uk),
        ("paris", s_france), ("париж", s_france), ("france", s_france), ("франция", s_france),
        ("warsaw", s_poland), ("варшава", s_poland), ("poland", s_poland), ("польша", s_poland),
        ("zurich", s_switzerland), ("цюрих", s_switzerland), ("switzerland", s_switzerland), ("швейцария", s_switzerland),
        ("vienna", s_austria), ("вена", s_austria), ("austria", s_austria), ("австрия", s_austria),
        ("prague", s_czechia), ("прага", s_czechia), ("czech", s_czechia), ("чехия", s_czechia),
        ("madrid", s_spain), ("мадрид", s_spain), ("spain", s_spain), ("испания", s_spain),
        ("milan", s_italy), ("милан", s_italy), ("rome", s_italy), ("italy", s_italy), ("италия", s_italy),
        ("oslo", s_norway), ("осло", s_norway), ("norway", s_norway), ("норвегия", s_norway),
        ("copenhagen", s_denmark), ("копенгаген", s_denmark), ("denmark", s_denmark), ("дания", s_denmark),
        ("tallinn", s_estonia), ("таллин", s_estonia), ("estonia", s_estonia), ("эстония", s_estonia),
        ("riga", s_latvia), ("рига", s_latvia), ("latvia", s_latvia), ("латвия", s_latvia),
        ("vilnius", s_lithuania), ("вильнюс", s_lithuania), ("lithuania", s_lithuania), ("литва", s_lithuania),
        ("istanbul", s_turkey), ("стамбул", s_turkey), ("turkey", s_turkey), ("турция", s_turkey),
        //азия, канада, ближний восток
        ("singapore", s_singapore), ("сингапур", s_singapore),
        ("tokyo", s_japan), ("токио", s_japan), ("osaka", s_japan), ("japan", s_japan), ("япония", s_japan),
        ("toronto", s_canada), ("торонто", s_canada), ("montreal", s_canada), ("canada", s_canada), ("канада", s_canada),
        ("dubai", s_uae), ("дубай", s_uae), ("оаэ", s_uae)
    ];

    private static readonly FrozenDictionary<string, ServerTimeZoneInfo> s_isoCodes = new Dictionary<string, ServerTimeZoneInfo>(StringComparer.OrdinalIgnoreCase)
    {
        ["SE"] = s_sweden,
        ["DE"] = s_germany,
        ["NL"] = s_netherlands,
        ["FI"] = s_finland,
        ["US"] = s_usEast,
        ["USA"] = s_usEast,
        ["GB"] = s_uk,
        ["UK"] = s_uk,
        ["FR"] = s_france,
        ["PL"] = s_poland,
        ["CH"] = s_switzerland,
        ["AT"] = s_austria,
        ["CZ"] = s_czechia,
        ["ES"] = s_spain,
        ["IT"] = s_italy,
        ["NO"] = s_norway,
        ["DK"] = s_denmark,
        ["EE"] = s_estonia,
        ["LV"] = s_latvia,
        ["LT"] = s_lithuania,
        ["TR"] = s_turkey,
        ["SG"] = s_singapore,
        ["JP"] = s_japan,
        ["CA"] = s_canada,
        ["AE"] = s_uae,
        ["UAE"] = s_uae
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly char[] s_tokenSeparators = [' ', ',', '.', '-', '_', '(', ')', '[', ']', '/', '|', ':', ';'];

    //определение целевого часового пояса по названию локации и флагу сервера
    public static bool TryResolve(string? location, out ServerTimeZoneInfo result, string? flagEmoji = null)
    {
        if (!string.IsNullOrWhiteSpace(location))
        {
            var normalized = location.Trim().ToLowerInvariant();
            foreach (var (keyword, info) in s_keywords)
            {
                if (normalized.Contains(keyword, StringComparison.Ordinal))
                {
                    result = info;
                    return true;
                }
            }

            //проверка кодов стран только как отдельных токенов (исключает ложные срабатывания вроде server -> se)
            var tokens = location.Split(s_tokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var token in tokens)
            {
                if (token.Length is >= 2 and <= 3 && s_isoCodes.TryGetValue(token, out var codeInfo))
                {
                    result = codeInfo;
                    return true;
                }
            }

            if (TryResolveFromFlag(location, out result))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(flagEmoji) && TryResolveFromFlag(flagEmoji, out result))
        {
            return true;
        }

        result = null!;
        return false;
    }

    //извлечение двухбуквенного iso-кода страны из эмодзи флага (regional indicator symbols)
    private static bool TryResolveFromFlag(string text, out ServerTimeZoneInfo result)
    {
        Span<char> iso = stackalloc char[2];
        var found = 0;

        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is >= 0x1F1E6 and <= 0x1F1FF)
            {
                iso[found++] = (char)('A' + (rune.Value - 0x1F1E6));
                if (found == 2)
                {
                    var code = new string(iso);
                    if (s_isoCodes.TryGetValue(code, out var info))
                    {
                        result = info;
                        return true;
                    }

                    found = 0;
                }
            }
            else
            {
                found = 0;
            }
        }

        result = null!;
        return false;
    }
}
