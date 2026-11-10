namespace obxodka.Helpers;

//парсинг версий обновлений из google play github releases и microsoft store
public static partial class StoreVersionParser
{
    [GeneratedRegex(@"\[\[\[""(\d+\.\d+\.\d+)""\]\]")]
    private static partial Regex GooglePlayVersionRegex();

    [GeneratedRegex(@"\[\[\[""(\d+\.\d+\.\d+)""\]\].*?""145"":\[null,\[null,""([^""]+)""\]\]", RegexOptions.Singleline)]
    private static partial Regex GooglePlayWhatsNewRegex();

    [GeneratedRegex(@"\b([a-fA-F0-9]{64})\s+(\S+)")]
    private static partial Regex Sha256ChecksumLineRegex();

    [GeneratedRegex(@"^\s*#+\s*(\[CHANGELOG\]|Release Notes|Что нового|What's Changed).*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ChangelogHeaderRegex();

    [GeneratedRegex(@"^\s*#+\s*(\[CHECKSUMS\]|Checksums|SHA256).*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ChecksumsHeaderRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]{64}\b")]
    private static partial Regex RawSha256HexRegex();

    //извлечение версии и описания релиза из html страницы google play
    public static (string? Version, string? WhatsNew) ParseGooglePlayHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return (null, null);
        }

        var match = GooglePlayVersionRegex().Match(html);
        if (!match.Success)
        {
            return (null, null);
        }

        var version = match.Groups[1].Value;
        string? whatsNew = null;

        var wnMatch = GooglePlayWhatsNewRegex().Match(html);
        if (wnMatch.Success)
        {
            whatsNew = Regex.Unescape(wnMatch.Groups[2].Value);
        }

        return (version, whatsNew);
    }

