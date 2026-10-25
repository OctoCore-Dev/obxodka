namespace obxodka.Core.Models;

public sealed record BypassRouteEntry(string DestinationIp, string Reason);

public sealed class SplitTunnelPolicy
{
    private static readonly FrozenSet<string> t_ruGovDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "gosuslugi.ru",
        "esia.gosuslugi.ru",
        "gu-st.ru",
        "mos.ru",
        "emiass.info",
        "nalog.ru",
        "nalog.gov.ru",
        "pfr.gov.ru",
        "sfr.gov.ru",
        "pochta.ru",
        "rzd.ru",
        "spb.ru",
        "gov.ru",
        "kremlin.ru",
        "cbr.ru",
        "roskomnadzor.gov.ru",
        "fssp.gov.ru",
        "gibdd.ru"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> t_ruBankDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "sberbank.ru",
        "sber.ru",
        "online.sberbank.ru",
        "sberbank.com",
        "tbank.ru",
        "tinkoff.ru",
        "cdn-tinkoff.ru",
        "vtb.ru",
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
        "nspk.ru",
        "sbp.nspk.ru",
        "mirpay.ru",
        "privetmir.ru",
        "yoomoney.ru",
        "yookassa.ru"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> t_ruMediaDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "yandex.ru",
        "ya.ru",
        "yandex.net",
        "yastatic.net",
        "yandex.com",
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
        "2gis.ru",
        "2gis.com",
        "rambler.ru",
        "pikabu.ru",
        "habr.com"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> t_ruMarketplaceDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
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

    private readonly Lock _sync = new();
    private HashSet<string> _bypassedExeNames = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _bypassedExePaths = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _customDomains = new(StringComparer.OrdinalIgnoreCase);

    public bool Enabled { get; set; } = true;

    public bool BypassRemoteManagement { get; set; } = true;

    public bool BypassRuGov { get; set; }

    public bool BypassRuBanks { get; set; }

    public bool BypassRuMedia { get; set; }

    public bool BypassRuMarketplaces { get; set; }

    public bool BypassAllRuTlds { get; set; }

    public List<string> CustomBypassIps { get; set; } = [];

    public List<string> CustomBypassDomains { get; set; } = [];

    public List<BypassRouteEntry> ActiveBypassRoutes { get; set; } = [];

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

    public bool HasDomainRules
    {
        get
        {
            if (!Enabled)
            {
                return false;
            }

            if (BypassRuGov || BypassRuBanks || BypassRuMedia || BypassRuMarketplaces || BypassAllRuTlds)
            {
                return true;
            }

            lock (_sync)
            {
                return _customDomains.Count > 0;
            }
        }
    }

    public static bool IsProtectedObxodkaEntry(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("обходка", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("wintun", StringComparison.OrdinalIgnoreCase));

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

        var slashIdx = s.IndexOf('/');
        if (slashIdx >= 0)
        {
            s = s[..slashIdx];
        }

        var colonIdx = s.IndexOf(':');
        if (colonIdx >= 0)
        {
            s = s[..colonIdx];
        }

        if (s.StartsWith("*.", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        return s.Trim('.');
    }

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

        if (BypassAllRuTlds &&
            (normalized.EndsWith(".ru", StringComparison.Ordinal) ||
             normalized.EndsWith(".su", StringComparison.Ordinal) ||
             normalized.EndsWith(".xn--p1ai", StringComparison.Ordinal) ||
             normalized.EndsWith(".рф", StringComparison.Ordinal)))
        {
            return true;
        }

        var current = normalized;
        while (!string.IsNullOrEmpty(current))
        {
            if (BypassRuGov && t_ruGovDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuBanks && t_ruBankDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuMedia && t_ruMediaDomains.Contains(current))
            {
                return true;
            }

            if (BypassRuMarketplaces && t_ruMarketplaceDomains.Contains(current))
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

        lock (_sync)
        {
            foreach (var d in _customDomains)
            {
                _ = result.Add(d);
            }
        }

        return [.. result];
    }

    public void RecordBypassRoute(string destinationIp, string reason)
    {
        if (string.IsNullOrWhiteSpace(destinationIp))
        {
            return;
        }

        lock (_sync)
        {
            if (!ActiveBypassRoutes.Any(r => r.DestinationIp.Equals(destinationIp, StringComparison.OrdinalIgnoreCase)))
            {
                ActiveBypassRoutes.Add(new BypassRouteEntry(destinationIp, reason));
            }
        }
    }

    public void ClearActiveRoutes()
    {
        lock (_sync)
        {
            ActiveBypassRoutes.Clear();
        }
    }
}
