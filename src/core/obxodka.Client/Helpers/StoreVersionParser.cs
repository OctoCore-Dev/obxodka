namespace obxodka.Helpers;

public static partial class StoreVersionParser
{
    [GeneratedRegex(@"\[\[\[""(\d+\.\d+\.\d+)""\]\]")]
    private static partial Regex GooglePlayVersionRegex();

    [GeneratedRegex(@"\[\[\[""(\d+\.\d+\.\d+)""\]\].*?""145"":\[null,\[null,""([^""]+)""\]\]", RegexOptions.Singleline)]
    private static partial Regex GooglePlayWhatsNewRegex();

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
