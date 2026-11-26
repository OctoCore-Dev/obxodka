namespace obxodka.Pages.Vpn;

//процедурные шейдеры skia, анимации лоадера, реактора, кристаллов и ауры
public sealed partial class VpnView
{
    //обработка одного кадра анимации лоадера и ауры
    private void StepLoaderFrame()
    {
        if (_disposed || !_isTabVisible || !_isWindowVisible)
        {
            return;
        }

        _loaderAngle += 1.6f;
        if (CustomButtonRingImage is { IsVisible: true })
        {
            CustomButtonRingImage.Rotation = _loaderAngle * 0.45f % 360f;
        }

        ButtonEffectsCanvas?.InvalidateSurface();
    }

    //управление паузой и возобновлением всех vsync-циклов вкладки vpn
    public void OnTabVisibilityChanged(bool isVisible)
    {
        _isTabVisible = isVisible;
        SyncAnimationLoops();
    }

    //синхронизация всех циклов рендеринга с видимостью вкладки и окна
    private void SyncAnimationLoops()
    {
        var canRender = !_disposed && _isTabVisible && _isWindowVisible;
        if (!canRender)
        {
            StopLoaderLoop();
            PauseGraphLoop();
            StopButtonAnimation();
            return;
        }

        if (_loaderShouldRun || _auraPulseActive)
        {
            EnsureLoaderLoopRunning();
        }

        if (_vpnService?.CurrentState == AppVpnState.Connected)
        {
            ResumeGraphLoop();
        }

        if (CustomButtonAnimatedCanvas.IsVisible && _buttonAnimatedCurrent is not null)
        {
            StartButtonAnimation();
        }

        ButtonEffectsCanvas?.InvalidateSurface();
        TrafficGraphCanvas?.InvalidateSurface();
    }

