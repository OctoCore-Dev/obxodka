namespace obxodka.Maui.Theming.Services;

public sealed partial class ThemeManager
{
    private Color _baseBgElevated = Color.FromArgb("#323232");
    private Color _baseBgInput = Color.FromArgb("#252525");
    private Color _baseBgSurface = Color.FromArgb("#2B2B2B");

    private double? _cachedCardOpacity;
    private long _lastApplyOpacityTicks;
    private int _opacitySaveSequence;

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
                    ApplyCardOpacity(clamped, true);
                }
            });

            ApplyCardOpacity(clamped);
        }
    }

    //определение текущей системной темы ос с фоллбэком на темную
    private static AppTheme GetEffectiveSystemTheme() =>
        Application.Current?.RequestedTheme == AppTheme.Light ? AppTheme.Light : AppTheme.Dark;

    //платформенный радиус скругления карточек для системной темы по умолчанию
    private static CornerRadius GetDefaultPlatformCornerRadius() =>
        DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? new CornerRadius(16)
            : new CornerRadius(10);

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

            //сбрасываем базовые токены на темную палитру перед наложением кастомной темы, чтобы не оставались светлые цвета ос
            ApplySystemDefaultPalette(AppTheme.Dark);

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

                var onPrimaryHex = ThemeValidator.ChooseContrastingTextHex(manifest.Core.Colors.Primary);
                if (Color.TryParse(onPrimaryHex, out var textOnPrimary))
                {
                    SetResourceValue(appResources, "TextOnPrimary", textOnPrimary);
                }
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.PrimaryBright) &&
                Color.TryParse(manifest.Core.Colors.PrimaryBright, out var primaryBright))
            {
                SetResourceValue(appResources, "PrimaryBright", primaryBright);
                SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(primaryBright));
            }
            else if (primaryColor != null)
            {
                SetResourceValue(appResources, "PrimaryBright", primaryColor);
                SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(primaryColor));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.PrimaryDim) &&
                Color.TryParse(manifest.Core.Colors.PrimaryDim, out var parsedPrimaryDim))
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

                SetResourceValue(appResources, "PrimaryGradientBrush",
                    new LinearGradientBrush(gradStops, new Point(0, 0), new Point(1, 1)));
            }

            if (Color.TryParse(manifest.Core.Colors.BgBase, out var parsedBgBase))
            {
                SetResourceValue(appResources, "BgBase", parsedBgBase);
                SetResourceValue(appResources, "BgBaseBrush", new SolidColorBrush(parsedBgBase));
                SetResourceValue(appResources, "BgOverlay", parsedBgBase.WithAlpha(0.70f));
            }

            var rawSurface = !string.IsNullOrEmpty(manifest.Core.Colors.BgSurface) &&
                             Color.TryParse(manifest.Core.Colors.BgSurface, out var parsedSurface)
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
                        surfaceStops.Add(new GradientStop(ssc.WithAlpha((float)userOpacity), offset));
                    }
                }
            }

            SetResourceValue(appResources, "SurfaceGradientBrush", new LinearGradientBrush(surfaceStops, new Point(0, 0), new Point(0, 1)));

            var rawElevated = !string.IsNullOrEmpty(manifest.Core.Colors.BgElevated) &&
                              Color.TryParse(manifest.Core.Colors.BgElevated, out var bgElevatedColor)
                ? bgElevatedColor
                : rawSurface.AddLuminosity(0.08f);
            _baseBgElevated = rawElevated;
            SetResourceValue(appResources, "BgElevated", rawElevated.WithAlpha((float)Math.Clamp(userOpacity + 0.08, 0.25, 1.0)));

            var rawInput = !string.IsNullOrEmpty(manifest.Core.Colors.BgInput) &&
                           Color.TryParse(manifest.Core.Colors.BgInput, out var bgInputColor)
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

            if (!string.IsNullOrEmpty(manifest.Core.Colors.TextSecondary) &&
                Color.TryParse(manifest.Core.Colors.TextSecondary, out var textSecondaryColor))
            {
                SetResourceValue(appResources, "TextSecondary", textSecondaryColor);
                SetResourceValue(appResources, "TextMuted", textSecondaryColor);
            }
            else if (textPrimary != null)
            {
                SetResourceValue(appResources, "TextSecondary", textPrimary.WithAlpha(0.75f));
                SetResourceValue(appResources, "TextMuted", textPrimary.WithAlpha(0.55f));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.TextMuted) &&
                Color.TryParse(manifest.Core.Colors.TextMuted, out var textMutedColor))
            {
                SetResourceValue(appResources, "TextMuted", textMutedColor);
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.SolidBorderDark) &&
                Color.TryParse(manifest.Core.Colors.SolidBorderDark, out var parsedBorderDark))
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

            if (!string.IsNullOrEmpty(manifest.Core.Colors.BorderSubtle) &&
                Color.TryParse(manifest.Core.Colors.BorderSubtle, out var borderSubtleColor))
            {
                SetResourceValue(appResources, "BorderSubtle", borderSubtleColor);
                SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(borderSubtleColor));
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.BorderMedium) &&
                Color.TryParse(manifest.Core.Colors.BorderMedium, out var borderMediumColor))
            {
                SetResourceValue(appResources, "BorderMedium", borderMediumColor);
            }

            if (!string.IsNullOrEmpty(manifest.Core.Colors.Success) && Color.TryParse(manifest.Core.Colors.Success, out var successColor))
            {
                SetResourceValue(appResources, "Success", successColor);
                SetResourceValue(appResources, "SuccessBrush", new SolidColorBrush(successColor));
                SetResourceValue(appResources, "SuccessDim", successColor.WithAlpha(0.15f));

                var onSuccessHex = ThemeValidator.ChooseContrastingTextHex(manifest.Core.Colors.Success);
                if (Color.TryParse(onSuccessHex, out var textOnSuccess))
                {
                    SetResourceValue(appResources, "TextOnSuccess", textOnSuccess);
                }
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
        catch
        {
            //подавление ошибок применения стилей
        }
    }

    //единый источник истины для системной темы по умолчанию (мягкий графит fluent 2 / чистая светлая)
    private void ApplySystemDefaultPalette(AppTheme theme)
    {
        try
        {
            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                return;
            }

            var isLight = theme == AppTheme.Light;

            var bgBase = Color.FromArgb(isLight ? "#F3F3F3" : "#202020");
            var bgSurface = Color.FromArgb(isLight ? "#FFFFFF" : "#2B2B2B");
            var bgElevated = Color.FromArgb(isLight ? "#EAEAEA" : "#323232");
            var bgInput = Color.FromArgb(isLight ? "#FFFFFF" : "#252525");
            var bgOverlay = Color.FromArgb(isLight ? "#66000000" : "#A6000000");

            var solidBorder = Color.FromArgb(isLight ? "#E0E0E0" : "#3A3A3A");
            var borderSubtle = Color.FromArgb(isLight ? "#E2E2E2" : "#383838");
            var borderMedium = Color.FromArgb(isLight ? "#CCCCCC" : "#484848");
            var borderStrong = Color.FromArgb(isLight ? "#B0B0B0" : "#5E5E5E");

            var primary = Color.FromArgb(isLight ? "#0067C0" : "#0078D4");
            var primaryBright = Color.FromArgb(isLight ? "#005FB8" : "#60CDFF");
            var primaryLight = Color.FromArgb(isLight ? "#0078D4" : "#4CC2FF");
            var primaryLight2 = Color.FromArgb(isLight ? "#005A9E" : "#0067C0");
            var primaryDim = Color.FromArgb(isLight ? "#1F0067C0" : "#2660CDFF");
            var primaryGlow = Color.FromArgb(isLight ? "#380067C0" : "#4060CDFF");

            var accent = Color.FromArgb(isLight ? "#0284C7" : "#60CDFF");
            var accentDim = Color.FromArgb(isLight ? "#1F0284C7" : "#2660CDFF");
            var accentGlow = Color.FromArgb(isLight ? "#380284C7" : "#4D60CDFF");
            var accentLight = Color.FromArgb(isLight ? "#0369A1" : "#4CC2FF");

            var purple = Color.FromArgb(isLight ? "#7C3AED" : "#A78BFA");
            var purpleDim = Color.FromArgb(isLight ? "#1F7C3AED" : "#26A78BFA");
            var purpleLight = Color.FromArgb(isLight ? "#6D28D9" : "#8B5CF6");
            var pink = Color.FromArgb(isLight ? "#DB2777" : "#F472B6");

            var textPrimary = Color.FromArgb(isLight ? "#1A1A1A" : "#FFFFFF");
            var textSecondary = Color.FromArgb(isLight ? "#525252" : "#D2D2D2");
            var textMuted = Color.FromArgb(isLight ? "#737373" : "#9E9E9E");
            var textOnPrimary = Color.FromArgb("#FFFFFF");

            var success = Color.FromArgb(isLight ? "#059669" : "#10B981");
            var successDim = Color.FromArgb(isLight ? "#1F059669" : "#2410B981");
            var warning = Color.FromArgb(isLight ? "#B45309" : "#F59E0B");
            var warningDim = Color.FromArgb(isLight ? "#1FB45309" : "#24F59E0B");
            var error = Color.FromArgb(isLight ? "#C42B1C" : "#EF4444");
            var errorDim = Color.FromArgb(isLight ? "#1FC42B1C" : "#24EF4444");

            _baseBgSurface = bgSurface;
            _baseBgElevated = bgElevated;
            _baseBgInput = bgInput;

            SetResourceValue(appResources, "Primary", primary);
            SetResourceValue(appResources, "PrimaryBrush", new SolidColorBrush(primary));
            SetResourceValue(appResources, "PrimaryBright", primaryBright);
            SetResourceValue(appResources, "PrimaryBrightBrush", new SolidColorBrush(primaryBright));
            SetResourceValue(appResources, "PrimaryLight", primaryLight);
            SetResourceValue(appResources, "PrimaryLight2", primaryLight2);
            SetResourceValue(appResources, "PrimaryDim", primaryDim);
            SetResourceValue(appResources, "PrimaryGlow", primaryGlow);

            SetResourceValue(appResources, "Accent", accent);
            SetResourceValue(appResources, "AccentBrush", new SolidColorBrush(accent));
            SetResourceValue(appResources, "AccentDim", accentDim);
            SetResourceValue(appResources, "AccentGlow", accentGlow);
            SetResourceValue(appResources, "AccentLight", accentLight);

            SetResourceValue(appResources, "Purple", purple);
            SetResourceValue(appResources, "PurpleDim", purpleDim);
            SetResourceValue(appResources, "PurpleLight", purpleLight);
            SetResourceValue(appResources, "Pink", pink);

            SetResourceValue(appResources, "BgBase", bgBase);
            SetResourceValue(appResources, "BgBaseBrush", new SolidColorBrush(bgBase));
            SetResourceValue(appResources, "BgSurface", bgSurface);
            SetResourceValue(appResources, "BgSurfaceBrush", new SolidColorBrush(bgSurface));
            SetResourceValue(appResources, "BgElevated", bgElevated);
            SetResourceValue(appResources, "BgInput", bgInput);
            SetResourceValue(appResources, "BgOverlay", bgOverlay);

            SetResourceValue(appResources, "TextPrimary", textPrimary);
            SetResourceValue(appResources, "TextPrimaryBrush", new SolidColorBrush(textPrimary));
            SetResourceValue(appResources, "TextSecondary", textSecondary);
            SetResourceValue(appResources, "TextMuted", textMuted);
            SetResourceValue(appResources, "TextOnPrimary", textOnPrimary);
            SetResourceValue(appResources, "TextOnSuccess", textOnPrimary);

            SetResourceValue(appResources, "SolidBorderDark", solidBorder);
            SetResourceValue(appResources, "BorderSubtle", borderSubtle);
            SetResourceValue(appResources, "BorderSubtleBrush", new SolidColorBrush(borderSubtle));
            SetResourceValue(appResources, "BorderMedium", borderMedium);
            SetResourceValue(appResources, "BorderStrong", borderStrong);

            SetResourceValue(appResources, "Success", success);
            SetResourceValue(appResources, "SuccessBrush", new SolidColorBrush(success));
            SetResourceValue(appResources, "SuccessDim", successDim);
            SetResourceValue(appResources, "Warning", warning);
            SetResourceValue(appResources, "WarningDim", warningDim);
            SetResourceValue(appResources, "Error", error);
            SetResourceValue(appResources, "ErrorBrush", new SolidColorBrush(error));
            SetResourceValue(appResources, "ErrorDim", errorDim);

            SetResourceValue(appResources, "AppFontRegular", "RobotoRegular");
            SetResourceValue(appResources, "AppFontMedium", "RobotoMedium");
            SetResourceValue(appResources, "AppFontBold", "RobotoBold");
            SetResourceValue(appResources, "AppFontTitle", "CommissionerExtraBold");

            var defaultGradStops = new GradientStopCollection
            {
                new GradientStop(primary, 0.0f),
                new GradientStop(accent, 1.0f)
            };
            SetResourceValue(appResources, "PrimaryGradientBrush",
                new LinearGradientBrush(defaultGradStops, new Point(0, 0), new Point(1, 1)));

            var surfaceEnd = isLight ? bgSurface : bgSurface.AddLuminosity(0.05f);
            var defaultSurfaceStops = new GradientStopCollection
            {
                new GradientStop(bgSurface, 0.0f),
                new GradientStop(surfaceEnd, 1.0f)
            };
            SetResourceValue(appResources, "SurfaceGradientBrush",
                new LinearGradientBrush(defaultSurfaceStops, new Point(0, 0), new Point(0, 1)));

            SetResourceValue(appResources, "CardCornerRadius", GetDefaultPlatformCornerRadius());
            SetResourceValue(appResources, "CardBorderThickness", 1.0);
        }
        catch
        {
            //подавление ошибок применения системной палитры
        }
    }

    private void ResetDefaultResources() => ApplySystemDefaultPalette(GetEffectiveSystemTheme());

    public void ApplyCardOpacity(double opacity, bool forceImmediate = false)
    {
        var now = Environment.TickCount64;
        if (!forceImmediate && now - _lastApplyOpacityTicks < 35 && opacity is > 0.20 and < 1.00)
        {
            return;
        }

        _lastApplyOpacityTicks = now;

        if (MainThread.IsMainThread)
        {
            UpdateAction();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(UpdateAction);
        }

        return;

        void UpdateAction()
        {
            var appResources = Application.Current?.Resources;
            if (appResources == null)
            {
                return;
            }

            //системная тема по умолчанию без обоев остается полностью непрозрачной чтобы светлые и графитовые карточки не мутнели
            var hasCustomTheme = ActiveTheme is not null;
            var effectiveOpacity = hasCustomTheme ? opacity : 1.0;

            var baseSurface = ActiveTheme?.Core.Colors.BgSurface != null &&
                              Color.TryParse(ActiveTheme.Core.Colors.BgSurface, out var parsedThemeColor)
                ? parsedThemeColor
                : _baseBgSurface;

            var surfaceWithAlpha = baseSurface.WithAlpha((float)effectiveOpacity);
            SetResourceValue(appResources, "BgSurface", surfaceWithAlpha);
            SetResourceValue(appResources, "BgSurfaceBrush", new SolidColorBrush(surfaceWithAlpha));

            var surfaceGradStops = new GradientStopCollection
            {
                new GradientStop(surfaceWithAlpha, 0.0f),
                new GradientStop(surfaceWithAlpha.AddLuminosity(0.06f), 1.0f)
            };
            if (hasCustomTheme && ActiveTheme?.Core.Gradients?.SurfaceGradient?.Stops is { Count: >= 2 } customSurfaceStops)
            {
                surfaceGradStops.Clear();
                for (var i = 0; i < customSurfaceStops.Count; i++)
                {
                    if (Color.TryParse(customSurfaceStops[i], out var ssc))
                    {
                        var offset = (float)i / (customSurfaceStops.Count - 1);
                        surfaceGradStops.Add(new GradientStop(ssc.WithAlpha((float)effectiveOpacity), offset));
                    }
                }
            }

            SetResourceValue(appResources, "SurfaceGradientBrush",
                new LinearGradientBrush(surfaceGradStops, new Point(0, 0), new Point(0, 1)));

            var baseElevated = ActiveTheme?.Core.Colors.BgElevated != null &&
                               Color.TryParse(ActiveTheme.Core.Colors.BgElevated, out var parsedElevated)
                ? parsedElevated
                : _baseBgElevated;

            var elevatedWithAlpha = hasCustomTheme
                ? baseElevated.WithAlpha((float)Math.Clamp(opacity + 0.08, 0.25, 1.0))
                : baseElevated;
            SetResourceValue(appResources, "BgElevated", elevatedWithAlpha);

            SetResourceValue(appResources, "BgInput", _baseBgInput);

            CardOpacityChanged?.Invoke(opacity);
        }
    }

    public void ResetToDefault()
    {
        void CommitResetOnMainThread()
        {
            ActiveTheme = null;
            ActiveThemeFolderPath = null;
            Preferences.Default.Set(ActiveThemeKey, DefaultThemePreferenceValue);
            ThemeAudioService.Reset();
            ResetDefaultResources();
            OnThemeReset?.Invoke();
        }

        if (MainThread.IsMainThread)
        {
            CommitResetOnMainThread();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(CommitResetOnMainThread);
        }
    }

    public async Task ResetToDefaultAsync()
    {
        await NotifyTransitionStartingAsync("Сброс темы...", "Возвращение к системному оформлению").ConfigureAwait(false);
        await MainThread.InvokeOnMainThreadAsync(ResetToDefault).ConfigureAwait(false);
    }
}
