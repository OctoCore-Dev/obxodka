namespace obxodka.Maui.Theming.Services;

public sealed partial class ThemeManager
{
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
                //подавление ошибок вызова в основном потоке
            }
        }

        ActiveTheme = manifest;
        ActiveThemeFolderPath = folderPath;
        Preferences.Default.Set(ActiveThemeKey, manifest.Id);

        string? bgPath = null;
        string? backgroundVideoPath = null;

        void RegisterBackgroundCandidate(string? rel)
        {
            var resolved = ThemeStorageService.ResolveFilePath(folderPath, rel);
            if (resolved is null)
            {
                return;
            }

            if (ThemeStorageService.IsVideoFile(resolved))
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
                framePath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations.ScreenFrameMobile);
                activeSlice = manifest.Decorations.FrameSliceMobile ?? manifest.Decorations.FrameSlice;
            }
            else if (!string.IsNullOrEmpty(manifest.Decorations?.ScreenFrame))
            {
                framePath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations.ScreenFrame);
                activeSlice = manifest.Decorations.FrameSlice;
            }
        }
        else
        {
            framePath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ScreenFrame);
            activeSlice = manifest.Decorations?.FrameSlice;
        }

        var cardFramePath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.CardFrame);
        var buttonRingPath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonRing);
        var fontFilePath = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Ui?.FontFile);

        var connectSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Connect ?? GetObxodkaNestedProperty(manifest, "sounds", "connect"));
        var disconnectSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Disconnect ?? GetObxodkaNestedProperty(manifest, "sounds", "disconnect"));
        var clickSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Click ?? GetObxodkaNestedProperty(manifest, "sounds", "click"));
        var hoverSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Hover ?? GetObxodkaNestedProperty(manifest, "sounds", "hover"));
        var notificationSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Notification ?? GetObxodkaNestedProperty(manifest, "sounds", "notification"));
        var ambientSound = ThemeStorageService.ResolveFilePath(folderPath, manifest.Core.Sounds?.Ambient ?? GetObxodkaNestedProperty(manifest, "sounds", "ambient"));

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
                //подавление ошибок проверки файла
            }
        }

        var particleSpritePaths = new List<string>();
        var singleSprite = ThemeStorageService.ResolveFilePath(folderPath, manifest.Vfx?.ParticleSprite);
        if (!string.IsNullOrEmpty(singleSprite))
        {
            particleSpritePaths.Add(singleSprite);
        }

        if (manifest.Vfx?.ParticleSprites is { Count: > 0 } rawSprites)
        {
            foreach (var rawSprite in rawSprites)
            {
                var resolvedSprite = ThemeStorageService.ResolveFilePath(folderPath, rawSprite);
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
            cornerTopLeft = ThemeStorageService.ResolveStickerPath(folderPath, stickersMap, "topLeft");
            cornerTopRight = ThemeStorageService.ResolveStickerPath(folderPath, stickersMap, "topRight");
            cornerBottomLeft = ThemeStorageService.ResolveStickerPath(folderPath, stickersMap, "bottomLeft");
            cornerBottomRight = ThemeStorageService.ResolveStickerPath(folderPath, stickersMap, "bottomRight");
        }

        var buttonImageIdle = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageIdle ?? GetObxodkaNestedProperty(manifest, "vpnButton", "idle"));
        var buttonImageConnecting = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageConnecting ?? GetObxodkaNestedProperty(manifest, "vpnButton", "connecting"));
        var buttonImageActive = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageActive ?? GetObxodkaNestedProperty(manifest, "vpnButton", "active"));
        var buttonImageError = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonImageError ?? GetObxodkaNestedProperty(manifest, "vpnButton", "error"));

        var buttonVideoIdle = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoIdle ?? GetObxodkaNestedProperty(manifest, "vpnButton", "idleWebp"));
        var buttonVideoConnecting = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoConnecting ?? GetObxodkaNestedProperty(manifest, "vpnButton", "connectingWebp"));
        var buttonVideoActive = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoActive ?? GetObxodkaNestedProperty(manifest, "vpnButton", "activeWebp"));
        var buttonVideoError = ThemeStorageService.ResolveFilePath(folderPath, manifest.Decorations?.ButtonVideoError ?? GetObxodkaNestedProperty(manifest, "vpnButton", "errorWebp"));

        var overlayCandidate = manifest.Vfx?.OverlayImage ?? manifest.Vfx?.Physics?.OverlayImage ??
            (manifest.Vfx?.ScreenOverlay?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) == true ||
            manifest.Vfx?.ScreenOverlay?.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) == true ||
            manifest.Vfx?.ScreenOverlay?.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) == true
                ? manifest.Vfx.ScreenOverlay
                : null);
        var screenOverlayPath = ThemeStorageService.ResolveFilePath(folderPath, overlayCandidate);

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
            ScreenOverlayPath = screenOverlayPath,
        };

        OnThemeApplied?.Invoke(args);
        return true;
    }
}
