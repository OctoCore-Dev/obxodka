namespace obxodka.Core;

//модуль блокировки рекламы и трекеров на уровне dns
public static partial class DnsAdBlocker
{
    //неизменяемый набор правил со быстрым поиском через span
    private sealed class RuleSet
    {
        public FrozenSet<string> Set { get; }
        public FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> Lookup { get; }

        public RuleSet(IEnumerable<string> domains)
        {
            Set = domains.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            Lookup = Set.GetAlternateLookup<ReadOnlySpan<char>>();
        }
    }





    //флаг включения блокировки рекламы
    private static volatile bool s_isAdBlockEnabled = true;
    //счетчик заблокированных dns запросов
    private static long s_blockedQueriesCount;

    //включение или отключение блокировщика рекламы
    public static bool IsAdBlockEnabled
    {
        get => s_isAdBlockEnabled;
        set => s_isAdBlockEnabled = value;
    }

    //общее количество заблокированных запросов
    public static long BlockedQueriesCount => Volatile.Read(ref s_blockedQueriesCount);

    //сброс счетчика блокировок
    public static void ResetBlockedCount() => Interlocked.Exchange(ref s_blockedQueriesCount, 0);

    //количество заблокированных доменов в базе
    public static int BlockedDomainsCount => s_rules.Set.Count;

    //количество доменов в белом списке
    public static int WhitelistDomainsCount => s_whitelist.Set.Count;

    //сброс списков правил к исходным значениям по умолчанию
    public static void ResetToDefaults()
    {
        s_rules = new RuleSet(s_defaultBlockedDomains ?? []);
        s_whitelist = new RuleSet(s_defaultWhitelistDomains ?? []);
        ResetBlockedCount();
    }

    //обновление набора заблокированных доменов
    public static void UpdateBlockedDomains(IEnumerable<string> domains, bool mergeWithDefaults = false)
    {
        if (mergeWithDefaults)
        {
            var merged = new HashSet<string>(s_defaultBlockedDomains ?? [], StringComparer.OrdinalIgnoreCase);
            merged.UnionWith(domains);
            s_rules = new RuleSet(merged);
        }
        else
        {
            s_rules = new RuleSet(domains);
        }
    }

    //обновление белого списка доменов
    public static void UpdateWhitelist(IEnumerable<string> domains) =>
        s_whitelist = new RuleSet(domains);

    //добавление одного домена в белый список
    public static void AddToWhitelist(string domain)
    {
        var current = new HashSet<string>(s_whitelist.Set, StringComparer.OrdinalIgnoreCase) { domain };
        s_whitelist = new RuleSet(current);
    }

    //удаление одного домена из белого списка
    public static void RemoveFromWhitelist(string domain)
    {
        var current = new HashSet<string>(s_whitelist.Set, StringComparer.OrdinalIgnoreCase);
        _ = current.Remove(domain);
        s_whitelist = new RuleSet(current);
    }

    //проверка наличия домена в белом списке
    public static bool IsWhitelisted(ReadOnlySpan<char> domain) =>
        s_whitelist.Lookup.Contains(domain);

    //проверка заблокирован ли домен с проверкой всех родительских зон
    public static bool IsBlocked(ReadOnlySpan<char> domain)
    {
        if (IsWhitelisted(domain))
        {
            return false;
        }

        var rules = s_rules;
        if (rules.Lookup.Contains(domain))
        {
            return true;
        }

        var remaining = domain;
        while (true)
        {
            var dot = remaining.IndexOf('.');
            if (dot < 0 || dot + 1 >= remaining.Length)
            {
                break;
            }
            remaining = remaining[(dot + 1)..];
            if (rules.Lookup.Contains(remaining))
            {
                return true;
            }
        }

        return false;
    }

    //проверка блокировки домена по строке
    public static bool IsBlocked(string domain) => IsBlocked(domain.AsSpan());
}
