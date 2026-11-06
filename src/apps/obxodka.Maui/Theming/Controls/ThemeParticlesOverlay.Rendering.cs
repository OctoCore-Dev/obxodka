namespace obxodka.Maui.Theming.Controls;

[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "visual particle physics and rendering")]
public sealed partial class ThemeParticlesOverlay
{
    //отрисовка поверхности canvas
    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (!HasActiveVisuals())
        {
            return;
        }

        var logicalW = Width <= 1 ? e.Info.Width : (float)Width;
        var logicalH = Height <= 1 ? e.Info.Height : (float)Height;

        if (_lastWidth <= 1f && logicalW > 1f && logicalH > 1f && _particles.Count > 0)
        {
            _lastWidth = logicalW;
            _lastHeight = logicalH;
            foreach (var p in _particles)
            {
                p.X = (float)(_random.NextDouble() * logicalW);
                p.Y = (float)(_random.NextDouble() * logicalH);
            }
        }

        var scaleX = e.Info.Width / Math.Max(logicalW, 1f);
        var scaleY = e.Info.Height / Math.Max(logicalH, 1f);

        _ = canvas.Save();
        canvas.Scale(scaleX, scaleY);

        var activeOverlay = GetActiveOverlayName();
        if (!string.IsNullOrWhiteSpace(activeOverlay) && !string.Equals(activeOverlay, "none", StringComparison.OrdinalIgnoreCase))
        {
            DrawScreenOverlay(canvas, logicalW, logicalH, activeOverlay);
        }

        if (_initialized && _particles.Count > 0 && !string.Equals(_particleTypeName, "none", StringComparison.OrdinalIgnoreCase))
        {
            DrawParticles(canvas);
        }

        if (_shockwaveActive)
        {
            DrawShockwave(canvas, logicalW, logicalH);
        }