    //получение цвета темы для графики skia
    private static SKColor GetSkiaThemeColor(string key, SKColor fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c
            ? new SKColor((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255))
            : fallback;

#if WINDOWS
    //аппаратная синхронизация кадров лоадера и ауры через vsync экрана
    private void OnLoaderVsyncRendering(object? sender, object e)
    {
        var nowTicks = Environment.TickCount64;
        if (nowTicks - _loaderLastVsyncTicks < 15)
        {
            return;
        }

        _loaderLastVsyncTicks = nowTicks;
        StepLoaderFrame();
    }
#endif

    //запуск цикла перерисовки единого холста кнопки
    private void EnsureLoaderLoopRunning()
    {
        if (_disposed || !_isTabVisible || !_isWindowVisible)
        {
            return;
        }

        if (!_loaderStopwatch.IsRunning)
        {
            _loaderStopwatch.Start();
        }

#if WINDOWS
        if (!_loaderVsyncActive)
        {
            _loaderLastVsyncTicks = 0;
            _loaderVsyncActive = true;
            Platforms.Windows.WinUiVsync.Subscribe(OnLoaderVsyncRendering);
        }
#else
        if (_loaderTimer is null)
        {
            _loaderTimer = Dispatcher.CreateTimer();
            _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
            _loaderTimer.Tick += (_, _) => StepLoaderFrame();
            _loaderTimer.Start();
        }
#endif
    }

    //остановка цикла перерисовки лоадера
    private void StopLoaderLoop()
    {
#if WINDOWS
        if (_loaderVsyncActive)
        {
            _loaderVsyncActive = false;
            Platforms.Windows.WinUiVsync.Unsubscribe(OnLoaderVsyncRendering);
        }
#else
        _loaderTimer?.Stop();
        _loaderTimer = null;
#endif
    }

    //плавное управление радиальной аурой на едином холсте
    private void SetAuraActive(bool active, bool animate = true)
    {
        _auraPulseActive = active;
        var target = active ? 1f : 0f;

        if (active)
        {
            EnsureLoaderLoopRunning();
        }

        if (!animate || _disposed)
        {
            _ = this.AbortAnimation("AuraFade");
            _auraOpacity = target;
            if (!active && !_loaderShouldRun)
            {
                StopLoaderLoop();
            }

            ButtonEffectsCanvas?.InvalidateSurface();
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _ = this.AbortAnimation("AuraFade");
                var start = _auraOpacity;
                var duration = active ? 400u : 280u;
                var anim = new Animation(v =>
                {
                    _auraOpacity = (float)v;
                    ButtonEffectsCanvas?.InvalidateSurface();
                }, start, target, Easing.CubicOut);
                anim.Commit(this, "AuraFade", 16, duration, Easing.CubicOut, (_, _) =>
                {
                    _auraOpacity = target;
                    if (!_auraPulseActive && !_loaderShouldRun && _loaderOpacity <= 0.005f)
                    {
                        StopLoaderLoop();
                    }

                    ButtonEffectsCanvas?.InvalidateSurface();
                });
            }
            catch
            {
                _auraOpacity = target;
                ButtonEffectsCanvas?.InvalidateSurface();
            }
        });
    }

    //плавное масштабирование слоя лоадера внутри единого холста
    private void AnimateLoaderScale(float target, uint duration = 450, Easing? easing = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _ = this.AbortAnimation("LoaderScale");
                var start = _loaderScale;
                var usedEasing = easing ?? Easing.CubicOut;
                var anim = new Animation(v =>
                {
                    _loaderScale = (float)v;
                    ButtonEffectsCanvas?.InvalidateSurface();
                }, start, target, usedEasing);
                anim.Commit(this, "LoaderScale", 16, duration, usedEasing);
            }
            catch
            {
                _loaderScale = target;
                ButtonEffectsCanvas?.InvalidateSurface();
            }
        });
    }

    //плавное изменение прозрачности слоя лоадера внутри единого холста
    private void AnimateLoaderFade(float target, uint duration = 350, Easing? easing = null, Action? onFinished = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                onFinished?.Invoke();
                return;
            }

            try
            {
                _ = this.AbortAnimation("LoaderFade");
                var start = _loaderOpacity;
                var usedEasing = easing ?? Easing.CubicOut;
                var anim = new Animation(v =>
                {
                    _loaderOpacity = (float)v;
                    ButtonEffectsCanvas?.InvalidateSurface();
                }, start, target, usedEasing);
                anim.Commit(this, "LoaderFade", 16, duration, usedEasing, (_, _) =>
                {
                    _loaderOpacity = target;
                    onFinished?.Invoke();
                    ButtonEffectsCanvas?.InvalidateSurface();
                });
            }
            catch
            {
                _loaderOpacity = target;
                onFinished?.Invoke();
                ButtonEffectsCanvas?.InvalidateSurface();
            }
        });
    }

    //запуск анимации лоадера и таймера частиц
    private void StartLoaderAnimation(bool animateEntrance = true)
    {
        _loaderShouldRun = true;
        _loaderStopwatch.Restart();
        EnsureLoaderLoopRunning();

        if (animateEntrance)
        {
            _ = this.AbortAnimation("LoaderScale");
            _ = this.AbortAnimation("LoaderFade");
            _loaderScale = 0.25f;
            _loaderOpacity = 0f;
            AnimateLoaderScale(1.0f, 450, Easing.CubicOut);
            AnimateLoaderFade(1.0f, 350, Easing.CubicOut);
        }
        else
        {
            _ = this.AbortAnimation("LoaderFade");
            _loaderOpacity = 1f;
            ButtonEffectsCanvas?.InvalidateSurface();
        }
    }

    //остановка лоадера соединения
    private void StopLoaderAnimation(bool animateExit = true)
    {
        _loaderShouldRun = false;
        if (ButtonEffectsCanvas is null || _disposed)
        {
            if (!_auraPulseActive)
            {
                StopLoaderLoop();
                _loaderStopwatch.Reset();
            }

            _loaderOpacity = 0f;
            _loaderScale = 1f;
            return;
        }

        if (animateExit && _loaderOpacity > 0.05f)
        {
            AnimateLoaderScale(0.25f, 350, Easing.CubicIn);
            AnimateLoaderFade(0.0f, 300, Easing.CubicIn, () =>
            {
                if (!_loaderShouldRun && !_auraPulseActive)
                {
                    StopLoaderLoop();
                    _loaderStopwatch.Reset();
                }

                if (!_loaderShouldRun)
                {
                    _loaderScale = 1.0f;
                }
            });
        }
        else
        {
            _ = this.AbortAnimation("LoaderScale");
            _ = this.AbortAnimation("LoaderFade");
            _loaderOpacity = 0f;
            _loaderScale = 1.0f;
            if (!_auraPulseActive)
            {
                StopLoaderLoop();
                _loaderStopwatch.Reset();
            }

            ButtonEffectsCanvas?.InvalidateSurface();
        }
    }

    //плавный переход формы и цвета реактора
    private void AnimateReactorMorph(float target, uint duration = 500)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _ = this.AbortAnimation("ReactorMorph");
                var start = _reactorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _reactorTransitionFactor = (float)v;
                    ButtonEffectsCanvas?.InvalidateSurface();
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
            if (_disposed)
            {
                return;
            }

            try
            {
                _ = this.AbortAnimation("ErrorMorph");
                var start = _errorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _errorTransitionFactor = (float)v;
                    ButtonEffectsCanvas?.InvalidateSurface();
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
        var r = (byte)(from.Red + (to.Red - from.Red) * t);
        var g = (byte)(from.Green + (to.Green - from.Green) * t);
        var b = (byte)(from.Blue + (to.Blue - from.Blue) * t);
        var a = (byte)(from.Alpha + (to.Alpha - from.Alpha) * t);
        return new SKColor(r, g, b, a);
    }

    //запуск кольцевой ударной волны вокруг кнопки
    private void TriggerShockwave(bool isConnect)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _ = this.AbortAnimation("ButtonShockwave");
                _shockwaveIsConnect = isConnect;
                _shockwaveProgress = 0f;
                var anim = new Animation(d =>
                {
                    _shockwaveProgress = (float)d;
                    ButtonEffectsCanvas?.InvalidateSurface();
                }, 0f, 1f, Easing.CubicOut);
                anim.Commit(this, "ButtonShockwave", 16, 650, Easing.CubicOut, (_, _) =>
                {
                    _shockwaveProgress = -1f;
                    ButtonEffectsCanvas?.InvalidateSurface();
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[Shockwave] Ошибка анимации волны: {ex.Message}");
                _shockwaveProgress = -1f;
            }
        });
    }

    //построение центрированного правильного многоугольника через skpathbuilder
    private static SKPath CreateCenteredPolygon(float r, int sides)
    {
        using var builder = new SKPathBuilder();
        for (var i = 0; i < sides; i++)
        {
            var a = (float)(i * 2.0 * Math.PI / sides - Math.PI / 2.0);
            var x = r * MathF.Cos(a);
            var y = r * MathF.Sin(a);
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

    //кэширование многоугольников под текущий радиус кнопки без аллокаций в каждом кадре
    private void EnsurePolygonCache(float r)
    {
        if (Math.Abs(_cachedPolyRadius - r) <= 0.5f && _cachedRuneHexPoly is not null)
        {
            return;
        }

        DisposePolygonCache();
        _cachedPolyRadius = r;
        for (var i = 0; i < s_polyConfigs.Length; i++)
        {
            var (_, _, _, _, size, sides) = s_polyConfigs[i];
            _cachedCrystalPolys[i] = CreateCenteredPolygon(r * size, sides);
        }

        var ringR = r * 0.82f;
        _cachedRuneHexPoly = CreateCenteredPolygon(ringR * 0.86f, 6);
        _cachedRuneTriPoly = CreateCenteredPolygon(ringR * 0.68f, 3);
    }

    //освобождение кэшированных многоугольников
    private void DisposePolygonCache()
    {
        for (var i = 0; i < _cachedCrystalPolys.Length; i++)
        {
            _cachedCrystalPolys[i]?.Dispose();
            _cachedCrystalPolys[i] = null;
        }

        _cachedRuneHexPoly?.Dispose();
        _cachedRuneHexPoly = null;
        _cachedRuneTriPoly?.Dispose();
        _cachedRuneTriPoly = null;
        _cachedPolyRadius = -1f;
    }

    //единая отрисовка радиальной ауры, ударной волны и процедурного лоадера на одном холсте
    private void OnPaintButtonEffectsSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var auraR = Math.Min(cx, cy) - 2f;
        if (auraR > 5f)
        {
            DrawOuterAuraLayer(canvas, cx, cy, auraR);
        }

        var loaderR = Math.Min(cx, cy) - 6f;
        if (loaderR > 12f && _loaderOpacity > 0.005f)
        {
            DrawLoaderLayer(canvas, cx, cy, loaderR);
        }
    }

    //отрисовка слоя процедурного лоадера на едином холсте
    private void DrawLoaderLayer(SKCanvas canvas, float cx, float cy, float r)
    {
        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var loaderStyle = vfx?.ButtonLoaderStyle.Trim().ToLowerInvariant() ?? string.Empty;
        if (loaderStyle == "none")
        {
            return;
        }

        var skPrimary = GetSkiaThemeColor("Primary", s_skPurple);
        var skAccent = GetSkiaThemeColor("Accent", s_skCyan);
        var skError = GetSkiaThemeColor("Error", new SKColor(0xEF, 0x44, 0x44));
        if (_vpnService.CurrentState == AppVpnState.Connected &&
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

        var baseAlpha = Math.Clamp(_loaderOpacity, 0f, 1f);
        _ = canvas.Save();
        if (Math.Abs(_loaderScale - 1f) > 0.001f)
        {
            canvas.Scale(_loaderScale, _loaderScale, cx, cy);
        }

        switch (loaderStyle)
        {
            case "dual_arc":
                DrawDualArcLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, baseAlpha);
                canvas.Restore();
                return;
            case "runes":
                DrawRunesLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, baseAlpha);
                canvas.Restore();
                return;
            case "reactor":
                DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, baseAlpha);
                canvas.Restore();
                return;
        }

        var factor = _reactorTransitionFactor;

        switch (factor)
        {
            case <= 0.001f:
                DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, baseAlpha);
                break;
            case >= 0.999f:
                DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, baseAlpha);
                break;
            default:
                DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, (1f - factor) * baseAlpha);
                DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, factor * baseAlpha);
                break;
        }

        canvas.Restore();
    }

    private void DrawDualArcLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult,
        float alphaMultiplier)
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
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f + 180f, 115f, false, _polyGlowPaint);
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f + 180f, 115f, false, _polyCorePaint);

        _polyGlowPaint.Color = colorPrimary.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyCorePaint);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f + 180f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f + 180f, 85f, false, _polyCorePaint);
    }

    private void DrawRunesLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult,
        float alphaMultiplier)
    {
        EnsurePolygonCache(r);
        var ringR = r * 0.82f;
        var glowAlpha = (byte)Math.Clamp(95 * glowMult * alphaMultiplier, 0, 255);
        var coreAlpha = (byte)Math.Clamp(195 * alphaMultiplier, 0, 255);

        _reactorTrackPaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawCircle(cx, cy, ringR, _reactorTrackPaint);
        canvas.DrawCircle(cx, cy, ringR * 0.86f, _reactorTrackPaint);

        if (_cachedRuneHexPoly is not null)
        {
            _ = canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(_loaderAngle * 0.65f);
            _polyGlowPaint.Color = colorAccent.WithAlpha(glowAlpha);
            _polyCorePaint.Color = colorAccent.WithAlpha(coreAlpha);
            canvas.DrawPath(_cachedRuneHexPoly, _polyGlowPaint);
            canvas.DrawPath(_cachedRuneHexPoly, _polyCorePaint);
            canvas.Restore();
        }

        if (_cachedRuneTriPoly is not null)
        {
            _ = canvas.Save();
            canvas.Translate(cx, cy);
            canvas.RotateDegrees(-_loaderAngle * 0.95f);
            _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
            canvas.DrawPath(_cachedRuneTriPoly, _polyCorePaint);
            canvas.RotateDegrees(60f);
            canvas.DrawPath(_cachedRuneTriPoly, _polyCorePaint);
            canvas.Restore();
        }
    }

    private void DrawQuantumHyperCrystal(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent,
        float glowMult, float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        EnsurePolygonCache(r);
        var ambientAlpha = (byte)Math.Clamp(55 * glowMult * alphaMultiplier, 0, 255);
        if (ambientAlpha > 0)
        {
            _crystalAmbientPaint.Color = colorPrimary.WithAlpha(ambientAlpha);
            canvas.DrawCircle(cx, cy, Math.Max(4f, r - 26f), _crystalAmbientPaint);
        }

        for (var i = 0; i < s_polyConfigs.Length; i++)
        {
            var path = _cachedCrystalPolys[i];
            if (path is null)
            {
                continue;
            }

            var (speed, delay, ox, oy, _, _) = s_polyConfigs[i];
            var rot = _loaderAngle * speed + delay;
            var baseAlpha = 80 + i * 20;
            var glowAlpha = (byte)Math.Clamp(baseAlpha * glowMult * alphaMultiplier, 0, 255);
            var coreAlpha = (byte)Math.Clamp((baseAlpha + 50) * Math.Min(glowMult, 1.2f) * alphaMultiplier, 0, 255);

            var polyColor = i % 2 == 0 ? colorPrimary : colorAccent;

            var pivotX = cx + (ox - 0.5f) * r;
            var pivotY = cy + (oy - 0.5f) * r;

            _ = canvas.Save();
            canvas.Translate(pivotX, pivotY);
            canvas.RotateDegrees(rot);

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

    private void DrawLivingReactor(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult,
        float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        var t = (float)_loaderStopwatch.Elapsed.TotalSeconds;

        var trafficBps = _smoothSpeedDown + _smoothSpeedUp;
        var trafficNormalized = Math.Clamp((float)(trafficBps / (1024.0 * 1024.0 * 2.0)), 0f, 3.5f);
        var dynamicSpeed = 1.2f + trafficNormalized * 2.0f;

        var pulse = MathF.Sin(t * 3f) * 0.12f + 0.88f;
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
            var angleDeg = baseAngle + _loaderAngle * dynamicSpeed * dir;
            var angleRad = angleDeg * (MathF.PI / 180f);

            var sparkX = cx + trackR * MathF.Cos(angleRad);
            var sparkY = cy + trackR * MathF.Sin(angleRad);

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

    //отрисовка слоя радиальной неоновой ауры и ударной волны на едином холсте
    private void DrawOuterAuraLayer(SKCanvas canvas, float cx, float cy, float r)
    {
        if (_auraOpacity <= 0.005f && _shockwaveProgress < 0f)
        {
            return;
        }

        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var isConnected = _vpnService.CurrentState == AppVpnState.Connected;

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

        if (_auraOpacity > 0.005f)
        {
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

            var pulseWave = _auraPulseActive
                ? 0.5f + 0.5f * MathF.Sin((float)_loaderStopwatch.Elapsed.TotalSeconds * (MathF.PI * 2f / 2.5f))
                : 0f;
            var maxScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.05f : 1.15f;
            var auraScale = 1f + (maxScale - 1f) * pulseWave;
            var finalAuraAlpha = (byte)Math.Clamp(_auraOpacity * (0.65f + 0.20f * pulseWave) * 255f, 0f, 255f);

            _ = canvas.Save();
            if (Math.Abs(auraScale - 1f) > 0.001f)
            {
                canvas.Scale(auraScale, auraScale, cx, cy);
            }

            _auraPaint.Color = SKColors.White.WithAlpha(finalAuraAlpha);
            _auraPaint.Shader = _cachedAuraShader;
            canvas.DrawCircle(cx, cy, r, _auraPaint);
            _auraPaint.Shader = null;
            _auraPaint.Color = SKColors.White;
            canvas.Restore();
        }

        if (!(_shockwaveProgress >= 0f))
        {
            return;
        }

        var p = _shockwaveProgress;
        var shockColor = _shockwaveIsConnect ? color : skPrimary;
        var waveAlpha = (byte)Math.Clamp((int)(240f * (1f - p) * glowMult), 0, 255);
        if (waveAlpha <= 0)
        {
            return;
        }

        var waveR1 = r * (0.28f + 0.72f * p);
        _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha);
        _shockwavePaint.StrokeWidth = Math.Max(1f, 5f * (1f - p));
        canvas.DrawCircle(cx, cy, waveR1, _shockwavePaint);

        if (!(p > 0.15f))
        {
            return;
        }

        var p2 = (p - 0.15f) / 0.85f;
        var waveR2 = r * (0.20f + 0.80f * p2);
        var waveAlpha2 = (byte)Math.Clamp((int)(180f * (1f - p2) * glowMult), 0, 255);
        _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha2);
        _shockwavePaint.StrokeWidth = Math.Max(1f, 3.5f * (1f - p2));
        canvas.DrawCircle(cx, cy, waveR2, _shockwavePaint);
    }
}
