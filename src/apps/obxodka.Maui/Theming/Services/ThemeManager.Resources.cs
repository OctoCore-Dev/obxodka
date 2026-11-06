namespace obxodka.Maui.Theming.Services;

public sealed partial class ThemeManager
{
    private Color _baseBgSurface = Color.FromArgb("#161622");
    private Color _baseBgElevated = Color.FromArgb("#1E1E2E");
    private Color _baseBgInput = Color.FromArgb("#12121C");

    private readonly Dictionary<string, object> _defaultResourceSnapshot = [];

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

    //проверка и захват исходных цветов темы оформления
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
                //подавление ошибок захвата ресурсов
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
        catch
        {
            //подавление ошибок применения стилей
        }
    }

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
        catch
        {
            //подавление ошибок сброса ресурсов
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
                //подавление ошибок сброса в основном потоке
            }
        }

        ActiveTheme = null;
        ActiveThemeFolderPath = null;
        Preferences.Default.Set(ActiveThemeKey, DefaultThemePreferenceValue);
        ThemeAudioService.Reset();

        OnThemeReset?.Invoke();
    }

    public async Task ResetToDefaultAsync()
    {
        if (MainThread.IsMainThread)
        {
            ResetToDefault();
            return;
        }

        await MainThread.InvokeOnMainThreadAsync(ResetToDefault).ConfigureAwait(false);
    }
}