        canvas.Restore();
    }

    //отрисовка частиц созвездий всплесков и спрайтов
    private void DrawParticles(SKCanvas canvas)
    {
        var connectConstellations = _particles.Count >= 2 &&
            (_connectLines || _screenOverlay is "cyber_hud");

        if (connectConstellations)
        {
            const float maxDist = 85f;
            const float maxDistSq = maxDist * maxDist;
            var lineBaseColor = _accentSkColor;

            for (var i = 0; i < _particles.Count; i++)
            {
                var p1 = _particles[i];
                var connections = 0;
                for (var j = i + 1; j < _particles.Count && connections < 2; j++)
                {
                    var p2 = _particles[j];
                    var dx = p1.X - p2.X;
                    var dy = p1.Y - p2.Y;
                    var distSq = (dx * dx) + (dy * dy);
                    if (distSq < maxDistSq)
                    {
                        var dist = MathF.Sqrt(distSq);
                        var ratio = 1.0f - (dist / maxDist);
                        var lineAlpha = (byte)Math.Clamp(ratio * ratio * 55f * Math.Min(p1.CurrentAlpha, p2.CurrentAlpha), 0f, 255f);
                        if (lineAlpha > 3)
                        {
                            _strokePaint.Color = lineBaseColor.WithAlpha(lineAlpha);
                            _strokePaint.StrokeWidth = 1.0f;
                            canvas.DrawLine(p1.X, p1.Y, p2.X, p2.Y, _strokePaint);
                            connections++;
                        }
                    }
                }
            }
        }

        lock (_spriteBitmaps)
        {
            var hasSprites = _spriteBitmaps.Count > 0;

            foreach (var p in _particles)
            {
                _ = canvas.Save();
                canvas.Translate(p.X, p.Y);
                canvas.RotateDegrees(p.Rotation);

                var alphaByte = (byte)Math.Clamp(p.CurrentAlpha * 255f, 0f, 255f);

                if (hasSprites)
                {
                    var bmp = _spriteBitmaps[p.SpriteIndex % _spriteBitmaps.Count];
                    var halfSize = p.Size * 0.5f;
                    var destRect = new SKRect(-halfSize, -halfSize, halfSize, halfSize);

                    if (_hasCustomTint)
                    {
                        var tint = _customTintColor.WithAlpha(alphaByte);
                        using var cf = SKColorFilter.CreateBlendMode(tint, SKBlendMode.Modulate);
                        _bitmapPaint.ColorFilter = cf;
                        _bitmapPaint.Color = SKColors.White;
                        canvas.DrawBitmap(bmp, destRect, s_samplingOptions, _bitmapPaint);
                        _bitmapPaint.ColorFilter = null;
                    }
                    else
                    {
                        _bitmapPaint.ColorFilter = null;
                        _bitmapPaint.Color = new SKColor(255, 255, 255, alphaByte);
                        canvas.DrawBitmap(bmp, destRect, s_samplingOptions, _bitmapPaint);
                    }
                }
                else
                {
                    _fallbackPaint.Color = _particleColor.WithAlpha(alphaByte);
                    var r = p.Size * 0.5f;

                    if (_particleTypeName == "rain")
                    {
                        _strokePaint.Color = _particleColor.WithAlpha(alphaByte);
                        _strokePaint.StrokeWidth = Math.Max(1.2f, r * 0.18f);
                        canvas.DrawLine(0, -r, 0, r, _strokePaint);
                    }
                    else if (_particleTypeName == "matrix")
                    {
                        var stepH = Math.Max(3f, r * 0.45f);
                        for (var k = 0; k < 3; k++)
                        {
                            var headAlpha = (byte)Math.Clamp(alphaByte * (1f - (k * 0.32f)), 10f, 255f);
                            _fallbackPaint.Color = (k == 0 ? SKColors.White : _particleColor).WithAlpha(headAlpha);
                            var rectY = -k * (stepH + 2f);
                            canvas.DrawRoundRect(-stepH * 0.4f, rectY, stepH * 0.8f, stepH, 1.5f, 1.5f, _fallbackPaint);
                        }
                    }
                    else if (_particleTypeName.Contains("star", StringComparison.OrdinalIgnoreCase) ||
                            _particleTypeName.Equals("twinkle", StringComparison.OrdinalIgnoreCase))
                    {
                        var glowR = r * 1.9f;
                        _glowPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.26f));
                        canvas.DrawCircle(0, 0, glowR, _glowPaint);

                        _ = canvas.Save();
                        var pulse = 0.85f + (0.3f * (p.CurrentAlpha / Math.Max(0.1f, p.BaseAlpha)));
                        canvas.Scale(r * 0.15f * pulse);
                        canvas.DrawPath(_starPath, _fallbackPaint);
                        canvas.Restore();
                    }
                    else if (_particleTypeName.Contains("snow", StringComparison.OrdinalIgnoreCase) ||
                            _particleTypeName.Contains("bubble", StringComparison.OrdinalIgnoreCase) ||
                            _particleTypeName.Contains("firefl", StringComparison.OrdinalIgnoreCase))
                    {
                        canvas.DrawCircle(0, 0, r * 0.65f, _fallbackPaint);
                        if (_particleTypeName.Contains("firefl", StringComparison.OrdinalIgnoreCase))
                        {
                            _fallbackPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.3f));
                            canvas.DrawCircle(0, 0, r * 1.35f, _fallbackPaint);
                        }
                    }
                    else if (_particleTypeName.Contains("spark", StringComparison.OrdinalIgnoreCase) ||
                            _particleTypeName.Contains("ember", StringComparison.OrdinalIgnoreCase))
                    {
                        var glowR = r * 1.85f;
                        _glowPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.28f));
                        canvas.DrawCircle(0, 0, glowR, _glowPaint);

                        var trailLen = Math.Max(2.5f, r * 1.8f) * _currentTrafficMultiplier;
                        _strokePaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.40f));
                        _strokePaint.StrokeWidth = Math.Max(1.0f, r * 0.35f);
                        canvas.DrawLine(0, 0, -p.SpeedX * trailLen, -p.SpeedY * trailLen, _strokePaint);

                        _ = canvas.Save();
                        canvas.Scale(r * 0.11f);
                        canvas.DrawPath(_diamondPath, _fallbackPaint);
                        canvas.Restore();
                    }
                    else
                    {
                        _ = canvas.Save();
                        canvas.Scale(r * 0.1f);
                        canvas.DrawPath(_petalPath, _fallbackPaint);
                        canvas.Restore();
                    }
                }

                canvas.Restore();
            }
        }

        foreach (var bp in _burstParticles)
        {
            var alphaByte = (byte)Math.Clamp(bp.Alpha * 255f, 0f, 255f);
            if (alphaByte == 0)
            {
                continue;
            }

            _glowPaint.Color = bp.Color.WithAlpha((byte)(alphaByte * 0.35f));
            canvas.DrawCircle(bp.X, bp.Y, bp.Size * 1.8f, _glowPaint);

            _fallbackPaint.Color = SKColors.White.WithAlpha(alphaByte);
            canvas.DrawCircle(bp.X, bp.Y, bp.Size * 0.6f, _fallbackPaint);
        }
    }

    //отрисовка кольцевой ударной волны
    private void DrawShockwave(SKCanvas canvas, float w, float h)
    {
        var maxRadius = MathF.Sqrt((w * w) + (h * h)) * 0.65f;
        var eased = 1f - MathF.Pow(1f - _shockwaveProgress, 3f);
        var radius = 24f + (maxRadius * eased);
        var alpha = (byte)Math.Clamp((1f - _shockwaveProgress) * 220f, 0f, 255f);

        _strokePaint.Color = _accentSkColor.WithAlpha(alpha);
        _strokePaint.StrokeWidth = Math.Max(2.5f, 16f * (1f - _shockwaveProgress));
        canvas.DrawCircle(w * 0.5f, h * 0.5f, radius, _strokePaint);

        if (radius > 32f)
        {
            var innerRadius = radius * 0.84f;
            var innerAlpha = (byte)Math.Clamp(alpha * 0.55f, 0f, 255f);
            _strokePaint.Color = _primarySkColor.WithAlpha(innerAlpha);
            _strokePaint.StrokeWidth = Math.Max(1.5f, 8f * (1f - _shockwaveProgress));
            canvas.DrawCircle(w * 0.5f, h * 0.5f, innerRadius, _strokePaint);
        }
    }
}

