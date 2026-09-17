namespace obxodka.Client.Tests.Security;

public class ReleaseIntegrityTests
{
    private static readonly Dictionary<string, (string[] RequiredTokens, string Description)> t_criticalLibraries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["CommunityToolkit.Maui.MediaElement"] = (["media3", "exoplayer"], "MediaElement / ExoPlayer video engine"),
            ["SkiaSharp.Views.Maui.Controls"] = (["skiasharp", "com.google.skia"], "SkiaSharp 2D vector graphics"),
            ["Plugin.InAppBilling"] = (["billing"], "Google Play In-App Billing / Subscriptions"),
            ["Microsoft.Maui.Controls"] = (["crc64", "com.microsoft.maui"], ".NET MAUI Core VisualElement and JNI Handlers"),
            ["Xamarin.AndroidX"] = (["androidx"], "AndroidX UI and Architecture Components"),
            ["AathifMahir.Maui.MauiIcons.Fluent"] = (["crc64"], "Fluent Icon Fonts")
        };

    private static string FindRepoRoot()
    {
        var ghWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");
        if (!string.IsNullOrWhiteSpace(ghWorkspace) && Directory.Exists(ghWorkspace))
        {
            if (File.Exists(Path.Combine(ghWorkspace, "obxodka.slnx")) ||
                File.Exists(Path.Combine(ghWorkspace, "version.props")))
            {
                return ghWorkspace;
            }
        }

        var candidates = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var start in candidates)
        {
            if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
            {
                continue;
            }

            var current = new DirectoryInfo(start);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "obxodka.slnx")) ||
                    File.Exists(Path.Combine(current.FullName, "version.props")) ||
                    File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                {
                    return current.FullName;
                }
                current = current.Parent;
            }
        }

        var defaultPath = @"C:\Users\irovb\Documents\code\obxodka";
        if (Directory.Exists(defaultPath))
        {
            return defaultPath;
        }

        var runnerFallback = @"D:\a\obxodka\obxodka";
        return Directory.Exists(runnerFallback)
            ? runnerFallback
            : throw new DirectoryNotFoundException("Repository root not found from: " + Directory.GetCurrentDirectory());
    }

    [Fact]
    public void SmartProguardDependencyAuditMustProtectAllReferencedLibraries()
    {
        var root = FindRepoRoot();
        var csprojPath = Path.Combine(root, "obxodka.Maui", "obxodka.Maui.csproj");
        Assert.True(File.Exists(csprojPath), $"File not found: {csprojPath}");

        var doc = XDocument.Load(csprojPath);
        var packageRefs = doc.Descendants("PackageReference")
            .Select(x => x.Attribute("Include")?.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .Cast<string>()
            .ToList();

        var proguardCfgPath = Path.Combine(root, "obxodka.Maui", "Platforms", "Android", "proguard.cfg");
        Assert.True(File.Exists(proguardCfgPath), $"[R8 GUARD]: proguard.cfg missing at {proguardCfgPath}");

        var rulesText = File.ReadAllText(proguardCfgPath);

        foreach (var (pkgKey, (tokens, desc)) in t_criticalLibraries)
        {
            var isReferenced = packageRefs.Any(p => p.Contains(pkgKey, StringComparison.OrdinalIgnoreCase));
            if (!isReferenced)
            {
                continue;
            }

            var hasMatch = tokens.Any(token => rulesText.Contains(token, StringComparison.OrdinalIgnoreCase));
            Assert.True(
                hasMatch,
                $"[SMART R8 GUARD FAILED]: Package '{pkgKey}' ({desc}) is referenced in obxodka.Maui.csproj, " +
                $"but Platforms/Android/proguard.cfg is MISSING required protection rules containing '{string.Join("' or '", tokens)}'! " +
                "R8 code shrinker WILL STRIP this library at runtime and crash on user phones! Add '-keep class ...' to proguard.cfg immediately.");
        }
    }

    [Fact]
    public void SmartAndroidManifestPermissionsGuardRejectsUnauthorizedForegroundServices()
    {
        var root = FindRepoRoot();
        var manifestPath = Path.Combine(root, "obxodka.Maui", "Platforms", "Android", "AndroidManifest.xml");
        Assert.True(File.Exists(manifestPath), $"File not found: {manifestPath}");

        var doc = XDocument.Load(manifestPath);
        var permissions = doc.Descendants("uses-permission")
            .Select(x => x.Attributes().FirstOrDefault(a => a.Name.LocalName == "name")?.Value)
            .Where(x => !string.IsNullOrEmpty(x))
            .Cast<string>()
            .ToList();

        foreach (var perm in permissions)
        {
            if (perm.StartsWith("android.permission.FOREGROUND_SERVICE_", StringComparison.OrdinalIgnoreCase))
            {
                var isAllowed = string.Equals(perm, "android.permission.FOREGROUND_SERVICE_SPECIAL_USE", StringComparison.OrdinalIgnoreCase);
                Assert.True(
                    isAllowed,
                    $"[SMART GOOGLE PLAY POLICY GUARD FAILED]: Dangerous permission '{perm}' was found in AndroidManifest.xml! " +
                    "Google Play API strictly halts and blocks deployment for any new foreground service permission without manual Console declaration review. " +
                    "Only 'android.permission.FOREGROUND_SERVICE_SPECIAL_USE' is permitted for the Octopus VPN tunnel.");
            }
        }
    }

    [Fact]
    public void SmartWhatsNewCharacterLimitValidationMustNotExceedGooglePlayQuota()
    {
        var root = FindRepoRoot();
        var whatsNewDir = Path.Combine(root, "distribution", "whatsnew");
        Assert.True(Directory.Exists(whatsNewDir), $"whatsnew directory not found at {whatsNewDir}");

        var ruFile = Path.Combine(whatsNewDir, "whatsnew-ru-RU");
        var enFile = Path.Combine(whatsNewDir, "whatsnew-en-US");

        Assert.True(File.Exists(ruFile), "whatsnew-ru-RU is required for Google Play releases!");
        Assert.True(File.Exists(enFile), "whatsnew-en-US is required for Google Play releases!");

        foreach (var file in Directory.GetFiles(whatsNewDir))
        {
            var text = File.ReadAllText(file).Trim();
            Assert.True(text.Length > 0, $"Release notes file {Path.GetFileName(file)} cannot be empty!");
            Assert.True(
                text.Length <= 500,
                $"[SMART GOOGLE PLAY LIMIT FAILED]: Release notes in '{Path.GetFileName(file)}' have {text.Length} characters! " +
                "Google Play API strictly rejects anything greater than 500 characters with an HTTP 400 error. Shorten the text!");
        }
    }

    [Fact]
    public void SmartVersionPropsValidationMustEnsureMonotonicVersionCode()
    {
        var root = FindRepoRoot();
        var versionPropsPath = Path.Combine(root, "version.props");
        Assert.True(File.Exists(versionPropsPath), $"version.props not found at {versionPropsPath}");

        var doc = XDocument.Load(versionPropsPath);
        var appVersion = doc.Descendants("AppVersion").FirstOrDefault()?.Value;
        var appBuildNumber = doc.Descendants("AppBuildNumber").FirstOrDefault()?.Value;

        Assert.False(string.IsNullOrWhiteSpace(appVersion), "AppVersion in version.props cannot be empty!");
        Assert.False(string.IsNullOrWhiteSpace(appBuildNumber), "AppBuildNumber in version.props cannot be empty!");
        Assert.True(int.TryParse(appBuildNumber, out var buildNum) && buildNum > 0, "AppBuildNumber must be a positive integer!");
    }
}
