using System.Collections.Frozen;
using System.Net.Mail;

namespace obxodka.Models;

//валидатор email адресов по стандарту rfc 5322 с белым списком провайдеров и эвристикой опечаток
public static class EmailValidator
{
    //популярные почтовые домены для умного выпадающего автодополнения при вводе '@'
    public static readonly string[] PopularDomains =
    [
        "gmail.com",
        "yandex.ru",
        "mail.ru",
        "bk.ru",
        "inbox.ru",
        "outlook.com",
        "icloud.com",
        "proton.me"
    ];

    //белый список проверенных почтовых доменов (все остальные домены отклоняются)
    public static readonly FrozenSet<string> AllowedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Google
        "gmail.com",
        "googlemail.com",

        // Яндекс
        "yandex.ru",
        "ya.ru",
        "yandex.com",
        "yandex.by",
        "yandex.kz",
        "narod.ru",

        // VK / Mail.ru
        "mail.ru",
        "bk.ru",
        "inbox.ru",
        "list.ru",
        "internet.ru",
        "vk.com",

        // Rambler
        "rambler.ru",
        "lenta.ru",
        "autorambler.ru",
        "myrambler.ru",
        "ro.ru",

        // Microsoft
        "outlook.com",
        "hotmail.com",
        "live.com",
        "msn.com",

        // Apple
        "icloud.com",
        "me.com",
        "mac.com",

        // Защищенные / Международные
        "proton.me",
        "protonmail.com",
        "yahoo.com",

        // Сервисный домен
        "obxodka.one",

        // Тестовые домены RFC 2606 и автоматических тестов
        "test.com",
        "example.com",
        "example.org",
        "example.net"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //строгая валидация формата email адреса с проверкой белого списка, кириллицы и опечаток
    public static EmailValidationResult Validate(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new EmailValidationResult(false, "Пожалуйста, введите Email адрес.");
        }

        var trimmed = email.Trim();
        if (trimmed.Length > 254)
        {
            return new EmailValidationResult(false, "Длина адреса превышает лимит (максимум 254 символа).");
        }

        if (trimmed.Contains(' '))
        {
            return new EmailValidationResult(false, "Email адрес не должен содержать пробелы.");
        }

        //проверка на наличие русских букв (случайно забытая раскладка клавиатуры)
        if (HasCyrillic(trimmed.AsSpan()))
        {
            return new EmailValidationResult(false, "Email адрес не должен содержать русские буквы. Переключите раскладку на английский язык.");
        }

        var atIndex = trimmed.IndexOf('@');
        if (atIndex < 0)
        {
            return new EmailValidationResult(false, "В адресе отсутствует символ «@».");
        }

        if (atIndex != trimmed.LastIndexOf('@'))
        {
            return new EmailValidationResult(false, "В адресе может быть только один символ «@».");
        }

        if (atIndex == 0)
        {
            return new EmailValidationResult(false, "Укажите имя ящика перед «@».");
        }

        if (atIndex == trimmed.Length - 1)
        {
            return new EmailValidationResult(false, "Укажите домен после «@» (например, gmail.com).");
        }

        var localPart = trimmed[..atIndex];
        var domainPart = trimmed[(atIndex + 1)..].ToLowerInvariant();

        if (localPart.Length > 64)
        {
            return new EmailValidationResult(false, "Имя ящика слишком длинное (максимум 64 символа).");
        }

        if (localPart.StartsWith('.') || localPart.EndsWith('.'))
        {
            return new EmailValidationResult(false, "Имя ящика не может начинаться или заканчиваться точкой.");
        }

        if (localPart.Contains(".."))
        {
            return new EmailValidationResult(false, "В имени ящика не могут идти две точки подряд.");
        }

        //проверка допустимых символов в имени ящика
        for (var i = 0; i < localPart.Length; i++)
        {
            if (!IsValidLocalPartChar(localPart[i]))
            {
                return new EmailValidationResult(false, "Имя ящика может содержать только латинские буквы (a-z), цифры и символы . _ - +");
            }
        }

        if (domainPart.Length > 255)
        {
            return new EmailValidationResult(false, "Имя домена слишком длинное.");
        }

        var lastDotIndex = domainPart.LastIndexOf('.');
        if (lastDotIndex < 0)
        {
            return new EmailValidationResult(false, "В домене отсутствует точка (например, .ru или .com).");
        }

        if (domainPart.StartsWith('.') || domainPart.EndsWith('.') || domainPart.StartsWith('-') || domainPart.EndsWith('-'))
        {
            return new EmailValidationResult(false, "Домен не может начинаться или заканчиваться точкой или дефисом.");
        }

        if (domainPart.Contains(".."))
        {
            return new EmailValidationResult(false, "В домене не могут идти две точки подряд.");
        }

