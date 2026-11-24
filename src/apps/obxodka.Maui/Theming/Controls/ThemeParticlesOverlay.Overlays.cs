namespace obxodka.Maui.Theming.Controls;

public sealed partial class ThemeParticlesOverlay
{
    //выбор и отрисовка полноэкранного фонового оверлея
    private void DrawScreenOverlay(SKCanvas canvas, float w, float h, string overlay)
    {
        if (string.IsNullOrEmpty(overlay) || overlay.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!(ThemeManager.Instance?.FramesEnabled ?? true))
        {
            return;
        }

        //отрисовка пользовательского растрового оверлея из папки загруженной темы
        if (_screenOverlayBitmap is not null)
        {
            _ = canvas.Save();
            var srcRect = new SKRect(0, 0, _screenOverlayBitmap.Width, _screenOverlayBitmap.Height);
            var destRect = new SKRect(0, 0, w, h);
            _bitmapPaint.Color = SKColors.White.WithAlpha((byte)Math.Clamp(_screenOverlayOpacity * 255f, 10f, 255f));
            canvas.DrawBitmap(_screenOverlayBitmap, srcRect, destRect, s_samplingOptions, _bitmapPaint);
            canvas.Restore();
            return;
        }

        //динамический выбор процедурного оверлея в цветах активной темы
        if (overlay.Contains("vignette", StringComparison.OrdinalIgnoreCase))
        {
            var tint = _hasCustomTint ? _customTintColor : _accentSkColor;
            DrawVignetteOverlay(canvas, w, h, tint);
        }
        else if (overlay.Contains("hud", StringComparison.OrdinalIgnoreCase) ||
                 overlay.Contains("cyber", StringComparison.OrdinalIgnoreCase))
        {
            DrawCyberHudOverlay(canvas, w, h);
        }
        else if (overlay.Contains("scanline", StringComparison.OrdinalIgnoreCase) ||
                 overlay.Contains("crt", StringComparison.OrdinalIgnoreCase))
        {
            DrawScanlinesOverlay(canvas, w, h, 4f, 16);
        }
        else if (overlay.Contains("aurora", StringComparison.OrdinalIgnoreCase) ||
                 overlay.Contains("wave", StringComparison.OrdinalIgnoreCase))
        {
            DrawAuroraOverlay(canvas, w, h);
        }
        else if (overlay.Contains("fog", StringComparison.OrdinalIgnoreCase) ||
                 overlay.Contains("mist", StringComparison.OrdinalIgnoreCase))
        {
            DrawMysticFogOverlay(canvas, w, h);
        }
        else if (overlay.Contains("ray", StringComparison.OrdinalIgnoreCase) ||
                 overlay.Contains("beam", StringComparison.OrdinalIgnoreCase))
        {
            var tint = _hasCustomTint ? _customTintColor : _accentSkColor;
            DrawRaysOverlay(canvas, w, h, tint);
        }
    }

    //отрисовка кинескопных линий сканирования
    private void DrawScanlinesOverlay(SKCanvas canvas, float w, float h, float step, byte lineAlpha)
    {
        _strokePaint.Color = new SKColor(0, 0, 0, lineAlpha);
        _strokePaint.StrokeWidth = 1f;
        for (var y = 0f; y < h; y += step)
        {
            canvas.DrawLine(0, y, w, y, _strokePaint);
        }
    }

    //отрисовка киберпанк интерфейса с лучом сканера и угловыми прицелами (zero-allocation shader)
    private void DrawCyberHudOverlay(SKCanvas canvas, float w, float h)
    {
        DrawScanlinesOverlay(canvas, w, h, 5f, 14);

        if (_cyberBeamShader is null || _lastCyberAccent != _accentSkColor)
        {
            _cyberBeamShader?.Dispose();
            _cyberBeamShader = SKShader.CreateLinearGradient(
                new SKPoint(0, -35f),
                new SKPoint(0, 35f),
                [_accentSkColor.WithAlpha(0), _accentSkColor.WithAlpha(28), _accentSkColor.WithAlpha(0)],
                [0f, 0.5f, 1f],
                SKShaderTileMode.Clamp);
            _lastCyberAccent = _accentSkColor;
        }

        var scanY = _shaderTime * 90f % (h + 120f) - 60f;
        _ = canvas.Save();
        canvas.Translate(0, scanY);
        _shaderPaint.Color = SKColors.White;
        _shaderPaint.Shader = _cyberBeamShader;
        canvas.DrawRect(0, -35f, w, 70f, _shaderPaint);
        _shaderPaint.Shader = null;
        canvas.Restore();

        const float margin = 16f;
        const float len = 26f;
        _strokePaint.Color = _accentSkColor.WithAlpha(95);
        _strokePaint.StrokeWidth = 1.6f;

        canvas.DrawLine(margin, margin, margin + len, margin, _strokePaint);
        canvas.DrawLine(margin, margin, margin, margin + len, _strokePaint);

        canvas.DrawLine(w - margin - len, margin, w - margin, margin, _strokePaint);
        canvas.DrawLine(w - margin, margin, w - margin, margin + len, _strokePaint);

        canvas.DrawLine(margin, h - margin, margin + len, h - margin, _strokePaint);
        canvas.DrawLine(margin, h - margin, margin, h - margin - len, _strokePaint);

        canvas.DrawLine(w - margin - len, h - margin, w - margin, h - margin, _strokePaint);
        canvas.DrawLine(w - margin, h - margin, w - margin, h - margin - len, _strokePaint);
    }

