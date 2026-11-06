//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

//процедурные шейдеры skia, анимации лоадера, реактора, кристаллов и ауры
public sealed partial class VpnView
{
    //запуск анимации лоадера и таймера частиц
    private void StartLoaderAnimation(bool animateEntrance = true)
    {
        _loaderStopwatch.Restart();
        if (_loaderTimer is null)
        {
            _loaderTimer = Dispatcher.CreateTimer();
            _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
            _loaderTimer.Tick += (_, _) =>
            {
                _loaderAngle += 1.6f;
                if (CustomButtonRingImage is { IsVisible: true })
                {
                    CustomButtonRingImage.Rotation = _loaderAngle * 0.45f % 360f;
                }
                LoaderCanvas?.InvalidateSurface();
            };
            _loaderTimer.Start();
        }

        LoaderCanvas.IsVisible = true;

        if (animateEntrance)
        {
            try
            {
                _ = LoaderCanvas.AbortAnimation("ScaleTo");
                _ = LoaderCanvas.AbortAnimation("FadeTo");
            }
            catch
            {
            }

            LoaderCanvas.Scale = 0.25;
            LoaderCanvas.Opacity = 0.0;
            SafeScaleTo(LoaderCanvas, 1.0, 450, Easing.CubicOut);
            SafeFadeTo(LoaderCanvas, 1.0, 350, Easing.CubicOut);
        }
        else
        {
            LoaderCanvas.Opacity = 1;
        }
    }

    //остановка лоадера соединения
    private void StopLoaderAnimation(bool animateExit = true)
    {
        if (LoaderCanvas is null)
        {
            _loaderTimer?.Stop();
            _loaderTimer = null;
            _loaderStopwatch.Reset();
            return;
        }

        if (animateExit && LoaderCanvas.IsVisible && LoaderCanvas.Opacity > 0.05)
        {
            try
            {
                _ = LoaderCanvas.AbortAnimation("ScaleTo");
                _ = LoaderCanvas.AbortAnimation("FadeTo");
            }
            catch
            {
            }

            SafeScaleTo(LoaderCanvas, 0.25, 350, Easing.CubicIn);
            SafeFadeTo(LoaderCanvas, 0.0, 300, Easing.CubicIn, () =>
            {
                LoaderCanvas.IsVisible = false;
                _loaderTimer?.Stop();
                _loaderTimer = null;
                _loaderStopwatch.Reset();
            });
        }
        else
        {
            _loaderTimer?.Stop();
            _loaderTimer = null;
            _loaderStopwatch.Reset();
            LoaderCanvas.IsVisible = false;
            LoaderCanvas.Opacity = 0;
            LoaderCanvas.Scale = 1.0;
        }
    }

