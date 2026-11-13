namespace obxodka.Core.Models;

//запись о динамическом маршруте, направленном в обход VPN-туннеля
public sealed record BypassRouteEntry(string DestinationIp, string Reason);

//политика раздельного туннелирования (Split Tunneling) для направления доверенного трафика мимо VPN
public sealed class SplitTunnelPolicy
{
    //список официальных государственных доменов РФ, блокирующих доступ из зарубежных IP
    private static readonly FrozenSet<string> s_ruGovDomains = new[]
    {
        "gosuslugi.ru",
        "esia.gosuslugi.ru",
        "gu-st.ru",
        "mos.ru",
        "emias.info",
        "emiass.info",
        "nalog.ru",
        "nalog.gov.ru",
        "lkfl2.nalog.ru",
        "pfr.gov.ru",
        "sfr.gov.ru",
        "pochta.ru",
        "rzd.ru",
        "spb.ru",
        "gov.ru",
        "kremlin.ru",
        "government.ru",
        "duma.gov.ru",
        "council.gov.ru",
        "cbr.ru",
        "roskomnadzor.gov.ru",
        "eais.rkn.gov.ru",
        "fssp.gov.ru",
        "gibdd.ru",
        "mvd.gov.ru",
        "customs.gov.ru",
        "sudrf.ru",
        "zakupki.gov.ru",
        "genproc.gov.ru",
        "rosreestr.gov.ru",
        "fas.gov.ru",
        "fedresurs.ru",
        "crpt.ru",
        "честныйзнак.рф",
        "xn--80ajghhoc2ajlc1b.xn--p1ai"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //список российских банков и платежных систем, требующих прямого подключения
    private static readonly FrozenSet<string> s_ruBankDomains = new[]
    {
        "sberbank.ru",
        "sber.ru",
        "online.sberbank.ru",
        "sberbank.com",
        "tbank.ru",
        "tinkoff.ru",
        "cdn-tinkoff.ru",
        "vtb.ru",
        "online.vtb.ru",
        "alfabank.ru",
        "raiffeisen.ru",
        "gazprombank.ru",
        "psbank.ru",
        "sovcombank.ru",
        "rshb.ru",
        "open.ru",
        "rosbank.ru",
        "mtsbank.ru",
        "ozon.bank",
        "bank.yandex.ru",
        "pochtabank.ru",
        "mkb.ru",
        "uralsib.ru",
        "rencredit.ru",
        "homecredit.ru",
        "home.bank",
        "bspb.ru",
        "zenit.ru",
        "domrfbank.ru",
        "nspk.ru",
        "sbp.nspk.ru",
        "mirpay.ru",
        "privetmir.ru",
        "yoomoney.ru",
        "yookassa.ru"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //список российских медиа, видеохостингов и социальных сетей
    private static readonly FrozenSet<string> s_ruMediaDomains = new[]
    {
        "yandex.ru",
        "ya.ru",
        "yandex.net",
        "yastatic.net",
        "yandex.com",
        "music.yandex.ru",
        "kinopoisk.ru",
        "kp.yandex.net",
        "vk.com",
        "vk.ru",
        "vkvideo.ru",
        "userapi.com",
        "vkuseraudio.net",
        "vkuservideo.net",
        "vkuser.net",
        "mycdn.me",
        "ok.ru",
        "mail.ru",
        "imgsmail.ru",
        "dzen.ru",
        "rutube.ru",
        "rtbcdn.ru",
        "ivi.ru",
        "okko.tv",
        "wink.ru",
        "premier.one",
        "kion.ru",
        "smotrim.ru",
        "1tv.ru",
        "ntv.ru",
        "matchtv.ru",
        "vgtrk.ru",
        "tass.ru",
        "ria.ru",
        "rbc.ru",
        "kommersant.ru",
        "lenta.ru",
        "gazeta.ru",
        "2gis.ru",
        "2gis.com",
        "rambler.ru",
        "pikabu.ru",
        "habr.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //список российских маркетплейсов, ритейлеров и служб доставки
    private static readonly FrozenSet<string> s_ruMarketplaceDomains = new[]
    {
        "ozon.ru",
        "ozone.ru",
        "ozonusercontent.com",
        "wildberries.ru",
        "wb.ru",
        "wbbasket.ru",
        "avito.ru",
        "avito.st",
        "dns-shop.ru",
        "citilink.ru",
        "mvideo.ru",
        "eldorado.ru",
        "kuper.ru",
        "sbermarket.ru",
        "samokat.ru",
        "market.yandex.ru",
        "megamarket.ru",
        "sbermegamarket.ru",
        "aliexpress.ru",
        "magnit.ru",
        "5ka.ru",
        "perekrestok.ru",
        "vkusvill.ru",
        "auchan.ru",
        "lenta.com",
        "cdek.ru",
        "boxberry.ru",
        "lamoda.ru",
        "detmir.ru",
        "vseinstrumenti.ru",
        "petrovich.ru",
        "hoff.ru",
        "lemanapro.ru",
        "sportmaster.ru",
        "apteka.ru",
        "eapteka.ru"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //список серверов измерения скорости и телеком-провайдеров рф для замера на полной скорости провайдера
    private static readonly FrozenSet<string> s_ruSpeedtestDomains = new[]
    {
        "speedtest.ru",
        "spb.speedtest.ru",
        "speedtest.net",
        "spb.speedtest.net",
        "fast.com",
        "selectel.ru",
        "selectel.net",
        "rostelecom.ru",
        "megafon.ru",
        "mts.ru",
        "beeline.ru",
        "t2.ru",
        "tele2.ru",
        "dom.ru",
        "ertelecom.ru",
        "speedtest.customs.ru",
        "speedtest.yandex.ru"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    //объект синхронизации для потокобезопасного доступа к изменяемым коллекциям
    private readonly Lock _sync = new();
    private HashSet<string> _bypassedExeNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _bypassedExePaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _customDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<BypassRouteEntry> _activeBypassRoutes = [];

    //включена ли политика раздельного туннелирования
    public bool Enabled { get; set; } = true;

    //пускать ли сессии удаленного управления (RDP, AnyDesk, TeamViewer) мимо VPN во избежание обрыва связи
    public bool BypassRemoteManagement { get; set; } = true;

    //направлять ли российские государственные сервисы в обход туннеля
    public bool BypassRuGov { get; set; } = true;

    //направлять ли российские банки и платежные системы в обход туннеля
    public bool BypassRuBanks { get; set; } = true;

    //направлять ли российские медиа-сервисы, ТВ и социальные сети в обход туннеля
    public bool BypassRuMedia { get; set; } = true;

    //направлять ли маркетплейсы и службы доставки в обход туннеля
    public bool BypassRuMarketplaces { get; set; } = true;

    //направлять ли абсолютно все сайты в зонах .ru, .su, .рф в обход туннеля
    public bool BypassAllRuTlds { get; set; } = true;

    //направлять ли серверы замера скорости и телеком-провайдеров в обход туннеля
    public bool BypassRuSpeedtest { get; set; } = true;

    //пользовательские IP-адреса и подсети для прямого подключения
    public List<string> CustomBypassIps { get; set; } = [];

    //пользовательские домены для прямого подключения
    public List<string> CustomBypassDomains { get; set; } = [];

    //потокобезопасное получение или перезапись списка активных динамических маршрутов
    public List<BypassRouteEntry> ActiveBypassRoutes
    {
        get => GetActiveBypassRoutes();
        set
        {
            lock (_sync)
            {
                _activeBypassRoutes.Clear();
                if (value is not null)
                {
                    _activeBypassRoutes.AddRange(value);
                }
            }
        }
    }

    //проверка наличия хотя бы одного правила исключения исполняемых файлов
    public bool HasBypassedExecutables
    {
        get
        {
            lock (_sync)
            {
                return _bypassedExeNames.Count > 0 || _bypassedExePaths.Count > 0;
            }
        }
    }

    //проверка активности хотя бы одного доменного правила раздельного туннелирования
    public bool HasDomainRules
    {
        get
        {
            if (!Enabled)
            {
                return false;
            }

            if (BypassRuGov || BypassRuBanks || BypassRuMedia || BypassRuMarketplaces || BypassAllRuTlds || BypassRuSpeedtest)
            {
                return true;
            }

            lock (_sync)
            {
                return _customDomains.Count > 0;
            }
        }
    }

    //защита системных процессов и адаптеров самой Обходки от случайного добавления в исключения
    public static bool IsProtectedObxodkaEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("обходка", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("wintun", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("octocore", StringComparison.OrdinalIgnoreCase));

    //установка списка исполняемых файлов и пакетов приложений, направляемых в обход VPN
    public void SetBypassedExecutables(IEnumerable<string> packagesOrPaths)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in packagesOrPaths)
        {
            if (string.IsNullOrWhiteSpace(raw) || IsProtectedObxodkaEntry(raw))
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (trimmed.Contains('\\') || trimmed.Contains('/'))
            {
                _ = paths.Add(trimmed);
                var fileName = Path.GetFileName(trimmed);
                if (!string.IsNullOrWhiteSpace(fileName) && !IsProtectedObxodkaEntry(fileName))
                {
                    _ = names.Add(fileName);
                    if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        _ = names.Add(fileName[..^4]);
                    }
                }
            }
            else
            {
                _ = names.Add(trimmed);
                _ = names.Add(trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? trimmed[..^4]
                    : $"{trimmed}.exe");
            }
        }

        lock (_sync)
        {
            _bypassedExeNames = names;
            _bypassedExePaths = paths;
        }
    }

    //парсинг и сохранение пользовательских правил (IP-адреса, подсети, доменные имена и URL)
    public void SetCustomRules(IEnumerable<string> rawRules)
    {
        var domains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ips = new List<string>();

        foreach (var raw in rawRules)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var cleaned = NormalizeRuleEntry(raw);
            if (string.IsNullOrWhiteSpace(cleaned) || IsProtectedObxodkaEntry(cleaned))
            {
                continue;
            }

            if (IPAddress.TryParse(cleaned, out var parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetwork)
            {
                if (!ips.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    ips.Add(cleaned);
                }
            }
            else
            {
                _ = domains.Add(cleaned);
            }
        }

        lock (_sync)
        {
            _customDomains = domains;
            CustomBypassDomains = [.. domains];
            CustomBypassIps = ips;
        }
    }

    //нормализация пользовательского правила: очистка от протоколов, слешей, портов и масок поддоменов
    public static string NormalizeRuleEntry(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var s = input.Trim().ToLowerInvariant();
        if (s.StartsWith("https://", StringComparison.Ordinal))
        {
            s = s[8..];
        }
        else if (s.StartsWith("http://", StringComparison.Ordinal))
        {
            s = s[7..];
        }

        //если это не CIDR-подсеть (например, 192.168.1.0/24), отсекаем URL-путь после первого слеша
        var slashIdx = s.IndexOf('/');
        if (slashIdx >= 0)
        {
            var beforeSlash = s[..slashIdx];
            var afterSlash = s[(slashIdx + 1)..];
            if (!(IPAddress.TryParse(beforeSlash, out _) && int.TryParse(afterSlash, out var prefix) && prefix is >= 0 and <= 128))
            {
                s = beforeSlash;
            }
        }

        //отсекаем порт (:443), но НЕ обрезаем IPv6-адреса, содержащие несколько двоеточий
        if (!s.Contains("::") && s.Count(c => c == ':') == 1)
        {
            var colonIdx = s.IndexOf(':');
            if (colonIdx >= 0)
            {
                s = s[..colonIdx];
            }
        }
        else if (s.StartsWith('[') && s.Contains(']'))
        {
            //формат IPv6 с портом: [2a02:6b8::1]:8080 -> извлекаем чистый IPv6
            var closeBracket = s.IndexOf(']');
            s = s[1..closeBracket];
        }

        if (s.StartsWith("*.", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        return s.Trim('.');
    }

    //проверка, должен ли процесс выполняться напрямую в обход VPN
    public bool IsProcessBypassed(string? exeName, string? fullPath)
    {
        if (!Enabled || IsProtectedObxodkaEntry(exeName) || IsProtectedObxodkaEntry(fullPath))
        {
            return false;
        }

        lock (_sync)
        {
            if (_bypassedExeNames.Count == 0 && _bypassedExePaths.Count == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(fullPath) && _bypassedExePaths.Contains(fullPath))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(exeName) && _bypassedExeNames.Contains(exeName))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                var fileName = Path.GetFileName(fullPath);
                if (!string.IsNullOrWhiteSpace(fileName) && _bypassedExeNames.Contains(fileName))
                {
                    return true;
                }
            }

            return false;
        }
    }

    //проверка, должен ли сетевой домен направляться напрямую в обход VPN
    public bool IsDomainBypassed(string? domain)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(domain) || IsProtectedObxodkaEntry(domain))
        {
            return false;
        }

        var normalized = domain.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalized.Length == 0 || IsProtectedObxodkaEntry(normalized))
        {
            return false;
        }

        //проверка правила обхода всех национальных доменных зон РФ
        if (BypassAllRuTlds &&
            (normalized == "ru" || normalized.EndsWith(".ru", StringComparison.Ordinal) ||
             normalized == "su" || normalized.EndsWith(".su", StringComparison.Ordinal) ||
             normalized == "xn--p1ai" || normalized.EndsWith(".xn--p1ai", StringComparison.Ordinal) ||
             normalized == "рф" || normalized.EndsWith(".рф", StringComparison.Ordinal)))
        {
            return true;
        }

        //иерархическая проверка: сначала полный поддомен, затем поднимаемся вверх к корню
        var current = normalized;
        while (!string.IsNullOrEmpty(current))
        {
            if (BypassRuGov && s_ruGovDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuBanks && s_ruBankDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuMedia && s_ruMediaDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuMarketplaces && s_ruMarketplaceDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuSpeedtest && s_ruSpeedtestDomains.Contains(current))
            {
                return true;
            }

            lock (_sync)
            {
                if (_customDomains.Contains(current))
                {
                    return true;
                }
            }

            var dotIdx = current.IndexOf('.');
            if (dotIdx < 0 || dotIdx + 1 >= current.Length)
            {
                break;
            }

            current = current[(dotIdx + 1)..];
        }

        return false;
    }