        //проверка допустимых символов в домене
        for (var i = 0; i < domainPart.Length; i++)
        {
            var c = domainPart[i];
            if (!char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-')
            {
                return new EmailValidationResult(false, "Имя домена может содержать только латинские буквы, цифры, точку и дефис.");
            }
        }

        var tld = domainPart[(lastDotIndex + 1)..];
        if (tld.Length < 2)
        {
            return new EmailValidationResult(false, "Некорректная доменная зона (минимум 2 буквы, например .ru, .com).");
        }

        for (var i = 0; i < tld.Length; i++)
        {
            if (!char.IsAsciiLetter(tld[i]))
            {
                return new EmailValidationResult(false, "Доменная зона может состоять только из букв (например, .ru, .com).");
            }
        }

        //стандартная rfc проверка синтаксиса
        if (!MailAddress.TryCreate(trimmed, out var parsed) || parsed.Address != trimmed)
        {
            return new EmailValidationResult(false, "Недопустимый формат адреса электронной почты.");
        }

        //проверка по белому списку разрешенных почтовых доменов
        if (AllowedDomains.Contains(domainPart))
        {
            return new EmailValidationResult(true, null, null, trimmed.ToLowerInvariant());
        }

        //домен не входит в белый список — ищем опечатку среди разрешенных доменов
        var suggestedDomain = FindTypoDomainSuggestion(domainPart);
        var suggestedEmail = suggestedDomain != null ? $"{localPart}@{suggestedDomain}" : null;

        if (suggestedDomain != null)
        {
            var err = $"Домен «{domainPart}» не поддерживается или содержит ошибку. Возможно, вы имели в виду «{suggestedDomain}»?";
            return new EmailValidationResult(false, err, suggestedEmail, trimmed.ToLowerInvariant());
        }

        //домен вообще неизвестен и не похож ни на один разрешенный
        return new EmailValidationResult(
            false,
            $"Почтовый домен «{domainPart}» не поддерживается. Разрешены только проверенные почтовые сервисы (Gmail, Яндекс, Mail.ru, BK, Outlook, iCloud, Proton и др.).",
            null,
            trimmed.ToLowerInvariant());
    }

    //проверка наличия символов кириллицы (русских букв)
    private static bool HasCyrillic(ReadOnlySpan<char> text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is (>= '\u0400' and <= '\u04FF') or (>= '\u0500' and <= '\u052F'))
            {
                return true;
            }
        }
        return false;
    }

    //проверка допустимости символа в локальной части адреса
    private static bool IsValidLocalPartChar(char c) =>
        char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '+';

    //поиск опечатки среди белого списка проверенных сервисов
    public static string? FindTypoDomainSuggestion(string domain)
    {
        //прямые соответствия частых опечаток
        var direct = domain switch
        {
            "gmail.ru" => "gmail.com",
            "b.ru" => "bk.ru",
            "m.ru" => "mail.ru",
            "y.ru" => "ya.ru",
            "outlok.com" => "outlook.com",
            "inbx.ru" => "inbox.ru",
            "yadnex.ru" or "yandx.ru" => "yandex.ru",
            "mial.ru" or "mai.ru" or "mal.ru" => "mail.ru",
            "gamail.com" or "gmial.com" or "gamil.com" or "gmai.com" or "gmaill.com" => "gmail.com",
            "icoud.com" or "iclod.com" => "icloud.com",
            _ => null
        };

        if (direct != null)
        {
            return direct;
        }

        //проверка опечаток в доменных зонах (.con -> .com, .rub -> .ru)
        var lastDot = domain.LastIndexOf('.');
        if (lastDot > 0)
        {
            var tld = domain[(lastDot + 1)..];
            var secondLevel = domain[..lastDot];
            var candidateTld = tld switch
            {
                "con" or "cpm" or "cm" => $"{secondLevel}.com",
                "rub" or "ry" => $"{secondLevel}.ru",
                _ => null
            };

            if (candidateTld != null && AllowedDomains.Contains(candidateTld))
            {
                return candidateTld;
            }
        }

        //эвристический поиск по дамерау-левенштейну с расстоянием <= 1
        var domainSpan = domain.AsSpan();
        string? bestMatch = null;
        var minDistance = int.MaxValue;

        foreach (var pop in AllowedDomains)
        {
            var dist = GetDamerauDistance(domainSpan, pop.AsSpan());
            if (dist <= 1 && dist < minDistance)
            {
                minDistance = dist;
                bestMatch = pop;
            }
        }

        return bestMatch;
    }

    //zero-allocation расчет расстояния дамерау-левенштейна на стеке
    private static int GetDamerauDistance(ReadOnlySpan<char> s, ReadOnlySpan<char> t)
    {
        var n = s.Length;
        var m = t.Length;
        if (n == 0)
        {
            return m;
        }

        if (m == 0)
        {
            return n;
        }

        if (Math.Abs(n - m) > 2)
        {
            return 99;
        }

        Span<int> p2 = stackalloc int[m + 1];
        Span<int> p1 = stackalloc int[m + 1];
        Span<int> cur = stackalloc int[m + 1];

        for (var j = 0; j <= m; j++)
        {
            p1[j] = j;
        }

        for (var i = 1; i <= n; i++)
        {
            cur[0] = i;
            var sc = char.ToLowerInvariant(s[i - 1]);

            for (var j = 1; j <= m; j++)
            {
                var tc = char.ToLowerInvariant(t[j - 1]);
                var cost = sc == tc ? 0 : 1;

                var insertion = cur[j - 1] + 1;
                var deletion = p1[j] + 1;
                var substitution = p1[j - 1] + cost;

                var min = Math.Min(Math.Min(insertion, deletion), substitution);

                if (i > 1 && j > 1 && sc == char.ToLowerInvariant(t[j - 2]) && char.ToLowerInvariant(s[i - 2]) == tc)
                {
                    min = Math.Min(min, p2[j - 2] + 1);
                }

                cur[j] = min;
            }

            p1.CopyTo(p2);
            cur.CopyTo(p1);
        }

        return cur[m];
    }
}