    //отрисовка северного сияния с волновым градиентом (zero-allocation path reuse)
    private void DrawAuroraOverlay(SKCanvas canvas, float w, float h)
    {
        var ribbonHeight = Math.Min(h * 0.38f, 260f);
        if (_auroraRibbonShader0 is null ||
            _auroraRibbonShader1 is null ||
            Math.Abs(_lastAuroraHeight - ribbonHeight) > 0.5f ||
            _lastAuroraAccent != _accentSkColor ||
            _lastAuroraPrimary != _primarySkColor)
        {
            _auroraRibbonShader0?.Dispose();
            _auroraRibbonShader1?.Dispose();
            _auroraRibbonShader0 = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, ribbonHeight),
                [_accentSkColor.WithAlpha(48), _accentSkColor.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _auroraRibbonShader1 = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, ribbonHeight),
                [_primarySkColor.WithAlpha(34), _primarySkColor.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _lastAuroraHeight = ribbonHeight;
            _lastAuroraAccent = _accentSkColor;
            _lastAuroraPrimary = _primarySkColor;
        }

        _shaderPaint.Color = SKColors.White;
        for (var layer = 0; layer < 2; layer++)
        {
            _wavePathBuilder.Reset();
            var phaseOffset = _shaderTime * (0.9f + layer * 0.4f) + layer * 1.7f;

            _wavePathBuilder.MoveTo(0, 0);
            const int segments = 16;
            for (var i = 0; i <= segments; i++)
            {
                var x = w * i / segments;
                var wave = MathF.Sin(i * 0.45f + phaseOffset) * 28f +
                           MathF.Cos(i * 0.25f - phaseOffset * 0.7f) * 18f;
                var y = ribbonHeight * (0.55f + layer * 0.18f) + wave;
                _wavePathBuilder.LineTo(x, y);
            }

            _wavePathBuilder.LineTo(w, 0);
            _wavePathBuilder.Close();

            using var wavePath = _wavePathBuilder.Detach();
            _shaderPaint.Shader = layer == 0 ? _auroraRibbonShader0 : _auroraRibbonShader1;
            canvas.DrawPath(wavePath, _shaderPaint);
            _shaderPaint.Shader = null;
        }
    }

    //отрисовка динамической пульсирующей виньетки в цветах активной темы (zero-allocation cached shader)
    private void DrawVignetteOverlay(SKCanvas canvas, float w, float h, SKColor themeColor)
    {
        if (_vignetteShader is null ||
            Math.Abs(_lastVignetteW - w) > 1f ||
            Math.Abs(_lastVignetteH - h) > 1f ||
            _lastVignetteColor != themeColor)
        {
            _vignetteShader?.Dispose();
            var center = new SKPoint(w * 0.5f, h * 0.5f);
            var radius = MathF.Max(w, h) * 0.72f;
            var opaqueTheme = themeColor.WithAlpha(255);
            _vignetteShader = SKShader.CreateRadialGradient(
                center,
                radius,
                [SKColors.Transparent, opaqueTheme.WithAlpha(89), opaqueTheme],
                [0.45f, 0.80f, 1.0f],
                SKShaderTileMode.Clamp);
            _lastVignetteW = w;
            _lastVignetteH = h;
            _lastVignetteColor = themeColor;
        }

        var pulse = 0.5f + 0.5f * MathF.Sin(_shaderTime * 2.2f);
        var boost = _vpnState == AppVpnState.Connected ? 1.35f : 1.0f;
        var edgeAlpha = (byte)Math.Clamp((40f + pulse * 26f) * boost, 0f, 140f);

        _shaderPaint.Color = SKColors.White.WithAlpha(edgeAlpha);
        _shaderPaint.Shader = _vignetteShader;
        canvas.DrawRect(0, 0, w, h, _shaderPaint);
        _shaderPaint.Shader = null;
        _shaderPaint.Color = SKColors.White;
    }

