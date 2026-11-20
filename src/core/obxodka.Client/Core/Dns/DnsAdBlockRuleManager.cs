namespace obxodka.Core;

//менеджер правил блокировки рекламы (загрузка из сети, кэширование, парсинг)
public static class DnsAdBlockRuleManager
{
    //ссылка на официальный список фильтрации по умолчанию
    private const string DefaultRemoteUrl = "https://raw.githubusercontent.com/AdguardTeam/AdguardSDNSFilter/gh-pages/Filters/rules.txt";

    //парсинг строк правил блокировки в набор доменов
    public static HashSet<string> ParseRuleLines(IEnumerable<string> lines) =>
        lines
            .Select(ExtractRuleDomain)
            .Where(static d => !string.IsNullOrEmpty(d))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

    //извлечение очищенного домена из одной строки правил
    private static string? ExtractRuleDomain(string rawLine)
    {
        var line = rawLine.Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
        {
            return null;
        }

        if (line.StartsWith("0.0.0.0 ", StringComparison.OrdinalIgnoreCase) ||
            line.StartsWith("127.0.0.1 ", StringComparison.OrdinalIgnoreCase))
        {
            var parts = line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                line = parts[1];
            }
        }
        else if (line.StartsWith("||", StringComparison.Ordinal) && line.EndsWith('^'))
        {
            line = line[2..^1];
        }

        var clean = CleanDomain(line);
        return string.IsNullOrEmpty(clean) ? null : clean;
    }

    //загрузка списка блокируемых доменов из локального кэша
    public static async Task<int> LoadFromCacheAsync(string cacheFilePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cacheFilePath))
        {
            return 0;
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(cacheFilePath, cancellationToken);
            var domains = ParseRuleLines(lines);
            if (domains.Count > 0)
            {
                DnsAdBlocker.UpdateBlockedDomains(domains, true);
                return domains.Count;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException
                                       or NotSupportedException or ArgumentException)
        {
            //игнорируем поврежденный или заблокированный файл кэша правил
        }

        return 0;
    }

    //сохранение списка заблокированных доменов в локальный кэш
    public static async Task<bool> SaveCacheAsync(string cacheFilePath, IEnumerable<string> domains,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var dir = Path.GetDirectoryName(cacheFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                _ = Directory.CreateDirectory(dir);
            }

            var tempPath = $"{cacheFilePath}.tmp.{Guid.NewGuid():N}";
            await File.WriteAllLinesAsync(tempPath, domains, cancellationToken);
            File.Move(tempPath, cacheFilePath, true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException or OperationCanceledException
                                       or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    //синхронизация и загрузка свежих правил с удаленного сервера
    public static async Task<bool> SyncFromRemoteAsync(
        string? url = null,
        string? cacheFilePath = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var targetUrl = string.IsNullOrWhiteSpace(url) ? DefaultRemoteUrl : url;
        using var defaultClient = httpClient is null
            ? new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            }
            : null;
        var client = httpClient ?? defaultClient!;

        try
        {
            using var response = await client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            List<string> lines = [with(1024)];
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lines.Add(line);
            }

            var domains = ParseRuleLines(lines);
            if (domains.Count < 10)
            {
                return false;
            }

            DnsAdBlocker.UpdateBlockedDomains(domains, true);

            if (!string.IsNullOrWhiteSpace(cacheFilePath))
            {
                _ = await SaveCacheAsync(cacheFilePath, domains, cancellationToken);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or SocketException
                                       or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    //очистка и валидация доменного имени
    private static string? CleanDomain(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('.');
        return trimmed.Length < 3 || !trimmed.Contains('.')
            ? null
            : trimmed.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_'))
                ? null
                : trimmed.ToLowerInvariant();
    }
}