    //извлечение ожидаемого sha256 хеша для указанного файла из описания релиза
    public static string? ExtractSha256FromReleaseBody(string? body, string? targetFileName)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var lines = body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var line in lines)
        {
            var match = Sha256ChecksumLineRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var hash = match.Groups[1].Value.ToLowerInvariant();
            var file = match.Groups[2].Value;

            if (!string.IsNullOrWhiteSpace(targetFileName) &&
                (file.Contains(targetFileName, StringComparison.OrdinalIgnoreCase) ||
                 targetFileName.Contains(file, StringComparison.OrdinalIgnoreCase)))
            {
                return hash;
            }

            if (string.IsNullOrWhiteSpace(targetFileName) &&
                (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                 file.Contains("Setup", StringComparison.OrdinalIgnoreCase)))
            {
                return hash;
            }
        }

        return null;
    }

    //очистка текста релиза от технических хешей sha256 и служебных секций
    public static string? CleanReleaseNotes(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        //если есть выделенная секция списка изменений — берем содержимое после нее
        var changelogMatch = ChangelogHeaderRegex().Match(body);
        var sourceText = changelogMatch.Success
            ? body[(changelogMatch.Index + changelogMatch.Length)..]
            : body;

        var lines = sourceText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var cleanedLines = new List<string>(lines.Length);
        var insideCodeBlock = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                insideCodeBlock = !insideCodeBlock;
                continue;
            }

            if (insideCodeBlock)
            {
                if (Sha256ChecksumLineRegex().IsMatch(line) || RawSha256HexRegex().IsMatch(line))
                {
                    continue;
                }
            }

            if (ChecksumsHeaderRegex().IsMatch(line) ||
                Sha256ChecksumLineRegex().IsMatch(line) ||
                RawSha256HexRegex().IsMatch(line))
            {
                continue;
            }

            if (ChangelogHeaderRegex().IsMatch(line))
            {
                continue;
            }

            cleanedLines.Add(line);
        }

        var result = string.Join("\n", cleanedLines).Trim();
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    //извлечение версии списка изменений ссылки на установщик и хеша sha256 из json github releases
    public static (string? Version, string? WhatsNew, string? DownloadUrl, string? ExpectedSha256) ParseGitHubReleaseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (null, null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagProp))
            {
                return (null, null, null, null);
            }

            var tagName = tagProp.GetString();
            if (string.IsNullOrWhiteSpace(tagName))
            {
                return (null, null, null, null);
            }

            var version = tagName.TrimStart('v', 'V');
            var rawBody = root.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            string? downloadUrl = null;
            string? installerFileName = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var np) ? np.GetString() ?? string.Empty : string.Empty;
                    if (asset.TryGetProperty("browser_download_url", out var urlProp))
                    {
                        var url = urlProp.GetString();
                        if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase) || name.Contains("Installer", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = url;
                            installerFileName = name;
                            break;
                        }
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".msixbundle", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".msix", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = url;
                            installerFileName = name;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUrl) && root.TryGetProperty("html_url", out var htmlUrlProp))
            {
                downloadUrl = htmlUrlProp.GetString();
            }

            var expectedSha256 = ExtractSha256FromReleaseBody(rawBody, installerFileName);
            var whatsNew = CleanReleaseNotes(rawBody);

            return (version, whatsNew, downloadUrl, expectedSha256);
        }
        catch
        {
            return (null, null, null, null);
        }
    }

    //извлечение версии пакета из каталога microsoft store
    public static string? ParseMicrosoftStoreCatalogJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("Product", out var product))
            {
                return null;
            }

            if (!product.TryGetProperty("DisplaySkuAvailabilities", out var skus) || skus.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var sku in skus.EnumerateArray())
            {
                if (sku.TryGetProperty("Sku", out var skuObj) &&
                    skuObj.TryGetProperty("Properties", out var props) &&
                    props.TryGetProperty("Packages", out var pkgs) &&
                    pkgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var pkg in pkgs.EnumerateArray())
                    {
                        if (pkg.TryGetProperty("Version", out var verProp))
                        {
                            var rawVerStr = verProp.GetString();
                            if (ulong.TryParse(rawVerStr, out var rawVer) && rawVer > 0)
                            {
                                var major = (rawVer >> 48) & 0xFFFF;
                                var minor = (rawVer >> 32) & 0xFFFF;
                                var build = (rawVer >> 16) & 0xFFFF;
                                return $"{major}.{minor}.{build}";
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    //проверка наличия более новой версии
    public static bool IsNewerVersion(string? currentVersion, string? latestVersion)
    {
        if (string.IsNullOrWhiteSpace(latestVersion))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(currentVersion))
        {
            return true;
        }

        var cleanCurrent = CleanVersionString(currentVersion);
        var cleanLatest = CleanVersionString(latestVersion);

        if (Version.TryParse(cleanCurrent, out var cur) && Version.TryParse(cleanLatest, out var lat))
        {
            var curMajor = cur.Major >= 0 ? cur.Major : 0;
            var latMajor = lat.Major >= 0 ? lat.Major : 0;
            if (latMajor != curMajor)
            {
                return latMajor > curMajor;
            }

            var curMinor = cur.Minor >= 0 ? cur.Minor : 0;
            var latMinor = lat.Minor >= 0 ? lat.Minor : 0;
            if (latMinor != curMinor)
            {
                return latMinor > curMinor;
            }

            var curBuild = cur.Build >= 0 ? cur.Build : 0;
            var latBuild = lat.Build >= 0 ? lat.Build : 0;
            if (latBuild != curBuild)
            {
                return latBuild > curBuild;
            }

            var curRev = cur.Revision >= 0 ? cur.Revision : 0;
            var latRev = lat.Revision >= 0 ? lat.Revision : 0;
            return latRev > curRev;
        }

        return string.Compare(cleanLatest, cleanCurrent, StringComparison.OrdinalIgnoreCase) > 0;
    }

    //очистка строки версии от префиксов и суффиксов
    private static string CleanVersionString(string ver)
    {
        var trimmed = ver.Trim().TrimStart('v', 'V');
        var dashIdx = trimmed.IndexOf('-');
        if (dashIdx > 0)
        {
            trimmed = trimmed[..dashIdx];
        }

        var parts = trimmed.Split('.');
        return parts.Length == 1 && int.TryParse(parts[0], out _)
            ? $"{parts[0]}.0.0"
            : parts.Length == 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _) ? $"{parts[0]}.{parts[1]}.0" : trimmed;
    }
}