    //отрисовка стелющегося мистического тумана (zero-allocation cached shader + path)
    private void DrawMysticFogOverlay(SKCanvas canvas, float w, float h)
    {
        var fogHeight = Math.Min(h * 0.32f, 220f);
        var topY = h - fogHeight;

        if (_fogShader0 is null ||
            _fogShader1 is null ||
            Math.Abs(_lastFogTopY - topY) > 1f ||
            Math.Abs(_lastFogH - h) > 1f ||
            _lastFogPrimary != _primarySkColor ||
            _lastFogAccent != _accentSkColor)
        {
            _fogShader0?.Dispose();
            _fogShader1?.Dispose();
            var fogColor0 = _primarySkColor.WithAlpha(42);
            var fogColor1 = _accentSkColor.WithAlpha(30);
            _fogShader0 = SKShader.CreateLinearGradient(
                new SKPoint(0, topY),
                new SKPoint(0, h),
                [fogColor0.WithAlpha(0), fogColor0],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _fogShader1 = SKShader.CreateLinearGradient(
                new SKPoint(0, topY),
                new SKPoint(0, h),
                [fogColor1.WithAlpha(0), fogColor1],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _lastFogTopY = topY;
            _lastFogH = h;
            _lastFogPrimary = _primarySkColor;
            _lastFogAccent = _accentSkColor;
        }

        _shaderPaint.Color = SKColors.White;
        for (var layer = 0; layer < 2; layer++)
        {
            _wavePathBuilder.Reset();
            var phase = _shaderTime * (0.65f + layer * 0.35f) + layer * 2.1f;
            _wavePathBuilder.MoveTo(0, h);

            const int segments = 16;
            for (var i = 0; i <= segments; i++)
            {
                var x = w * i / segments;
                var offset = MathF.Sin(i * 0.4f + phase) * 20f;
                var y = topY + layer * 28f + offset;
                _wavePathBuilder.LineTo(x, y);
            }

            _wavePathBuilder.LineTo(w, h);
            _wavePathBuilder.Close();

            using var fogPath = _wavePathBuilder.Detach();
            _shaderPaint.Shader = layer == 0 ? _fogShader0 : _fogShader1;
            canvas.DrawPath(fogPath, _shaderPaint);
            _shaderPaint.Shader = null;
        }
    }

    //отрисовка нисходящих лучей света в цветах активной темы с кэшированным контуром и шейдером
    private void DrawRaysOverlay(SKCanvas canvas, float w, float h, SKColor themeColor)
    {
        var originX = w * 0.5f;
        const float originY = -30f;
        var rayLength = MathF.Max(w, h) * 0.85f;
        const float spread = 0.09f;

        if (_rayUnitPath is null || Math.Abs(_lastRayLength - rayLength) > 1f)
        {
            _rayUnitPath?.Dispose();
            using var builder = new SKPathBuilder();
            builder.MoveTo(0f, 0f);
            builder.LineTo(MathF.Cos(-spread) * rayLength, MathF.Sin(-spread) * rayLength);
            builder.LineTo(MathF.Cos(spread) * rayLength, MathF.Sin(spread) * rayLength);
            builder.Close();
            _rayUnitPath = builder.Detach();
        }

        if (_rayShader is null ||
            Math.Abs(_lastRayLength - rayLength) > 1f ||
            _lastRayColor != themeColor)
        {
            _rayShader?.Dispose();
            _rayShader = SKShader.CreateLinearGradient(
                new SKPoint(0f, 0f),
                new SKPoint(rayLength * 0.75f, 0f),
                [themeColor.WithAlpha(255), themeColor.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _lastRayLength = rayLength;
            _lastRayColor = themeColor;
        }

        _shaderPaint.Shader = _rayShader;
        for (var i = -2; i <= 2; i++)
        {
            var baseAngle = MathF.PI * 0.5f + i * 0.22f + MathF.Sin(_shaderTime * 0.7f + i) * 0.05f;
            var alpha = (byte)(26 - Math.Abs(i) * 4);
            _shaderPaint.Color = SKColors.White.WithAlpha(alpha);

            _ = canvas.Save();
            canvas.Translate(originX, originY);
            canvas.RotateRadians(baseAngle);
            canvas.DrawPath(_rayUnitPath, _shaderPaint);
            canvas.Restore();
        }

        _shaderPaint.Shader = null;
        _shaderPaint.Color = SKColors.White;
    }


    //генерация векторного пути кристалла ромба
    private static SKPath CreateDiamondPath()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0, -10);
        builder.LineTo(6, 0);
        builder.LineTo(0, 10);
        builder.LineTo(-6, 0);
        builder.Close();
        return builder.Detach();
    }

    //генерация векторного пути звезды
    private static SKPath CreateStarPath()
    {
        using var builder = new SKPathBuilder();
        const int points = 5;
        const float outerRadius = 10f;
        const float innerRadius = 4f;

        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            var angle = i * Math.PI / points - Math.PI / 2;
            var x = (float)(radius * Math.Cos(angle));
            var y = (float)(radius * Math.Sin(angle));

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

    //генерация векторного пути лепестка
    private static SKPath CreatePetalPath()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0, -10);
        builder.CubicTo(6, -4, 8, 5, 0, 10);
        builder.CubicTo(-8, 5, -6, -4, 0, -10);
        builder.Close();
        return builder.Detach();
    }
}
