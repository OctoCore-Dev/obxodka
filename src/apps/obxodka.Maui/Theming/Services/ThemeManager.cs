namespace obxodka.Maui.Theming.Services;

#pragma warning disable CA1822

//главный менеджер тем оформления для maui интерфейса, управляющий жизненным циклом и настройками
public sealed partial class ThemeManager
{
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

    //кроссплатформенный сервис хранения и загрузки ассетов тем
    private readonly ThemeStorageService _storageService;

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

    public ThemeManifest? ActiveTheme { get; private set; }
    public string? ActiveThemeFolderPath { get; private set; }
    public string? LastError => _storageService.LastError;

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

    public string ThemesDirectory => _storageService.ThemesDirectory;

    public static ThemeManager? Instance { get; private set; }
    public static double CurrentGlowIntensity => Instance?.GlowIntensity ?? Preferences.Default.Get(ThemeGlowIntensityKey, 1.0);

    public ThemeManager()
    {
        Instance = this;
        _storageService = new ThemeStorageService(Path.Combine(FileSystem.AppDataDirectory, "themes"));
        CaptureDefaultResourcesSnapshot();
    }

    public bool ApplyThemeById(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        var manifestPath = Path.Combine(ThemesDirectory, themeId.TrimStart('/', '\\'), "theme.json");
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
        catch
        {
            return false;
        }
    }

    public async Task<bool> ApplyThemeByIdAsync(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            return false;
        }

        var manifestPath = Path.Combine(ThemesDirectory, themeId.TrimStart('/', '\\'), "theme.json");
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
        catch
        {
            return false;
        }
    }

    public async Task<bool> ApplyThemeAsync(ThemeManifest manifest) =>
        manifest != null &&
        ThemeValidator.ValidateManifest(manifest, out _) &&
        (MainThread.IsMainThread
            ? ApplyTheme(manifest)
            : await MainThread.InvokeOnMainThreadAsync(() => ApplyTheme(manifest)).ConfigureAwait(false));

    //восстановление активной темы при запуске приложения из локального хранилища
    public void RestoreActiveThemeAtStartup()
    {
        EnsureDefaultResourcesCaptured();

        var activeId = Preferences.Default.Get<string?>(ActiveThemeKey, null);
        if (string.IsNullOrWhiteSpace(activeId) ||
            string.Equals(activeId, DefaultThemePreferenceValue, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var manifestPath = Path.Combine(ThemesDirectory, activeId.TrimStart('/', '\\'), "theme.json");
        if (!File.Exists(manifestPath))
        {
            Preferences.Default.Remove(ActiveThemeKey);
            ResetToDefault();
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
            else
            {
                Preferences.Default.Remove(ActiveThemeKey);
                ResetToDefault();
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[ThemeManager] Ошибка восстановления темы '{activeId}': {ex.Message}");
            Preferences.Default.Remove(ActiveThemeKey);
            ResetToDefault();
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
