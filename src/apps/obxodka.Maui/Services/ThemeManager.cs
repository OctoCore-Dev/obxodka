namespace obxodka.Maui.Services;

#pragma warning disable CA1822

public sealed class ThemeAppliedEventArgs : EventArgs
{
    public ThemeManifest Manifest { get; init; } = new();
    public string ThemeFolderPath { get; init; } = string.Empty;
    public string? BackgroundImagePath { get; init; }
    public string? BackgroundVideoPath { get; init; }
    public string? ScreenFramePath { get; init; }
    public FrameSlice? FrameSlice { get; init; }
    public string? CardFramePath { get; init; }
    public string? ButtonRingPath { get; init; }
    public string? FontFilePath { get; init; }
    public string? ConnectSoundPath { get; init; }
    public string? DisconnectSoundPath { get; init; }
    public string? ClickSoundPath { get; init; }
    public string? HoverSoundPath { get; init; }
    public string? NotificationSoundPath { get; init; }
    public string? AmbientSoundPath { get; init; }
    public bool HasParticles { get; init; }
    public string? ParticleSpritePath { get; init; }
    public List<string> ParticleSpritePaths { get; init; } = [];
    public string? CornerTopLeftPath { get; init; }
    public string? CornerTopRightPath { get; init; }
    public string? CornerBottomLeftPath { get; init; }
    public string? CornerBottomRightPath { get; init; }
    public string? ButtonImageIdlePath { get; init; }
    public string? ButtonImageConnectingPath { get; init; }
    public string? ButtonImageActivePath { get; init; }
    public string? ButtonImageErrorPath { get; init; }
    public string? ButtonVideoIdlePath { get; init; }
    public string? ButtonVideoConnectingPath { get; init; }
    public string? ButtonVideoActivePath { get; init; }
    public string? ButtonVideoErrorPath { get; init; }
}


public sealed class ThemeManager
{
    private static readonly string[] t_catalogUrls =
    [
        "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/catalog.json",
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json",
        "https://fastly.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json",
        "https://gcore.jsdelivr.net/gh/OctoCore-Dev/themes@main/catalog.json"
    ];

    private static readonly string[] t_themesBaseUrls =
    [
        "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/themes/",
        "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/",
        "https://fastly.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/",
        "https://gcore.jsdelivr.net/gh/OctoCore-Dev/themes@main/themes/"
    ];

    private const string ActiveThemeKey = "ActiveThemeId";
    private const string DefaultThemePreferenceValue = "default";
    private const string ThemeSoundsEnabledKey = "ThemeSoundsEnabled";
    private const string ThemeClickSoundsEnabledKey = "ThemeClickSoundsEnabled";
    private const string ThemeSoundVolumeKey = "ThemeSoundVolume";
    private const string ThemeAmbientEnabledKey = "ThemeAmbientEnabled";
    private const string ThemeParticlesEnabledKey = "ThemeParticlesEnabled";
    private const string ThemeFramesEnabledKey = "ThemeFramesEnabled";
    private const string ThemeParallaxEnabledKey = "ThemeParallaxEnabled";
    private const string ThemeVideoEnabledKey = "ThemeVideoEnabled";
    private const string ThemeCardOpacityKey = "ThemeCardOpacity";
    private const string ThemeAlwaysPlayVideoKey = "ThemeAlwaysPlayVideoEnabled";
    private const string ThemeGlowIntensityKey = "ThemeGlowIntensity";

