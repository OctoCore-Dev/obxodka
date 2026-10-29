namespace obxodka.Core;

public static class DnsAdBlockRuleManager
{
    public const string DefaultRemoteUrl = "https://raw.githubusercontent.com/AdguardTeam/AdguardSDNSFilter/gh-pages/Filters/rules.txt";

    public static HashSet<string> ParseRuleLines(IEnumerable<string> lines)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('!'))
            {
                continue;
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
            if (!string.IsNullOrEmpty(clean))
            {
                _ = result.Add(clean);
            }
        }

        return result;
    }

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
                DnsAdBlocker.UpdateBlockedDomains(domains, mergeWithDefaults: true);
                return domains.Count;
            }
        }
        catch
        {
        }

        return 0;
    }

    public static async Task<bool> SaveCacheAsync(string cacheFilePath, IEnumerable<string> domains, CancellationToken cancellationToken = default)
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
            File.Move(tempPath, cacheFilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> SyncFromRemoteAsync(
        string? url = null,
        string? cacheFilePath = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        var targetUrl = string.IsNullOrWhiteSpace(url) ? DefaultRemoteUrl : url;
        var client = httpClient;
        var shouldDisposeClient = false;

        if (client is null)
        {
            client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            shouldDisposeClient = true;
        }

        try
        {
            using var response = await client.GetAsync(targetUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            var lines = new List<string>(1024);
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                lines.Add(line);
            }

            var domains = ParseRuleLines(lines);
            if (domains.Count < 10)
            {
                return false;
            }

            DnsAdBlocker.UpdateBlockedDomains(domains, mergeWithDefaults: true);

            if (!string.IsNullOrWhiteSpace(cacheFilePath))
            {
                _ = await SaveCacheAsync(cacheFilePath, domains, cancellationToken);
            }

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (shouldDisposeClient)
            {
                client.Dispose();
            }
        }
    }

    private static string? CleanDomain(string raw)
    {
        var trimmed = raw.Trim().TrimEnd('.');
        if (trimmed.Length < 3 || !trimmed.Contains('.'))
        {
            return null;
        }

        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (!(char.IsAsciiLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_'))
            {
                return null;
            }
        }

        return trimmed.ToLowerInvariant();
    }
}