    //плавный переход формы и цвета реактора
    private void AnimateReactorMorph(float target, uint duration = 500)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ReactorMorph");
                var start = _reactorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _reactorTransitionFactor = (float)v;
                    LoaderCanvas?.InvalidateSurface();
                }, start, target, Easing.CubicInOut);
                anim.Commit(this, "ReactorMorph", 16, duration, Easing.CubicInOut);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[ReactorMorph] Ошибка анимации реактора: {ex.Message}");
                _reactorTransitionFactor = target;
            }
        });
    }

    //морфинг индикатора ошибки
    private void AnimateErrorMorph(float target, uint duration = 400)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ErrorMorph");
                var start = _errorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _errorTransitionFactor = (float)v;
                    LoaderCanvas?.InvalidateSurface();
                    OuterAura?.InvalidateSurface();
                }, start, target, Easing.CubicOut);
                anim.Commit(this, "ErrorMorph", 16, duration, Easing.CubicOut);
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[ErrorMorph] Ошибка анимации ошибки: {ex.Message}");
                _errorTransitionFactor = target;
            }
        });
    }

    //линейная интерполяция двух цветов
    private static SKColor LerpColor(SKColor from, SKColor to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var r = (byte)(from.Red + ((to.Red - from.Red) * t));
        var g = (byte)(from.Green + ((to.Green - from.Green) * t));
        var b = (byte)(from.Blue + ((to.Blue - from.Blue) * t));
        var a = (byte)(from.Alpha + ((to.Alpha - from.Alpha) * t));
        return new SKColor(r, g, b, a);
    }

    //безопасное масштабирование элемента интерфейса
    private static void SafeScaleTo(VisualElement? element, double scale, uint length = 250, Easing? easing = null)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            if (element.Handler is not null && element.IsLoaded && element.Window is not null)
            {
                _ = element.ScaleToAsync(scale, length, easing ?? Easing.Linear);
            }
            else
            {
                element.Scale = scale;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[SafeScaleTo] Предотвращена анимация удаленного элемента: {ex.Message}");
        }
    }

    private static void SafeFadeTo(VisualElement? element, double opacity, uint length = 250, Easing? easing = null, Action? onCompleted = null)
    {
        if (element is null)
        {
            onCompleted?.Invoke();
            return;
        }

        try
        {
            if (element.Handler is not null && element.IsLoaded && element.Window is not null)
            {
                _ = element.FadeToAsync(opacity, length, easing ?? Easing.Linear).ContinueWith(_ =>
                {
                    if (onCompleted is not null)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                onCompleted();
                            }
                            catch
                            {
                            }
                        });
                    }
                }, TaskScheduler.Default);
            }
            else
            {
                element.Opacity = opacity;
                onCompleted?.Invoke();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[SafeFadeTo] Предотвращена анимация удаленного элемента: {ex.Message}");
            onCompleted?.Invoke();
        }
    }

    //запуск кольцевой ударной волны вокруг кнопки
    private void TriggerShockwave(bool isConnect)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ButtonShockwave");
                _shockwaveIsConnect = isConnect;
                _shockwaveProgress = 0f;
                var anim = new Animation(v =>
                {
                    _shockwaveProgress = (float)v;
                    OuterAura?.InvalidateSurface();
                }, 0f, 1f, Easing.CubicOut);
                anim.Commit(this, "ButtonShockwave", 16, 650, Easing.CubicOut, (v, c) =>
                {
                    _shockwaveProgress = -1f;
                    OuterAura?.InvalidateSurface();
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[Shockwave] Ошибка анимации волны: {ex.Message}");
                _shockwaveProgress = -1f;
            }
        });
    }

    //построение контура правильного многоугольника
    private static SKPath MakePolygon(float cx, float cy, float r, int sides)
    {
        using var builder = new SKPathBuilder();
        for (var i = 0; i < sides; i++)
        {
            var a = (float)((i * 2 * Math.PI / sides) - (Math.PI / 2));
            var x = cx + (r * MathF.Cos(a));
            var y = cy + (r * MathF.Sin(a));
            if (i == 0)
            {
                builder.MoveTo(x, y);
            }
            else
            {
                builder.LineTo(x, y);
            }
        }
        builder.Close();
        return builder.Detach();
    }

    //отрисовка процедурного лоадера на холсте skia
    private void OnPaintLoaderSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_loaderTimer is null && _vpnService?.CurrentState != AppVpnState.Connected)
        {
            return;
        }

        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var loaderStyle = vfx?.ButtonLoaderStyle?.Trim().ToLowerInvariant() ?? string.Empty;
        if (loaderStyle == "none")
        {
            return;
        }

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 6f;
        if (r <= 12f)
        {
            return;
        }

        var skPrimary = GetSkiaThemeColor("Primary", s_skPurple);
        var skAccent = GetSkiaThemeColor("Accent", s_skCyan);
        var skError = GetSkiaThemeColor("Error", new SKColor(0xEF, 0x44, 0x44));
        if (_vpnService?.CurrentState == AppVpnState.Connected &&
            !string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.AccentColor) &&
            SKColor.TryParse(vfx.ActiveOverrides.AccentColor, out var ovAccent))
        {
            skAccent = ovAccent;
        }

        var colorPrimary = LerpColor(skPrimary, skError, _errorTransitionFactor);
        var colorAccent = LerpColor(skAccent, skError, _errorTransitionFactor);
        var glowMult = (float)ThemeManager.CurrentGlowIntensity;
        if (vfx?.Glow?.Intensity is > 0)
        {
            glowMult *= (float)vfx.Glow.Intensity;
        }

        if (loaderStyle == "dual_arc")
        {
            DrawDualArcLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

        if (loaderStyle == "runes")
        {
            DrawRunesLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

        if (loaderStyle == "reactor")
        {
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

        var factor = _reactorTransitionFactor;

        if (factor <= 0.001f)
        {
            DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
        }
        else if (factor >= 0.999f)
        {
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
        }
        else
        {
            DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f - factor);
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, factor);
        }
    }

    private void DrawDualArcLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        var outerR = r * 0.84f;
        var innerR = r * 0.66f;
        var outerRect = new SKRect(cx - outerR, cy - outerR, cx + outerR, cy + outerR);
        var innerRect = new SKRect(cx - innerR, cy - innerR, cx + innerR, cy + innerR);

        var glowAlpha = (byte)Math.Clamp(110 * glowMult * alphaMultiplier, 0, 255);
        var coreAlpha = (byte)Math.Clamp(220 * alphaMultiplier, 0, 255);

        _polyGlowPaint.Color = colorAccent.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorAccent.WithAlpha(coreAlpha);
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f, 115f, false, _polyGlowPaint);
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f, 115f, false, _polyCorePaint);
        canvas.DrawArc(outerRect, (_loaderAngle * 1.4f) + 180f, 115f, false, _polyGlowPaint);
        canvas.DrawArc(outerRect, (_loaderAngle * 1.4f) + 180f, 115f, false, _polyCorePaint);

        _polyGlowPaint.Color = colorPrimary.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyCorePaint);
        canvas.DrawArc(innerRect, (-_loaderAngle * 1.9f) + 180f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, (-_loaderAngle * 1.9f) + 180f, 85f, false, _polyCorePaint);
    }

    private void DrawRunesLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        var ringR = r * 0.82f;
        var glowAlpha = (byte)Math.Clamp(95 * glowMult * alphaMultiplier, 0, 255);
        var coreAlpha = (byte)Math.Clamp(195 * alphaMultiplier, 0, 255);

        _reactorTrackPaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawCircle(cx, cy, ringR, _reactorTrackPaint);
        canvas.DrawCircle(cx, cy, ringR * 0.86f, _reactorTrackPaint);

        _ = canvas.Save();
        canvas.RotateDegrees(_loaderAngle * 0.65f, cx, cy);
        using var hexPath = MakePolygon(cx, cy, ringR * 0.86f, 6);
        _polyGlowPaint.Color = colorAccent.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorAccent.WithAlpha(coreAlpha);
        canvas.DrawPath(hexPath, _polyGlowPaint);
        canvas.DrawPath(hexPath, _polyCorePaint);
        canvas.Restore();

        _ = canvas.Save();
        canvas.RotateDegrees(-_loaderAngle * 0.95f, cx, cy);
        using var triPath1 = MakePolygon(cx, cy, ringR * 0.68f, 3);
        _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawPath(triPath1, _polyCorePaint);
        canvas.RotateDegrees(60f, cx, cy);
        canvas.DrawPath(triPath1, _polyCorePaint);
        canvas.Restore();
    }

    private void DrawQuantumHyperCrystal(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        var ambientAlpha = (byte)Math.Clamp(55 * glowMult * alphaMultiplier, 0, 255);
        if (ambientAlpha > 0)
        {
            _crystalAmbientPaint.Color = colorPrimary.WithAlpha(ambientAlpha);
            canvas.DrawCircle(cx, cy, Math.Max(4f, r - 26f), _crystalAmbientPaint);
        }

        for (var i = 0; i < s_polyConfigs.Length; i++)
        {
            var (speed, delay, ox, oy, size, sides) = s_polyConfigs[i];
            var rot = (_loaderAngle * speed) + delay;
            var baseAlpha = 80 + (i * 20);
            var glowAlpha = (byte)Math.Clamp(baseAlpha * glowMult * alphaMultiplier, 0, 255);
            var coreAlpha = (byte)Math.Clamp((baseAlpha + 50) * Math.Min(glowMult, 1.2f) * alphaMultiplier, 0, 255);

            var polyColor = i % 2 == 0 ? colorPrimary : colorAccent;

            var pivotX = cx + ((ox - 0.5f) * r);
            var pivotY = cy + ((oy - 0.5f) * r);

            _ = canvas.Save();
            canvas.RotateDegrees(rot, pivotX, pivotY);

            using var path = MakePolygon(pivotX, pivotY, r * size, sides);

            if (glowAlpha > 0)
            {
                _polyGlowPaint.Color = polyColor.WithAlpha(glowAlpha);
                canvas.DrawPath(path, _polyGlowPaint);
            }

            _polyCorePaint.Color = polyColor.WithAlpha(coreAlpha);
            canvas.DrawPath(path, _polyCorePaint);

            canvas.Restore();
        }
    }

    private void DrawLivingReactor(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        var t = (float)_loaderStopwatch.Elapsed.TotalSeconds;

        var trafficBps = _smoothSpeedDown + _smoothSpeedUp;
        var trafficNormalized = Math.Clamp((float)(trafficBps / (1024.0 * 1024.0 * 2.0)), 0f, 3.5f);
        var dynamicSpeed = 1.2f + (trafficNormalized * 2.0f);

        var pulse = (MathF.Sin(t * 3f) * 0.12f) + 0.88f;
        var coreR = Math.Max(6f, r * 0.42f * pulse);

        var coreAlpha = (byte)Math.Clamp(50 * glowMult * pulse * alphaMultiplier, 0, 255);
        if (coreAlpha > 0)
        {
            _crystalAmbientPaint.Color = colorAccent.WithAlpha(coreAlpha);
            canvas.DrawCircle(cx, cy, coreR, _crystalAmbientPaint);
        }

        var trackR1 = r * 0.82f;
        var trackR2 = r * 0.58f;

        _reactorTrackPaint.Color = colorPrimary.WithAlpha((byte)Math.Clamp(35 * glowMult * alphaMultiplier, 0, 255));
        canvas.DrawCircle(cx, cy, trackR1, _reactorTrackPaint);

        _reactorTrackPaint.Color = colorAccent.WithAlpha((byte)Math.Clamp(25 * glowMult * alphaMultiplier, 0, 255));
        canvas.DrawCircle(cx, cy, trackR2, _reactorTrackPaint);

        for (var i = 0; i < 5; i++)
        {
            var baseAngle = i * (360f / 5f);
            var isOuter = i % 2 == 0;
            var trackR = isOuter ? trackR1 : trackR2;
            var dir = isOuter ? 1f : -1.25f;
            var angleDeg = baseAngle + (_loaderAngle * dynamicSpeed * dir);
            var angleRad = angleDeg * (MathF.PI / 180f);

            var sparkX = cx + (trackR * MathF.Cos(angleRad));
            var sparkY = cy + (trackR * MathF.Sin(angleRad));

            var sparkColor = isOuter ? colorAccent : colorPrimary;
            var sparkAlpha = (byte)Math.Clamp(200 * glowMult * alphaMultiplier, 0, 255);

            if (sparkAlpha > 0)
            {
                _reactorSparkGlowPaint.Color = sparkColor.WithAlpha((byte)Math.Clamp(sparkAlpha * 0.7f, 0, 255));
                canvas.DrawCircle(sparkX, sparkY, 6f, _reactorSparkGlowPaint);
            }

            _reactorSparkPaint.Color = SKColors.White.WithAlpha(sparkAlpha);
            canvas.DrawCircle(sparkX, sparkY, 2.6f, _reactorSparkPaint);
        }
    }

    //отрисовка радиальной неоновой ауры вокруг кнопки
    private void OnPaintOuterAuraSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 2f;
        if (r <= 5f)
        {
            return;
        }

        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var isConnected = _vpnService?.CurrentState == AppVpnState.Connected;

        var skAccent = GetSkiaThemeColor("Accent", s_skCyan);
        var skPrimary = GetSkiaThemeColor("Primary", s_skPurple);
        var skError = GetSkiaThemeColor("Error", new SKColor(0xEF, 0x44, 0x44));

        var coreGlowColor = skAccent;
        if (!string.IsNullOrWhiteSpace(vfx?.Glow?.CoreColor) && SKColor.TryParse(vfx.Glow.CoreColor, out var parsedCore))
        {
            coreGlowColor = parsedCore;
        }
        var bloomGlowColor = !string.IsNullOrWhiteSpace(vfx?.Glow?.BloomColor) && SKColor.TryParse(vfx.Glow.BloomColor, out var parsedBloom)
            ? parsedBloom
            : coreGlowColor;

        if (isConnected)
        {
            if (!string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.AccentColor) &&
                SKColor.TryParse(vfx.ActiveOverrides.AccentColor, out var ovAccent))
            {
                coreGlowColor = ovAccent;
            }
            if (!string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.GlowColor) &&
                SKColor.TryParse(vfx.ActiveOverrides.GlowColor, out var ovGlow))
            {
                bloomGlowColor = ovGlow;
            }
        }

        var targetColor = _isErrorState ? skError : coreGlowColor;
        var color = LerpColor(coreGlowColor, targetColor, Math.Max(_isErrorState ? 1f : 0f, _errorTransitionFactor));
        var bloomColor = LerpColor(bloomGlowColor, targetColor, Math.Max(_isErrorState ? 1f : 0f, _errorTransitionFactor));

        var glowMult = (float)ThemeManager.CurrentGlowIntensity;
        if (vfx?.Glow?.Intensity is > 0)
        {
            glowMult *= (float)vfx.Glow.Intensity;
        }

        var baseAlpha = (byte)(isConnected ? 95 : 65);
        var midAlpha = (byte)(isConnected ? 35 : 20);

        if (_cachedAuraShader == null ||
            Math.Abs(_lastAuraCx - cx) > 0.5f ||
            Math.Abs(_lastAuraCy - cy) > 0.5f ||
            Math.Abs(_lastAuraR - r) > 0.5f ||
            Math.Abs(_lastAuraGlow - glowMult) > 0.01f ||
            _lastAuraColor != color ||
            _lastAuraBloomColor != bloomColor ||
            _lastAuraBaseAlpha != baseAlpha ||
            _lastAuraMidAlpha != midAlpha)
        {
            _cachedAuraShader?.Dispose();
            _lastAuraCx = cx;
            _lastAuraCy = cy;
            _lastAuraR = r;
            _lastAuraGlow = glowMult;
            _lastAuraColor = color;
            _lastAuraBloomColor = bloomColor;
            _lastAuraBaseAlpha = baseAlpha;
            _lastAuraMidAlpha = midAlpha;

            _cachedAuraShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r,
                [
                    color.WithAlpha((byte)Math.Clamp(baseAlpha * glowMult, 0, 255)),
                    bloomColor.WithAlpha((byte)Math.Clamp(midAlpha * glowMult, 0, 255)),
                    SKColors.Transparent
                ],
                [0f, 0.65f, 1f],
                SKShaderTileMode.Clamp);
        }

        _auraPaint.Shader = _cachedAuraShader;
        canvas.DrawCircle(cx, cy, r, _auraPaint);
        _auraPaint.Shader = null;

        if (_shockwaveProgress >= 0f)
        {
            var p = _shockwaveProgress;
            var shockColor = _shockwaveIsConnect ? color : skPrimary;
            var waveAlpha = (byte)Math.Clamp((int)(240f * (1f - p) * glowMult), 0, 255);
            if (waveAlpha > 0)
            {
                var waveR1 = r * (0.28f + (0.72f * p));
                _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha);
                _shockwavePaint.StrokeWidth = Math.Max(1f, 5f * (1f - p));
                canvas.DrawCircle(cx, cy, waveR1, _shockwavePaint);

                if (p > 0.15f)
                {
                    var p2 = (p - 0.15f) / 0.85f;
                    var waveR2 = r * (0.20f + (0.80f * p2));
                    var waveAlpha2 = (byte)Math.Clamp((int)(180f * (1f - p2) * glowMult), 0, 255);
                    _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha2);
                    _shockwavePaint.StrokeWidth = Math.Max(1f, 3.5f * (1f - p2));
                    canvas.DrawCircle(cx, cy, waveR2, _shockwavePaint);
                }
            }
        }
    }

    //получение цвета темы для графики skia
    private static SKColor GetSkiaThemeColor(string key, SKColor fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c
            ? new SKColor((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255))
            : fallback;


}