    //получение списка популярных доменов для предварительного прогрева DNS и маршрутизации
    public List<string> GetSeedDomainsToPreResolve()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Enabled)
        {
            return [];
        }

        if (BypassRuGov)
        {
            _ = result.Add("gosuslugi.ru");
            _ = result.Add("www.gosuslugi.ru");
            _ = result.Add("esia.gosuslugi.ru");
            _ = result.Add("mos.ru");
            _ = result.Add("www.mos.ru");
            _ = result.Add("lkfl2.nalog.ru");
            _ = result.Add("nalog.gov.ru");
            _ = result.Add("pochta.ru");
            _ = result.Add("rzd.ru");
        }

        if (BypassRuBanks)
        {
            _ = result.Add("online.sberbank.ru");
            _ = result.Add("sberbank.ru");
            _ = result.Add("tbank.ru");
            _ = result.Add("www.tbank.ru");
            _ = result.Add("tinkoff.ru");
            _ = result.Add("vtb.ru");
            _ = result.Add("online.vtb.ru");
            _ = result.Add("alfabank.ru");
            _ = result.Add("yoomoney.ru");
        }

        if (BypassRuMedia)
        {
            _ = result.Add("ya.ru");
            _ = result.Add("yandex.ru");
            _ = result.Add("kinopoisk.ru");
            _ = result.Add("www.kinopoisk.ru");
            _ = result.Add("vk.com");
            _ = result.Add("vkvideo.ru");
            _ = result.Add("dzen.ru");
            _ = result.Add("rutube.ru");
            _ = result.Add("mail.ru");
            _ = result.Add("ok.ru");
        }

        if (BypassRuMarketplaces)
        {
            _ = result.Add("ozon.ru");
            _ = result.Add("www.ozon.ru");
            _ = result.Add("wildberries.ru");
            _ = result.Add("www.wildberries.ru");
            _ = result.Add("avito.ru");
            _ = result.Add("www.avito.ru");
            _ = result.Add("dns-shop.ru");
            _ = result.Add("kuper.ru");
        }

        if (BypassRuSpeedtest)
        {
            _ = result.Add("speedtest.ru");
            _ = result.Add("spb.speedtest.ru");
            _ = result.Add("rostelecom.ru");
            _ = result.Add("selectel.ru");
        }

        lock (_sync)
        {
            foreach (var d in _customDomains)
            {
                _ = result.Add(d);
            }
        }

        return [.. result];
    }

    //добавление динамического IP-маршрута в список активных исключений туннеля
    public void RecordBypassRoute(string destinationIp, string reason)
    {
        if (string.IsNullOrWhiteSpace(destinationIp))
        {
            return;
        }

        lock (_sync)
        {
            for (var i = 0; i < _activeBypassRoutes.Count; i++)
            {
                if (_activeBypassRoutes[i].DestinationIp.Equals(destinationIp, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            _activeBypassRoutes.Add(new BypassRouteEntry(destinationIp, reason));
        }
    }

    //возвращает потокобезопасный снимок текущих активных маршрутов в обход туннеля
    public List<BypassRouteEntry> GetActiveBypassRoutes()
    {
        lock (_sync)
        {
            return [.. _activeBypassRoutes];
        }
    }

    //очистка списка активных маршрутов
    public void ClearActiveRoutes()
    {
        lock (_sync)
        {
            _activeBypassRoutes.Clear();
        }
    }
}
