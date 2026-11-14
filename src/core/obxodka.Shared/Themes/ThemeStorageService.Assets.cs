namespace obxodka.Shared.Themes;

public sealed partial class ThemeStorageService
{
    //разрешенные видео-расширения для фонов и кнопок
    private static readonly HashSet<string> s_videoExtensions = new([".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v"], StringComparer.OrdinalIgnoreCase);

    //проверка является ли файл видеоформатом
    public static bool IsVideoFile(string? path) =>
        !string.IsNullOrEmpty(path) && s_videoExtensions.Contains(Path.GetExtension(path));

    //безопасное разрешение абсолютного пути к файлу ресурса с защитой от directory traversal
    public static string? ResolveFilePath(string folderPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var cleanRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart('/', '\\');
        var baseDir = Path.GetFullPath(folderPath);
        if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            baseDir += Path.DirectorySeparatorChar;
        }

        var p = Path.GetFullPath(Path.Combine(baseDir, cleanRel.TrimStart('/', '\\')));
        return p.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && File.Exists(p) && ThemeValidator.ValidateAssetFile(p, out _)
            ? p
            : null;
    }

    //разрешение пути к стикеру оформления
    public static string? ResolveStickerPath(string folderPath, Dictionary<string, string> stickers, string key) =>
        stickers.TryGetValue(key, out var rel) && !string.IsNullOrWhiteSpace(rel)
            ? ResolveFilePath(folderPath, rel)
            : null;

    //сбор всех относительных путей файлов ресурсов темы из манифеста
    public static IEnumerable<string> EnumerateAssetPaths(ThemeManifest manifest)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "icon.png" };

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var clean = path.Replace('/', Path.DirectorySeparatorChar).TrimStart('/', '\\');
                if (clean.Length > 0 && !clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    _ = set.Add(clean);
                }
            }
        }

        if (manifest.Background is { } bg)
        {
            Add(bg.ImageSource);
            Add(bg.FallbackImage);
            Add(bg.VideoSource);
        }

        if (manifest.Decorations is { } dec)
        {
            Add(dec.ScreenFrame);
            Add(dec.ScreenFrameMobile);
            Add(dec.CardFrame);
            Add(dec.ButtonRing);
            Add(dec.ButtonImageIdle);
            Add(dec.ButtonImageConnecting);
            Add(dec.ButtonImageActive);
            Add(dec.ButtonImageError);
            Add(dec.ButtonVideoIdle);
            Add(dec.ButtonVideoConnecting);
            Add(dec.ButtonVideoActive);
            Add(dec.ButtonVideoError);
            if (dec.CornerStickers != null)
            {
                foreach (var v in dec.CornerStickers.Values)
                {
                    Add(v);
                }
            }
        }

        if (manifest.Core.Sounds is { } sounds)
        {
            Add(sounds.Connect);
            Add(sounds.Disconnect);
            Add(sounds.Click);
            Add(sounds.Hover);
            Add(sounds.Notification);
            Add(sounds.Ambient);
        }

        if (manifest.Vfx is { } vfx)
        {
            Add(vfx.ParticleSprite);
            Add(vfx.OverlayImage);
            Add(vfx.Physics?.OverlayImage);
            if (vfx.ParticleSprites != null)
            {
                foreach (var sprite in vfx.ParticleSprites)
                {
                    Add(sprite);
                }
            }
        }

        if (manifest.Core.Ui is { } ui)
        {
            Add(ui.FontFile);
        }

        if (manifest.Apps != null &&
            manifest.Apps.TryGetValue("obxodka", out var obxodkaEl) &&
            obxodkaEl.ValueKind == JsonValueKind.Object)
        {
            if (obxodkaEl.TryGetProperty("sounds", out var appSounds) && appSounds.ValueKind == JsonValueKind.Object)
            {
                string[] soundKeys = ["connect", "disconnect", "click", "hover", "notification", "ambient"];
                foreach (var key in soundKeys)
                {
                    if (appSounds.TryGetProperty(key, out var sProp) && sProp.ValueKind == JsonValueKind.String)
                    {
                        Add(sProp.GetString());
                    }
                }
            }

            if (obxodkaEl.TryGetProperty("vpnButton", out var appBtn) && appBtn.ValueKind == JsonValueKind.Object)
            {
                string[] btnKeys = ["idle", "connecting", "active", "error", "idleWebp", "connectingWebp", "activeWebp", "errorWebp"];
                foreach (var key in btnKeys)
                {
                    if (appBtn.TryGetProperty(key, out var bProp) && bProp.ValueKind == JsonValueKind.String)
                    {
                        Add(bProp.GetString());
                    }
                }
            }
        }

        return set;
    }
}