    private static readonly HttpClient t_httpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OctoCore-Client/1.0 (Windows NT 10.0; Win64; x64)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        return client;
    }

    public event Action<ThemeAppliedEventArgs>? OnThemeApplied;
    public event Action? OnThemeReset;
    public event Action<ThemeCatalogResponse>? OnCatalogUpdated;
    public event Action<bool>? VideoEnabledChanged;
    public event Action<bool>? SoundsEnabledChanged;
    public event Action<bool>? ClickSoundsEnabledChanged;
    public event Action<double>? SoundVolumeChanged;
    public event Action<bool>? AmbientEnabledChanged;
    public event Action<bool>? ParticlesEnabledChanged;
    public event Action<bool>? FramesEnabledChanged;
    public event Action<bool>? ParallaxEnabledChanged;
    public event Action<bool>? AlwaysPlayVideoChanged;
    public event Action<double>? CardOpacityChanged;
    public event Action<double>? GlowIntensityChanged;
    public event EventHandler<(string ThemeId, double Progress, bool IsCompleted, bool Success, string? Error)>? ThemeDownloadProgressChanged;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<bool>> _activeDownloads = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _downloadProgress = new();

    private Color _baseBgSurface = Color.FromArgb("#161622");
    private Color _baseBgElevated = Color.FromArgb("#1E1E2E");
    private Color _baseBgInput = Color.FromArgb("#12121C");

    public ThemeManifest? ActiveTheme { get; private set; }
    public string? ActiveThemeFolderPath { get; private set; }
    public string? LastError { get; private set; }

    public bool SoundsEnabled
    {
        get => Preferences.Default.Get(ThemeSoundsEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeSoundsEnabledKey, value);
            if (!value)
            {
                ThemeAudioService.UpdateAmbientState(false);
            }
            SoundsEnabledChanged?.Invoke(value);
        }
    }

    public bool ClickSoundsEnabled
    {
        get => Preferences.Default.Get(ThemeClickSoundsEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeClickSoundsEnabledKey, value);
            ClickSoundsEnabledChanged?.Invoke(value);
        }
    }

    private double? _cachedSoundVolume;

    public double SoundVolume
    {
        get => _cachedSoundVolume ??= Math.Clamp(Preferences.Default.Get(ThemeSoundVolumeKey, 0.85), 0.0, 1.0);
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            if (_cachedSoundVolume.HasValue && Math.Abs(_cachedSoundVolume.Value - clamped) < 0.005)
            {
                return;
            }

            _cachedSoundVolume = clamped;
            Preferences.Default.Set(ThemeSoundVolumeKey, clamped);
            ThemeAudioService.SetVolume(clamped);
            SoundVolumeChanged?.Invoke(clamped);
        }
    }

    public bool AmbientEnabled
    {
        get => Preferences.Default.Get(ThemeAmbientEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeAmbientEnabledKey, value);
            if (!value)
            {
                ThemeAudioService.UpdateAmbientState(false);
            }
            AmbientEnabledChanged?.Invoke(value);
        }
    }

    public bool ParticlesEnabled
    {
        get => Preferences.Default.Get(ThemeParticlesEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeParticlesEnabledKey, value);
            ParticlesEnabledChanged?.Invoke(value);
        }
    }

    public bool FramesEnabled
    {
        get => Preferences.Default.Get(ThemeFramesEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeFramesEnabledKey, value);
            FramesEnabledChanged?.Invoke(value);
        }
    }

    public bool ParallaxEnabled
    {
        get => Preferences.Default.Get(ThemeParallaxEnabledKey, false);
        set
        {
            Preferences.Default.Set(ThemeParallaxEnabledKey, value);
            ParallaxEnabledChanged?.Invoke(value);
        }
    }

    public bool VideoEnabled
    {
        get => Preferences.Default.Get(ThemeVideoEnabledKey, true);
        set
        {
            Preferences.Default.Set(ThemeVideoEnabledKey, value);
            VideoEnabledChanged?.Invoke(value);
        }
    }

    public bool AlwaysPlayVideoEnabled
    {
        get => Preferences.Default.Get(ThemeAlwaysPlayVideoKey, false);
        set
        {
            Preferences.Default.Set(ThemeAlwaysPlayVideoKey, value);
            AlwaysPlayVideoChanged?.Invoke(value);
        }
    }

    private double? _cachedCardOpacity;
    private int _opacitySaveSequence;
    private long _lastApplyOpacityTicks;

    public double CardOpacity
    {
        get => _cachedCardOpacity ??= Preferences.Default.Get(ThemeCardOpacityKey, 0.85);
        set
        {
            var clamped = Math.Clamp(value, 0.20, 1.00);
            if (_cachedCardOpacity.HasValue && Math.Abs(_cachedCardOpacity.Value - clamped) < 0.005)
            {
                return;
            }

            _cachedCardOpacity = clamped;

            var seq = Interlocked.Increment(ref _opacitySaveSequence);
            _ = Task.Run(async () =>
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (Volatile.Read(ref _opacitySaveSequence) == seq)
                {
                    Preferences.Default.Set(ThemeCardOpacityKey, clamped);
                    ApplyCardOpacity(clamped, forceImmediate: true);
                }
            });

            ApplyCardOpacity(clamped);
        }
    }

    private double? _cachedGlowIntensity;

    public double GlowIntensity
    {
        get => _cachedGlowIntensity ??= Preferences.Default.Get(ThemeGlowIntensityKey, 1.0);
        set
        {
            var clamped = Math.Clamp(value, 0.0, 2.0);
            if (_cachedGlowIntensity.HasValue && Math.Abs(_cachedGlowIntensity.Value - clamped) < 0.01)
            {
                return;
            }

            _cachedGlowIntensity = clamped;
            Preferences.Default.Set(ThemeGlowIntensityKey, clamped);
            GlowIntensityChanged?.Invoke(clamped);
        }
    }

    public bool IsThemeDownloading(string themeId, out double progress)
    {
        if (_activeDownloads.ContainsKey(themeId))
        {
            progress = _downloadProgress.TryGetValue(themeId, out var p) ? p : 0.0;
            return true;
        }

        progress = 0.0;
        return false;
    }

    private readonly Dictionary<string, object> _defaultResourceSnapshot = [];

    public string ThemesDirectory => Path.Combine(FileSystem.AppDataDirectory, "themes");

    public static ThemeManager? Instance { get; private set; }
    public static double CurrentGlowIntensity => Instance?.GlowIntensity ?? Preferences.Default.Get(ThemeGlowIntensityKey, 1.0);

    public ThemeManager()
    {
        Instance = this;
        _ = Directory.CreateDirectory(ThemesDirectory);
        CaptureDefaultResourcesSnapshot();
    }

    public void EnsureDefaultResourcesCaptured()
    {
        if (_defaultResourceSnapshot.Count > 0)
        {
            return;
        }

        if (MainThread.IsMainThread)
        {
            CaptureResourcesInternal();
        }
        else
        {
            try
            {
                _ = MainThread.InvokeOnMainThreadAsync(CaptureResourcesInternal).Wait(TimeSpan.FromSeconds(2));
            }
            catch
            {
            }
        }
    }

    private void CaptureDefaultResourcesSnapshot()
    {
        if (MainThread.IsMainThread)
        {
            CaptureResourcesInternal();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(CaptureResourcesInternal);
        }
    }

    private void CaptureResourcesInternal()
    {
        var res = Application.Current?.Resources;
        if (res == null || _defaultResourceSnapshot.Count > 0)
        {
            return;
        }

        string[] trackedKeys =
        [
            "Primary", "PrimaryBrush", "PrimaryBright", "PrimaryBrightBrush", "PrimaryLight", "PrimaryLight2", "PrimaryDim", "PrimaryGlow",
            "Accent", "AccentBrush", "AccentDim", "AccentGlow", "AccentLight",
            "Purple", "PurpleDim", "PurpleLight", "Pink",
            "BgBase", "BgBaseBrush", "BgSurface", "BgSurfaceBrush", "BgElevated", "BgInput", "BgOverlay",
            "TextPrimary", "TextPrimaryBrush", "TextSecondary", "TextMuted", "TextOnPrimary",
            "SolidBorderDark", "BorderSubtle", "BorderSubtleBrush", "BorderMedium", "BorderStrong",
            "Success", "SuccessBrush", "SuccessDim",
            "Warning", "WarningDim",
            "Error", "ErrorBrush", "ErrorDim",
            "AppFontRegular", "AppFontMedium", "AppFontBold", "AppFontTitle",
            "CardCornerRadius", "CardBorderThickness"
        ];

        foreach (var key in trackedKeys)
        {
            if (res.TryGetValue(key, out var val) && val is not null)
            {
                _defaultResourceSnapshot[key] = val is SolidColorBrush brush
                    ? new SolidColorBrush(brush.Color)
                    : val;
            }
        }
    }

    public static IEnumerable<string> EnumerateAssetPaths(ThemeManifest manifest)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "icon.png" };

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                var clean = path.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
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

    public static string ToMirrorUrl(string? rawUrl)
    {
        return string.IsNullOrWhiteSpace(rawUrl)
            ? string.Empty
            : rawUrl.StartsWith("https://raw.githubusercontent.com/OctoCore-Dev/themes/main/", StringComparison.OrdinalIgnoreCase)
            ? rawUrl.Replace(
                "https://raw.githubusercontent.com/OctoCore-Dev/themes/main/",
                "https://cdn.jsdelivr.net/gh/OctoCore-Dev/themes@main/",
                StringComparison.OrdinalIgnoreCase)
            : rawUrl;
    }

    private static void NormalizeCatalogUrls(ThemeCatalogResponse catalog)
    {
        foreach (var theme in catalog.Themes)
        {
            theme.IconUrl = ToMirrorUrl(theme.IconUrl);
            theme.PreviewUrl = ToMirrorUrl(theme.PreviewUrl);
        }
    }

    private static async Task<ThemeCatalogResponse?> FetchCatalogFromMirrorsAsync(CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        foreach (var url in t_catalogUrls)
        {
            try
            {
                var separator = url.Contains('?') ? "&" : "?";
                var urlWithBuster = $"{url}{separator}_t={timestamp}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(8));
                using var req = new HttpRequestMessage(HttpMethod.Get, urlWithBuster);
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };
                using var res = await t_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    var catalog = await res.Content.ReadFromJsonAsync(ThemeJsonContext.Default.ThemeCatalogResponse, cts.Token);
                    if (catalog != null && catalog.Themes.Count > 0)
                    {
                        return catalog;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeManager] Mirror {url} failed: {ex.Message}");
            }
        }

        return null;
    }

    public async Task<ThemeCatalogResponse?> LoadCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var cachePath = Path.Combine(FileSystem.AppDataDirectory, "theme_catalog_cache.json");

        if (forceRefresh && File.Exists(cachePath))
        {
            try
            {
                File.Delete(cachePath);
            }
            catch
            {
            }
        }

        var isCacheStale = !File.Exists(cachePath) || (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)) > TimeSpan.FromMinutes(5);

        if (!forceRefresh && !isCacheStale && File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath, ct);
                var cachedCatalog = JsonSerializer.Deserialize(cachedJson, ThemeJsonContext.Default.ThemeCatalogResponse);
                if (cachedCatalog?.Themes.Count > 0)
                {
                    NormalizeCatalogUrls(cachedCatalog);

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var fresh = await FetchCatalogFromMirrorsAsync(CancellationToken.None);
                            if (fresh != null && fresh.Themes.Count > 0)
                            {
                                NormalizeCatalogUrls(fresh);
                                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(fresh, ThemeJsonContext.Default.ThemeCatalogResponse), CancellationToken.None);
                                MainThread.BeginInvokeOnMainThread(() => OnCatalogUpdated?.Invoke(fresh));
                            }
                        }
                        catch
                        {
                        }
                    }, CancellationToken.None);

                    return cachedCatalog;
                }
            }
            catch
            {
            }
        }

        try
        {
            var catalog = await FetchCatalogFromMirrorsAsync(ct);
            if (catalog != null && catalog.Themes.Count > 0)
            {
                NormalizeCatalogUrls(catalog);
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(catalog, ThemeJsonContext.Default.ThemeCatalogResponse), ct);
                return catalog;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Error fetching catalog from mirrors: {ex.Message}");
        }

        if (File.Exists(cachePath))
        {
            try
            {
                var cachedJson = await File.ReadAllTextAsync(cachePath, ct);
                var cachedCatalog = JsonSerializer.Deserialize(cachedJson, ThemeJsonContext.Default.ThemeCatalogResponse);
                if (cachedCatalog?.Themes.Count > 0)
                {
                    NormalizeCatalogUrls(cachedCatalog);
                    return cachedCatalog;
                }
            }
            catch
            {
            }
        }

        return new ThemeCatalogResponse
        {
            SchemaVersion = "4.0.0",
            TotalThemes = 0,
            Themes = []
        };
    }

    public List<ThemeManifest> GetInstalledThemes()
    {
        var list = new List<ThemeManifest>();
        if (!Directory.Exists(ThemesDirectory))
        {
            return list;
        }

        foreach (var dir in Directory.GetDirectories(ThemesDirectory))
        {
            var manifestPath = Path.Combine(dir, "theme.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var json = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
                if (manifest != null && ThemeValidator.ValidateManifest(manifest, out _))
                {
                    list.Add(manifest);
                }
            }
            catch
            {
            }
        }

        return list;
    }

    public bool IsThemeInstalled(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        var targetDir = Path.Combine(ThemesDirectory, themeId);
        return File.Exists(Path.Combine(targetDir, "theme.json"));
    }

    public static bool IsVersionNewer(string? currentVersion, string? remoteVersion) =>
        ThemeValidator.IsVersionNewer(currentVersion, remoteVersion);

    public string? GetInstalledThemeVersion(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return null;
        }

        var manifestPath = Path.Combine(ThemesDirectory, themeId, "theme.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
            return manifest?.Version;
        }
        catch
        {
            return null;
        }
    }

    public bool IsUpdateAvailable(string themeId, string? remoteVersion)
    {
        var localVersion = GetInstalledThemeVersion(themeId);
        return localVersion != null && IsVersionNewer(localVersion, remoteVersion);
    }

    public bool ApplyThemeById(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        var manifestPath = Path.Combine(ThemesDirectory, themeId, "theme.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
            return manifest != null && ApplyTheme(manifest);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Error applying theme {themeId}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ApplyThemeByIdAsync(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        var manifestPath = Path.Combine(ThemesDirectory, themeId, "theme.json");
        if (!File.Exists(manifestPath))
        {
            return false;
        }

        try
        {
            var json = await File.ReadAllTextAsync(manifestPath).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
            return manifest != null && await ApplyThemeAsync(manifest).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Error applying theme {themeId}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> ApplyThemeAsync(ThemeManifest manifest) =>
        manifest != null &&
        ThemeValidator.ValidateManifest(manifest, out _) &&
        (MainThread.IsMainThread
            ? ApplyTheme(manifest)
            : await MainThread.InvokeOnMainThreadAsync(() => ApplyTheme(manifest)).ConfigureAwait(false));

    public async Task ResetToDefaultAsync()
    {
        if (MainThread.IsMainThread)
        {
            ResetToDefault();
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(ResetToDefault).ConfigureAwait(false);
    }

    public bool DeleteTheme(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        try
        {
            if (ActiveTheme?.Id == themeId)
            {
                ResetToDefault();
            }

            var targetDir = Path.Combine(ThemesDirectory, themeId);
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Error deleting theme {themeId}: {ex.Message}");
            return false;
        }
    }

    public Task<bool> DownloadAndInstallThemeAsync(string themeId, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!ThemeValidator.IsSafeId(themeId))
        {
            LastError = $"[ThemeSecurity] Недопустимый идентификатор темы: '{themeId}'";
            return Task.FromResult(false);
        }

        lock (_activeDownloads)
        {
            if (_activeDownloads.TryGetValue(themeId, out var runningTask))
            {
                return runningTask;
            }

            var task = RunDownloadAndInstallThemeInternalAsync(themeId, progress, ct);
            _activeDownloads[themeId] = task;
            return task;
        }
    }

    private async Task<bool> RunDownloadAndInstallThemeInternalAsync(string themeId, IProgress<double>? progress, CancellationToken ct)
    {
        var targetDir = Path.Combine(ThemesDirectory, themeId);
        _ = Directory.CreateDirectory(targetDir);

        void Report(double p)
        {
            _downloadProgress[themeId] = p;
            progress?.Report(p);
            MainThread.BeginInvokeOnMainThread(() => ThemeDownloadProgressChanged?.Invoke(this, (themeId, p, false, false, null)));
        }

        var success = false;
        try
        {
            Report(0.1);

            var manifestJson = await DownloadStringWithMirrorsAsync(themeId, "theme.json", ct);
            var manifest = JsonSerializer.Deserialize(manifestJson, ThemeJsonContext.Default.ThemeManifest);

            string? validationError = null;
            if (manifest == null || !ThemeValidator.ValidateManifest(manifest, out validationError))
            {
                throw new SecurityException($"Invalid theme manifest: {validationError ?? "Manifest is empty"}");
            }
            Report(0.3);

            var assetPaths = EnumerateAssetPaths(manifest).ToList();
            var totalAssets = assetPaths.Count;
            var processed = 0;

            foreach (var rel in assetPaths)
            {
                var isIcon = string.Equals(rel, "icon.png", StringComparison.OrdinalIgnoreCase);
                var dest = Path.Combine(targetDir, rel);
                var parentDir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                {
                    _ = Directory.CreateDirectory(parentDir);
                }

                await DownloadThemeAssetWithMirrorsAsync(themeId, rel, dest, ct, isRequired: isIcon).ConfigureAwait(false);
                processed++;
                Report(0.3 + (0.65 * processed / Math.Max(1, totalAssets)));
            }

            await File.WriteAllTextAsync(Path.Combine(targetDir, "theme.json"), manifestJson, ct);

            Report(1.0);
            success = true;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Debug.WriteLine($"[ThemeManager] Install failed for {themeId}: {ex}");
            Console.Error.WriteLine($"[ThemeManager] Install failed for {themeId}: {ex}");
            if (Directory.Exists(targetDir))
            {
                try
                {
                    Directory.Delete(targetDir, true);
                }
                catch
                {
                }
            }
            return false;
        }
        finally
        {
            lock (_activeDownloads)
            {
                _ = _activeDownloads.TryRemove(themeId, out _);
                _ = _downloadProgress.TryRemove(themeId, out _);
            }

            var finalSuccess = success;
            var finalErr = success ? null : LastError;
            MainThread.BeginInvokeOnMainThread(() => ThemeDownloadProgressChanged?.Invoke(this, (themeId, 1.0, true, finalSuccess, finalErr)));
        }
    }

    private static async Task<string> DownloadStringWithMirrorsAsync(string themeId, string relativePath, CancellationToken ct)
    {
        var cleanRelPath = relativePath.Replace('\\', '/').TrimStart('/');
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Exception? lastEx = null;
        foreach (var baseUrl in t_themesBaseUrls)
        {
            try
            {
                var separator = cleanRelPath.Contains('?') ? "&" : "?";
                var url = $"{baseUrl}{themeId}/{cleanRelPath}{separator}_t={timestamp}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };
                using var res = await t_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (res.IsSuccessStatusCode)
                {
                    return await res.Content.ReadAsStringAsync(cts.Token);
                }
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        throw lastEx ?? new InvalidOperationException($"Не удалось загрузить '{cleanRelPath}' темы '{themeId}' со всех зеркал.");
    }

    private static async Task DownloadThemeAssetWithMirrorsAsync(
        string themeId,
        string relativePath,
        string destinationPath,
        CancellationToken ct,
        bool isRequired = true)
    {
        if (!ThemeValidator.IsSafeRelativePath(relativePath))
        {
            throw new SecurityException($"[ThemeSecurity] Блокировка: небезопасный относительный путь ресурса '{relativePath}'");
        }

        var cleanRelPath = relativePath.Replace('\\', '/').TrimStart('/');
        var ext = Path.GetExtension(cleanRelPath);
        if (!SafeThemeExtractor.IsAllowedExtension(ext))
        {
            throw new SecurityException($"[ThemeSecurity] Блокировка: запрещенное расширение '{ext}' у ресурса '{cleanRelPath}'");
        }

        var fullDest = Path.GetFullPath(destinationPath);
        var parentDir = Path.GetDirectoryName(fullDest);
        if (!string.IsNullOrEmpty(parentDir))
        {
            _ = Directory.CreateDirectory(parentDir);
        }

        var maxAllowedSize = SafeThemeExtractor.GetMaxFileSize(ext);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Exception? lastEx = null;

        foreach (var baseUrl in t_themesBaseUrls)
        {
            try
            {
                var separator = cleanRelPath.Contains('?') ? "&" : "?";
                var url = $"{baseUrl}{themeId}/{cleanRelPath}{separator}_t={timestamp}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(15));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };

                using var response = await t_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                if (response.Content.Headers.ContentLength is { } len && len > maxAllowedSize)
                {
                    throw new SecurityException($"[ThemeSecurity] Размер ресурса '{cleanRelPath}' ({len} байт) превышает лимит {maxAllowedSize} байт!");
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
                if (!ThemeValidator.ValidateAssetData(ext, bytes, out var valError))
                {
                    throw new SecurityException($"[ThemeSecurity] Ошибка валидации ресурса '{cleanRelPath}': {valError}");
                }

                await File.WriteAllBytesAsync(fullDest, bytes, ct);
                return;
            }
            catch (SecurityException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastEx = ex;
            }
        }

        if (isRequired)
        {
            throw lastEx ?? new InvalidOperationException($"Не удалось скачать обязательный ресурс '{relativePath}' темы '{themeId}' со всех доступных зеркал.");
        }
        else
        {
            Debug.WriteLine($"[ThemeManager] Опциональный ресурс '{relativePath}' не удалось загрузить: {lastEx?.Message}");
        }
    }

    private static void SetResourceValue(ResourceDictionary root, string key, object value)
    {
        if (value is SolidColorBrush newBrush && root.TryGetValue(key, out var existing) && existing is SolidColorBrush currentBrush)
        {
            currentBrush.Color = newBrush.Color;
        }
        root[key] = value;

        void UpdateDict(ResourceDictionary dict)
        {
            if (dict.ContainsKey(key))
            {
                if (value is SolidColorBrush nb && dict.TryGetValue(key, out var ex) && ex is SolidColorBrush cb)
                {
                    cb.Color = nb.Color;
                }
                dict[key] = value;
            }

            foreach (var md in dict.MergedDictionaries)
            {
                UpdateDict(md);
            }
        }

        foreach (var md in root.MergedDictionaries)
        {
            UpdateDict(md);
        }
    }

    private void InjectThemeResources(ThemeManifest manifest)
    {
        try
        {
            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                return;
            }

            Color? primaryColor = null;
            if (Color.TryParse(manifest.Core.Colors.Primary, out var parsedPrimary))
            {
                primaryColor = parsedPrimary;
                SetResourceValue(appResources, "Primary", parsedPrimary);
                SetResourceValue(appResources, "PrimaryBrush", new SolidColorBrush(parsedPrimary));
                SetResourceValue(appResources, "PrimaryLight", parsedPrimary.AddLuminosity(0.12f));
                SetResourceValue(appResources, "PrimaryLight2", parsedPrimary);
                SetResourceValue(appResources, "PrimaryDim", parsedPrimary.WithAlpha(0.22f));
                SetResourceValue(appResources, "PrimaryGlow", parsedPrimary.WithAlpha(0.35f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.PrimaryBright) && Color.TryParse(manifest.Core.Colors.PrimaryBright, out var primaryBright))
            {
                SetResourceValue(appResources, "PrimaryBright", primaryBright);
                SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(primaryBright));
            }
            else if (primaryColor != null)
            {
                SetResourceValue(appResources, "PrimaryBright", primaryColor);
                SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(primaryColor));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.PrimaryDim) && Color.TryParse(manifest.Core.Colors.PrimaryDim, out var parsedPrimaryDim))
            {
                SetResourceValue(appResources, "PrimaryDim", parsedPrimaryDim);
            }

            Color? accentColor = null;
            if (!string.IsNullOrEmpty(manifest.Core.Colors.Accent) && Color.TryParse(manifest.Core.Colors.Accent, out var parsedAccent))
            {
                accentColor = parsedAccent;
                SetResourceValue(appResources, "Accent", parsedAccent);
                SetResourceValue(appResources, "AccentBrush", new SolidColorBrush(parsedAccent));
                SetResourceValue(appResources, "AccentDim", parsedAccent.WithAlpha(0.25f));
                SetResourceValue(appResources, "AccentGlow", parsedAccent.WithAlpha(0.40f));
                SetResourceValue(appResources, "AccentLight", parsedAccent.AddLuminosity(0.12f));
            }
            else if (primaryColor != null)
            {
                accentColor = primaryColor;
                SetResourceValue(appResources, "Accent", primaryColor);
                SetResourceValue(appResources, "AccentBrush", new SolidColorBrush(primaryColor));
                SetResourceValue(appResources, "AccentDim", primaryColor.WithAlpha(0.25f));
                SetResourceValue(appResources, "AccentGlow", primaryColor.WithAlpha(0.40f));
                SetResourceValue(appResources, "AccentLight", primaryColor.AddLuminosity(0.12f));
            }

            if (primaryColor is not null)
            {
                var stop2 = accentColor ?? primaryColor.AddLuminosity(0.18f);
                var gradStops = new GradientStopCollection
                {
                    new GradientStop(primaryColor, 0.0f),
                    new GradientStop(stop2, 1.0f)
                };
                if (manifest.Core.Gradients?.PrimaryGradient?.Stops is { Count: >= 2 } customStops)
                {
                    gradStops.Clear();
                    for (var i = 0; i < customStops.Count; i++)
                    {
                        if (Color.TryParse(customStops[i], out var sc))
                        {
                            var offset = (float)i / (customStops.Count - 1);
                            gradStops.Add(new GradientStop(sc, offset));
                        }
                    }
                }
                SetResourceValue(appResources, "PrimaryGradientBrush", new LinearGradientBrush(gradStops, new Point(0, 0), new Point(1, 1)));
            }

            if (Color.TryParse(manifest.Core.Colors.BgBase, out var parsedBgBase))
            {
                SetResourceValue(appResources, "BgBase", parsedBgBase);
                SetResourceValue(appResources, "BgBaseBrush", new SolidColorBrush(parsedBgBase));
                SetResourceValue(appResources, "BgOverlay", parsedBgBase.WithAlpha(0.70f));
            }

            var rawSurface = !string.IsNullOrEmpty(manifest.Core.Colors.BgSurface) && Color.TryParse(manifest.Core.Colors.BgSurface, out var parsedSurface)
                ? parsedSurface
                : Color.FromArgb("#161622");
            _baseBgSurface = rawSurface;

            var userOpacity = CardOpacity;
            var bgSurfaceColor = rawSurface.WithAlpha((float)userOpacity);
            SetResourceValue(appResources, "BgSurface", bgSurfaceColor);
            SetResourceValue(appResources, "BgSurfaceBrush", new SolidColorBrush(bgSurfaceColor));

            var surfaceStops = new GradientStopCollection
            {
                new GradientStop(bgSurfaceColor, 0.0f),
                new GradientStop(bgSurfaceColor.AddLuminosity(0.06f), 1.0f)
            };
            if (manifest.Core.Gradients?.SurfaceGradient?.Stops is { Count: >= 2 } surfCustomStops)
            {
                surfaceStops.Clear();
                for (var i = 0; i < surfCustomStops.Count; i++)
                {
                    if (Color.TryParse(surfCustomStops[i], out var ssc))
                    {
                        var offset = (float)i / (surfCustomStops.Count - 1);
                        surfaceStops.Add(new GradientStop(ssc, offset));
                    }
                }
            }
            SetResourceValue(appResources, "SurfaceGradientBrush", new LinearGradientBrush(surfaceStops, new Point(0, 0), new Point(0, 1)));

            var rawElevated = !string.IsNullOrEmpty(manifest.Core.Colors.BgElevated) && Color.TryParse(manifest.Core.Colors.BgElevated, out var bgElevatedColor)
                ? bgElevatedColor
                : rawSurface.AddLuminosity(0.08f);
            _baseBgElevated = rawElevated;
            SetResourceValue(appResources, "BgElevated", rawElevated.WithAlpha((float)Math.Clamp(userOpacity + 0.08, 0.25, 1.0)));

            var rawInput = !string.IsNullOrEmpty(manifest.Core.Colors.BgInput) && Color.TryParse(manifest.Core.Colors.BgInput, out var bgInputColor)
                ? bgInputColor
                : rawSurface.WithAlpha(0.55f);
            _baseBgInput = rawInput;
            SetResourceValue(appResources, "BgInput", rawInput);

            Color? textPrimary = null;
            if (Color.TryParse(manifest.Core.Colors.TextPrimary, out var textPrimaryColor))
            {
                textPrimary = textPrimaryColor;
                SetResourceValue(appResources, "TextPrimary", textPrimaryColor);
                SetResourceValue(appResources, "TextPrimaryBrush", new SolidColorBrush(textPrimaryColor));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.TextSecondary) && Color.TryParse(manifest.Core.Colors.TextSecondary, out var textSecondaryColor))
            {
                SetResourceValue(appResources, "TextSecondary", textSecondaryColor);
                SetResourceValue(appResources, "TextMuted", textSecondaryColor);
            }
            else if (textPrimary != null)
            {
                SetResourceValue(appResources, "TextSecondary", textPrimary.WithAlpha(0.75f));
                SetResourceValue(appResources, "TextMuted", textPrimary.WithAlpha(0.55f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.TextMuted) && Color.TryParse(manifest.Core.Colors.TextMuted, out var textMutedColor))
            {
                SetResourceValue(appResources, "TextMuted", textMutedColor);
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.SolidBorderDark) && Color.TryParse(manifest.Core.Colors.SolidBorderDark, out var parsedBorderDark))
            {
                SetResourceValue(appResources, "SolidBorderDark", parsedBorderDark);
                SetResourceValue(appResources, "BorderSubtle", parsedBorderDark);
                SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(parsedBorderDark));
                SetResourceValue(appResources, "BorderMedium", parsedBorderDark.WithAlpha(0.55f));
                SetResourceValue(appResources, "BorderStrong", parsedBorderDark.AddLuminosity(0.10f));
            }
            else if (primaryColor != null)
            {
                var subtle = primaryColor.WithAlpha(0.18f);
                SetResourceValue(appResources, "BorderSubtle", subtle);
                SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(subtle));
                SetResourceValue(appResources, "BorderMedium", primaryColor.WithAlpha(0.35f));
                SetResourceValue(appResources, "BorderStrong", primaryColor.WithAlpha(0.50f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.BorderSubtle) && Color.TryParse(manifest.Core.Colors.BorderSubtle, out var borderSubtleColor))
            {
                SetResourceValue(appResources, "BorderSubtle", borderSubtleColor);
                SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(borderSubtleColor));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.BorderMedium) && Color.TryParse(manifest.Core.Colors.BorderMedium, out var borderMediumColor))
            {
                SetResourceValue(appResources, "BorderMedium", borderMediumColor);
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.Success) && Color.TryParse(manifest.Core.Colors.Success, out var successColor))
            {
                SetResourceValue(appResources, "Success", successColor);
                SetResourceValue(appResources, "SuccessBrush", new SolidColorBrush(successColor));
                SetResourceValue(appResources, "SuccessDim", successColor.WithAlpha(0.15f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.Warning) && Color.TryParse(manifest.Core.Colors.Warning, out var warningColor))
            {
                SetResourceValue(appResources, "Warning", warningColor);
                SetResourceValue(appResources, "WarningDim", warningColor.WithAlpha(0.15f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.Error) && Color.TryParse(manifest.Core.Colors.Error, out var errorColor))
            {
                SetResourceValue(appResources, "Error", errorColor);
                SetResourceValue(appResources, "ErrorBrush", new SolidColorBrush(errorColor));
                SetResourceValue(appResources, "ErrorDim", errorColor.WithAlpha(0.15f));
            }

            var purpleColor = accentColor ?? primaryColor ?? Color.FromArgb("#9F6FF0");
            SetResourceValue(appResources, "Purple", purpleColor);
            SetResourceValue(appResources, "PurpleDim", purpleColor.WithAlpha(0.20f));
            SetResourceValue(appResources, "PurpleLight", purpleColor.AddLuminosity(0.10f));
            SetResourceValue(appResources, "Pink", accentColor ?? Color.FromArgb("#EC4899"));

            var cornerRadius = Math.Clamp(manifest.Core.Ui?.CornerRadius ?? 12, 2, 36);
            var borderThickness = Math.Clamp(manifest.Core.Ui?.BorderThickness ?? 1.0, 0.5, 4.0);
            SetResourceValue(appResources, "CardCornerRadius", new CornerRadius(cornerRadius));
            SetResourceValue(appResources, "CardBorderThickness", borderThickness);

            SetResourceValue(appResources, "AppFontRegular", !string.IsNullOrWhiteSpace(manifest.Core.Ui?.FontRegular)
                ? manifest.Core.Ui.FontRegular
                : "RobotoRegular");
            SetResourceValue(appResources, "AppFontMedium", !string.IsNullOrWhiteSpace(manifest.Core.Ui?.FontMedium)
                ? manifest.Core.Ui.FontMedium
                : "RobotoMedium");
            SetResourceValue(appResources, "AppFontBold", !string.IsNullOrWhiteSpace(manifest.Core.Ui?.FontBold)
                ? manifest.Core.Ui.FontBold
                : "RobotoBold");
            SetResourceValue(appResources, "AppFontTitle", !string.IsNullOrWhiteSpace(manifest.Core.Ui?.FontTitle)
                ? manifest.Core.Ui.FontTitle
                : "CommissionerExtraBold");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Resource injection error: {ex.Message}");
        }
    }

    private static string? GetObxodkaNestedProperty(ThemeManifest manifest, string sectionKey, string propKey) =>
        manifest.Apps != null &&
        manifest.Apps.TryGetValue("obxodka", out var obxodkaEl) &&
        obxodkaEl.ValueKind == JsonValueKind.Object &&
        obxodkaEl.TryGetProperty(sectionKey, out var sectionEl) &&
        sectionEl.ValueKind == JsonValueKind.Object &&
        sectionEl.TryGetProperty(propKey, out var propEl) &&
        propEl.ValueKind == JsonValueKind.String
            ? propEl.GetString()
            : null;

    public bool ApplyTheme(ThemeManifest manifest)
    {
        if (manifest == null || !ThemeValidator.ValidateManifest(manifest, out _))
        {
            return false;
        }

        var folderPath = Path.Combine(ThemesDirectory, manifest.Id);

        if (MainThread.IsMainThread)
        {
            InjectThemeResources(manifest);
        }
        else
        {
            try
            {
                MainThread.InvokeOnMainThreadAsync(() => InjectThemeResources(manifest)).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        ActiveTheme = manifest;
        ActiveThemeFolderPath = folderPath;
        Preferences.Default.Set(ActiveThemeKey, manifest.Id);

        string? bgPath = null;
        string? backgroundVideoPath = null;

        void RegisterBackgroundCandidate(string? rel)
        {
            var resolved = ResolveFilePath(folderPath, rel);
            if (resolved is null)
            {
                return;
            }

            if (IsVideoFile(resolved))
            {
                backgroundVideoPath ??= resolved;
            }
            else
            {
                bgPath ??= resolved;
            }
        }

        RegisterBackgroundCandidate(manifest.Background?.VideoSource);
        RegisterBackgroundCandidate(manifest.Background?.ImageSource);
        RegisterBackgroundCandidate(manifest.Background?.FallbackImage);

        string? framePath = null;
        FrameSlice? activeSlice = null;

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            if (!string.IsNullOrEmpty(manifest.Decorations?.ScreenFrameMobile))
            {
                framePath = ResolveFilePath(folderPath, manifest.Decorations.ScreenFrameMobile);
                activeSlice = manifest.Decorations.FrameSliceMobile ?? manifest.Decorations.FrameSlice;
            }
            else if (!string.IsNullOrEmpty(manifest.Decorations?.ScreenFrame))
            {
                framePath = ResolveFilePath(folderPath, manifest.Decorations.ScreenFrame);
                activeSlice = manifest.Decorations.FrameSlice;
            }
        }
        else
        {
            framePath = ResolveFilePath(folderPath, manifest.Decorations?.ScreenFrame);
            activeSlice = manifest.Decorations?.FrameSlice;
        }

        var cardFramePath = ResolveFilePath(folderPath, manifest.Decorations?.CardFrame);
        var buttonRingPath = ResolveFilePath(folderPath, manifest.Decorations?.ButtonRing);
        var fontFilePath = ResolveFilePath(folderPath, manifest.Core.Ui?.FontFile);

        var connectSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Connect ?? GetObxodkaNestedProperty(manifest, "sounds", "connect"));
        var disconnectSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Disconnect ?? GetObxodkaNestedProperty(manifest, "sounds", "disconnect"));
        var clickSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Click ?? GetObxodkaNestedProperty(manifest, "sounds", "click"));
        var hoverSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Hover ?? GetObxodkaNestedProperty(manifest, "sounds", "hover"));
        var notificationSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Notification ?? GetObxodkaNestedProperty(manifest, "sounds", "notification"));
        var ambientSound = ResolveFilePath(folderPath, manifest.Core.Sounds?.Ambient ?? GetObxodkaNestedProperty(manifest, "sounds", "ambient"));

        if (!string.IsNullOrEmpty(clickSound) && !string.IsNullOrEmpty(connectSound) &&
            string.Equals(clickSound, connectSound, StringComparison.OrdinalIgnoreCase))
        {
            var altClick = Path.Combine(folderPath, "assets", "sounds", "click.wav");
            clickSound = File.Exists(altClick) ? altClick : null;
        }
        else if (!string.IsNullOrEmpty(clickSound) && File.Exists(clickSound))
        {
            try
            {
                if (new FileInfo(clickSound).Length > 90000)
                {
                    var altClick = Path.Combine(folderPath, "assets", "sounds", "click.wav");
                    clickSound = File.Exists(altClick) && new FileInfo(altClick).Length <= 90000 ? altClick : null;
                }
            }
            catch
            {
            }
        }

        var particleSpritePaths = new List<string>();
        var singleSprite = ResolveFilePath(folderPath, manifest.Vfx?.ParticleSprite);
        if (!string.IsNullOrEmpty(singleSprite))
        {
            particleSpritePaths.Add(singleSprite);
        }

        if (manifest.Vfx?.ParticleSprites is { Count: > 0 } rawSprites)
        {
            foreach (var rawSprite in rawSprites)
            {
                var resolvedSprite = ResolveFilePath(folderPath, rawSprite);
                if (!string.IsNullOrEmpty(resolvedSprite) && !particleSpritePaths.Contains(resolvedSprite, StringComparer.OrdinalIgnoreCase))
                {
                    particleSpritePaths.Add(resolvedSprite);
                }
            }
        }

        var primaryParticleSprite = singleSprite ?? particleSpritePaths.FirstOrDefault();

        string? cornerTopLeft = null;
        string? cornerTopRight = null;
        string? cornerBottomLeft = null;
        string? cornerBottomRight = null;
        if (manifest.Decorations?.CornerStickers is { Count: > 0 } stickersMap)
        {
            cornerTopLeft = ResolveStickerPath(folderPath, stickersMap, "topLeft");
            cornerTopRight = ResolveStickerPath(folderPath, stickersMap, "topRight");
            cornerBottomLeft = ResolveStickerPath(folderPath, stickersMap, "bottomLeft");
            cornerBottomRight = ResolveStickerPath(folderPath, stickersMap, "bottomRight");
        }

        var buttonImageIdle = ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageIdle ?? GetObxodkaNestedProperty(manifest, "vpnButton", "idle"));
        var buttonImageConnecting = ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageConnecting ?? GetObxodkaNestedProperty(manifest, "vpnButton", "connecting"));
        var buttonImageActive = ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageActive ?? GetObxodkaNestedProperty(manifest, "vpnButton", "active"));
        var buttonImageError = ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageError ?? GetObxodkaNestedProperty(manifest, "vpnButton", "error"));

        var buttonVideoIdle = ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoIdle ?? GetObxodkaNestedProperty(manifest, "vpnButton", "idleWebp"));
        var buttonVideoConnecting = ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoConnecting ?? GetObxodkaNestedProperty(manifest, "vpnButton", "connectingWebp"));
        var buttonVideoActive = ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoActive ?? GetObxodkaNestedProperty(manifest, "vpnButton", "activeWebp"));
        var buttonVideoError = ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoError ?? GetObxodkaNestedProperty(manifest, "vpnButton", "errorWebp"));

        ThemeAudioService.Configure(
            clickSound,
            hoverSound,
            notificationSound,
            connectSound,
            disconnectSound,
            ambientSound,
            SoundVolume);

        var args = new ThemeAppliedEventArgs
        {
            Manifest = manifest,
            ThemeFolderPath = folderPath,
            BackgroundImagePath = bgPath,
            BackgroundVideoPath = backgroundVideoPath,
            ScreenFramePath = framePath,
            FrameSlice = activeSlice,
            CardFramePath = cardFramePath,
            ButtonRingPath = buttonRingPath,
            FontFilePath = fontFilePath,
            ConnectSoundPath = connectSound,
            DisconnectSoundPath = disconnectSound,
            ClickSoundPath = clickSound,
            HoverSoundPath = hoverSound,
            NotificationSoundPath = notificationSound,
            AmbientSoundPath = ambientSound,
            HasParticles = ParticlesEnabled && manifest.Vfx?.Particles != "none",
            ParticleSpritePath = primaryParticleSprite,
            ParticleSpritePaths = particleSpritePaths,
            CornerTopLeftPath = cornerTopLeft,
            CornerTopRightPath = cornerTopRight,
            CornerBottomLeftPath = cornerBottomLeft,
            CornerBottomRightPath = cornerBottomRight,
            ButtonImageIdlePath = buttonImageIdle,
            ButtonImageConnectingPath = buttonImageConnecting,
            ButtonImageActivePath = buttonImageActive,
            ButtonImageErrorPath = buttonImageError,
            ButtonVideoIdlePath = buttonVideoIdle,
            ButtonVideoConnectingPath = buttonVideoConnecting,
            ButtonVideoActivePath = buttonVideoActive,
            ButtonVideoErrorPath = buttonVideoError,
        };

        OnThemeApplied?.Invoke(args);
        return true;
    }

    private static readonly HashSet<string> t_videoExtensions = new([".mp4", ".webm", ".mov", ".mkv", ".avi", ".m4v"], StringComparer.OrdinalIgnoreCase);

    private static bool IsVideoFile(string? path) =>
        !string.IsNullOrEmpty(path) && t_videoExtensions.Contains(Path.GetExtension(path));

    private static string? ResolveFilePath(string folderPath, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var cleanRel = relativePath.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        var baseDir = Path.GetFullPath(folderPath);
        if (!baseDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            baseDir += Path.DirectorySeparatorChar;
        }

        var p = Path.GetFullPath(Path.Combine(baseDir, cleanRel));
        return p.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase) && File.Exists(p) && ThemeValidator.ValidateAssetFile(p, out _)
            ? p
            : null;
    }

    private static string? ResolveStickerPath(string folderPath, Dictionary<string, string> stickers, string key) =>
        stickers.TryGetValue(key, out var rel) && !string.IsNullOrWhiteSpace(rel)
            ? ResolveFilePath(folderPath, rel)
            : null;

    private void ResetDefaultResources()
    {
        try
        {
            _baseBgSurface = Color.FromArgb("#161622");
            _baseBgElevated = Color.FromArgb("#1E1E2E");
            _baseBgInput = Color.FromArgb("#12121C");

            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                return;
            }

            if (_defaultResourceSnapshot.Count > 0)
            {
                foreach (var (key, value) in _defaultResourceSnapshot)
                {
                    if (value is Color c)
                    {
                        SetResourceValue(appResources, key, c);
                    }
                    else if (value is SolidColorBrush brush)
                    {
                        SetResourceValue(appResources, key, new SolidColorBrush(brush.Color));
                    }
                    else if (value is string s)
                    {
                        SetResourceValue(appResources, key, s);
                    }
                    else
                    {
                        SetResourceValue(appResources, key, value);
                    }
                }

                if (_defaultResourceSnapshot.TryGetValue("BgSurface", out var snapSurf) && snapSurf is Color surfC)
                {
                    _baseBgSurface = surfC;
                }
                if (_defaultResourceSnapshot.TryGetValue("BgElevated", out var snapElev) && snapElev is Color elevC)
                {
                    _baseBgElevated = elevC;
                }
                if (_defaultResourceSnapshot.TryGetValue("BgInput", out var snapInp) && snapInp is Color inpC)
                {
                    _baseBgInput = inpC;
                }
            }
            else
            {
                SetResourceValue(appResources, "Primary", Color.FromArgb("#0078D4"));
                SetResourceValue(appResources, "PrimaryBrush", new SolidColorBrush(Color.FromArgb("#0078D4")));
                SetResourceValue(appResources, "PrimaryBright", Color.FromArgb("#2B88D8"));
                SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(Color.FromArgb("#2B88D8")));
                SetResourceValue(appResources, "PrimaryLight", Color.FromArgb("#4C9CE1"));
                SetResourceValue(appResources, "PrimaryLight2", Color.FromArgb("#0067C0"));
                SetResourceValue(appResources, "PrimaryDim", Color.FromArgb("#005A9E"));
                SetResourceValue(appResources, "PrimaryGlow", Color.FromArgb("#004578"));
                SetResourceValue(appResources, "Accent", Color.FromArgb("#00E5FF"));
                SetResourceValue(appResources, "AccentBrush", new SolidColorBrush(Color.FromArgb("#00E5FF")));
                SetResourceValue(appResources, "AccentDim", Color.FromArgb("#005F6B"));
                SetResourceValue(appResources, "AccentGlow", Color.FromArgb("#0097A7"));
                SetResourceValue(appResources, "AccentLight", Color.FromArgb("#0284C7"));
                SetResourceValue(appResources, "Purple", Color.FromArgb("#9F6FF0"));
                SetResourceValue(appResources, "PurpleDim", Color.FromArgb("#2A1B3D"));
                SetResourceValue(appResources, "PurpleLight", Color.FromArgb("#7C3AED"));
                SetResourceValue(appResources, "Pink", Color.FromArgb("#EC4899"));
                SetResourceValue(appResources, "BgBase", Color.FromArgb("#0E0E14"));
                SetResourceValue(appResources, "BgBaseBrush", new SolidColorBrush(Color.FromArgb("#0E0E14")));
                SetResourceValue(appResources, "BgSurface", Color.FromArgb("#161622"));
                SetResourceValue(appResources, "BgSurfaceBrush", new SolidColorBrush(Color.FromArgb("#161622")));
                SetResourceValue(appResources, "BgElevated", Color.FromArgb("#202030"));
                SetResourceValue(appResources, "BgInput", Color.FromArgb("#12121B"));
                SetResourceValue(appResources, "BgOverlay", Color.FromArgb("#B3000000"));
                SetResourceValue(appResources, "TextPrimary", Color.FromArgb("#FFFFFF"));
                SetResourceValue(appResources, "TextPrimaryBrush", new SolidColorBrush(Color.FromArgb("#FFFFFF")));
                SetResourceValue(appResources, "TextSecondary", Color.FromArgb("#D0FFFFFF"));
                SetResourceValue(appResources, "TextMuted", Color.FromArgb("#90FFFFFF"));
                SetResourceValue(appResources, "TextOnPrimary", Color.FromArgb("#FFFFFF"));
                SetResourceValue(appResources, "SolidBorderDark", Color.FromArgb("#282838"));
                SetResourceValue(appResources, "BorderSubtle", Color.FromArgb("#28283C"));
                SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(Color.FromArgb("#28283C")));
                SetResourceValue(appResources, "BorderMedium", Color.FromArgb("#363650"));
                SetResourceValue(appResources, "BorderStrong", Color.FromArgb("#4A4A68"));
                SetResourceValue(appResources, "Success", Color.FromArgb("#10B981"));
                SetResourceValue(appResources, "SuccessBrush", new SolidColorBrush(Color.FromArgb("#10B981")));
                SetResourceValue(appResources, "SuccessDim", Color.FromArgb("#1F10B981"));
                SetResourceValue(appResources, "Warning", Color.FromArgb("#F59E0B"));
                SetResourceValue(appResources, "WarningDim", Color.FromArgb("#1FF59E0B"));
                SetResourceValue(appResources, "Error", Color.FromArgb("#EF4444"));
                SetResourceValue(appResources, "ErrorBrush", new SolidColorBrush(Color.FromArgb("#EF4444")));
                SetResourceValue(appResources, "ErrorDim", Color.FromArgb("#1FEF4444"));
                SetResourceValue(appResources, "AppFontRegular", "RobotoRegular");
                SetResourceValue(appResources, "AppFontMedium", "RobotoMedium");
                SetResourceValue(appResources, "AppFontBold", "RobotoBold");
                SetResourceValue(appResources, "AppFontTitle", "CommissionerExtraBold");
                SetResourceValue(appResources, "CardCornerRadius", new CornerRadius(12));
                SetResourceValue(appResources, "CardBorderThickness", 1.0);
            }

            var defaultPrimary = _defaultResourceSnapshot.TryGetValue("Primary", out var snapPrim) && snapPrim is Color primC
                ? primC
                : Color.FromArgb("#0078D4");
            var defaultAccent = _defaultResourceSnapshot.TryGetValue("Accent", out var snapAcc) && snapAcc is Color accC
                ? accC
                : Color.FromArgb("#00E5FF");

            var defaultGradStops = new GradientStopCollection
            {
                new GradientStop(defaultPrimary, 0.0f),
                new GradientStop(defaultAccent, 1.0f)
            };
            SetResourceValue(appResources, "PrimaryGradientBrush", new LinearGradientBrush(defaultGradStops, new Point(0, 0), new Point(1, 1)));

            var defaultSurfaceStops = new GradientStopCollection
            {
                new GradientStop(_baseBgSurface, 0.0f),
                new GradientStop(_baseBgSurface.AddLuminosity(0.06f), 1.0f)
            };
            SetResourceValue(appResources, "SurfaceGradientBrush", new LinearGradientBrush(defaultSurfaceStops, new Point(0, 0), new Point(0, 1)));

            SetResourceValue(appResources, "CardCornerRadius", new CornerRadius(12));
            SetResourceValue(appResources, "CardBorderThickness", 1.0);

            ApplyCardOpacity(CardOpacity, forceImmediate: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ThemeManager] Reset error: {ex.Message}");
        }
    }

    public void ApplyCardOpacity(double opacity, bool forceImmediate = false)
    {
        var now = Environment.TickCount64;
        if (!forceImmediate && now - _lastApplyOpacityTicks < 35 && opacity > 0.20 && opacity < 1.00)
        {
            return;
        }
        _lastApplyOpacityTicks = now;

        void UpdateAction()
        {
            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                return;
            }

            var baseSurface = ActiveTheme?.Core?.Colors?.BgSurface != null && Color.TryParse(ActiveTheme.Core.Colors.BgSurface, out var parsedThemeColor)
                ? parsedThemeColor
                : _baseBgSurface;

            var surfaceWithAlpha = baseSurface.WithAlpha((float)opacity);
            SetResourceValue(appResources, "BgSurface", surfaceWithAlpha);
            SetResourceValue(appResources, "BgSurfaceBrush", new SolidColorBrush(surfaceWithAlpha));

            var baseElevated = ActiveTheme?.Core?.Colors?.BgElevated != null && Color.TryParse(ActiveTheme.Core.Colors.BgElevated, out var parsedElevated)
                ? parsedElevated
                : _baseBgElevated;

            var elevatedWithAlpha = baseElevated.WithAlpha((float)Math.Clamp(opacity + 0.08, 0.25, 1.0));
            SetResourceValue(appResources, "BgElevated", elevatedWithAlpha);

            SetResourceValue(appResources, "BgInput", _baseBgInput);

            CardOpacityChanged?.Invoke(opacity);
        }

        if (MainThread.IsMainThread)
        {
            UpdateAction();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(UpdateAction);
        }
    }

    public static void ApplyVisualTreeCardOpacity(VisualElement? root, Color bgSurface, Color? bgElevated = null)
    {
        _ = root;
        _ = bgSurface;
        _ = bgElevated;
    }

    public void ResetToDefault()
    {
        if (MainThread.IsMainThread)
        {
            ResetDefaultResources();
        }
        else
        {
            try
            {
                MainThread.InvokeOnMainThreadAsync(ResetDefaultResources).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        ActiveTheme = null;
        ActiveThemeFolderPath = null;
        Preferences.Default.Set(ActiveThemeKey, DefaultThemePreferenceValue);
        ThemeAudioService.Reset();

        OnThemeReset?.Invoke();
    }

    public async Task<(bool Success, string? ErrorMessage, ThemeManifest? Manifest)> ImportThemeFromZipAsync(
        Stream zipStream,
        string fileName,
        CancellationToken ct = default)
    {
        if (zipStream == null)
        {
            return (false, "Поток архива темы пуст.", null);
        }

        var tempDir = Path.Combine(Path.GetTempPath(), "obxodka_theme_import_" + Guid.NewGuid().ToString("N"));
        try
        {
            await SafeThemeExtractor.ExtractZipSafelyAsync(zipStream, tempDir, ct).ConfigureAwait(false);

            var sourceThemeDir = tempDir;
            var manifestPath = Path.Combine(tempDir, "theme.json");
            if (!File.Exists(manifestPath))
            {
                var subDirs = Directory.GetDirectories(tempDir);
                foreach (var subDir in subDirs)
                {
                    var candidate = Path.Combine(subDir, "theme.json");
                    if (File.Exists(candidate))
                    {
                        sourceThemeDir = subDir;
                        manifestPath = candidate;
                        break;
                    }
                }
            }

            if (!File.Exists(manifestPath))
            {
                return (false, $"В архиве '{fileName}' не найден файл theme.json.", null);
            }

            var manifestJson = await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(manifestJson, ThemeJsonContext.Default.ThemeManifest);
            string? manifestError = null;
            if (manifest == null || !ThemeValidator.ValidateManifest(manifest, out manifestError))
            {
                return (false, $"Некорректный манифест темы: {manifestError ?? "Пустой манифест"}", null);
            }

            foreach (var extractedFile in Directory.EnumerateFiles(sourceThemeDir, "*", SearchOption.AllDirectories))
            {
                if (!ThemeValidator.ValidateAssetFile(extractedFile, out var assetError))
                {
                    var relName = Path.GetRelativePath(sourceThemeDir, extractedFile);
                    return (false, $"Файл '{relName}' не прошел проверку безопасности: {assetError}", null);
                }
            }

            var targetDir = Path.Combine(ThemesDirectory, manifest.Id);
            if (Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            _ = Directory.CreateDirectory(targetDir);
            foreach (var srcFile in Directory.EnumerateFiles(sourceThemeDir, "*", SearchOption.AllDirectories))
            {
                var relPath = Path.GetRelativePath(sourceThemeDir, srcFile);
                if (!ThemeValidator.IsSafeRelativePath(relPath))
                {
                    return (false, $"Недопустимый относительный путь файла: '{relPath}'", null);
                }

                var destFile = Path.Combine(targetDir, relPath);
                var destParent = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destParent))
                {
                    _ = Directory.CreateDirectory(destParent);
                }

                File.Copy(srcFile, destFile, overwrite: true);
            }

            _ = MainThread.IsMainThread
                ? ApplyThemeById(manifest.Id)
                : await MainThread.InvokeOnMainThreadAsync(() => ApplyThemeById(manifest.Id)).ConfigureAwait(false);

            return (true, null, manifest);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return (false, ex.Message, null);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try
                {
                    Directory.Delete(tempDir, true);
                }
                catch
                {
                }
            }
        }
    }

    private const string SeasonalHalloweenThemeId = "halloween_seasonal_default";

    public static ThemeManifest CreateHalloweenSeasonalManifest() => new()
    {
        Id = SeasonalHalloweenThemeId,
        Name = "Halloween Edition",
        Author = "OctoCore",
        Version = "1.0.0",
        Description = "Праздничная хэллоуинская тема",
        Core = new CoreThemeTokens
        {
            Colors = new ThemeColors
            {
                BgBase = "#0D031D",
                BgSurface = "#180730",
                BgElevated = "#260C45",
                BgInput = "#120524",
                Primary = "#FF6600",
                PrimaryBright = "#FF8800",
                PrimaryDim = "#331500",
                Accent = "#00FFFF",
                BorderSubtle = "#38125E",
                BorderMedium = "#521A8A",
                SolidBorderDark = "#38125E",
                TextPrimary = "#FFFFFF",
                TextSecondary = "#E9D5FF",
                TextMuted = "#A855F7"
            },
            Gradients = new ThemeGradients
            {
                PrimaryGradient = new GradientDefinition
                {
                    Stops = ["#FF5500", "#FF2E93", "#A855F7"]
                }
            }
        }
    };

    public void RestoreActiveThemeAtStartup()
    {
        EnsureDefaultResourcesCaptured();

        var activeId = Preferences.Default.Get<string?>(ActiveThemeKey, null);
        if (string.Equals(activeId, DefaultThemePreferenceValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(activeId) || activeId == SeasonalHalloweenThemeId)
        {
            if (SeasonSchedule.IsHalloweenSeason())
            {
                _ = ApplyTheme(CreateHalloweenSeasonalManifest());
                return;
            }

            if (activeId == SeasonalHalloweenThemeId)
            {
                Preferences.Default.Remove(ActiveThemeKey);
                ResetToDefault();
            }
            return;
        }

        var manifestPath = Path.Combine(ThemesDirectory, activeId, "theme.json");
        if (!File.Exists(manifestPath))
        {
            Preferences.Default.Remove(ActiveThemeKey);
            if (SeasonSchedule.IsHalloweenSeason())
            {
                _ = ApplyTheme(CreateHalloweenSeasonalManifest());
            }
            return;
        }

        try
        {
            var json = File.ReadAllText(manifestPath);
            var manifest = JsonSerializer.Deserialize(json, ThemeJsonContext.Default.ThemeManifest);
            if (manifest != null && ThemeValidator.ValidateManifest(manifest, out _))
            {
                _ = ApplyTheme(manifest);
            }
        }
        catch
        {
            Preferences.Default.Remove(ActiveThemeKey);
            if (SeasonSchedule.IsHalloweenSeason())
            {
                _ = ApplyTheme(CreateHalloweenSeasonalManifest());
            }
        }
    }

    public void ReapplyActiveTheme()
    {
        if (ActiveTheme is not null)
        {
            _ = ApplyTheme(ActiveTheme);
        }
    }
}
