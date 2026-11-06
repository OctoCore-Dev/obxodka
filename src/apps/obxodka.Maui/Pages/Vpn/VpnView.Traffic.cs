//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

//сплайновый график скорости и счетчики трафика на skia canvas
public sealed partial class VpnView
{
    //запуск таймера интерполяции и отрисовки графика трафика
    private void StartGraphAnimation()
    {
        if (_graphAnimTimer is not null)
        {
            return;
        }

        _graphStopwatch.Restart();
        lock (_graphSamples)
        {
            _graphSamples.Clear();
            _graphSamples.Add(new GraphSample(0f, 0f, 0f));
        }

        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
        _smoothSpeedUp = 0;
        _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;

        _graphAnimTimer = Dispatcher.CreateTimer();
        _graphAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
        _graphAnimTimer.Tick += (_, _) =>
        {
            if (!IsVisible || TrafficGraphCanvas is null || !TrafficGraphCanvas.IsVisible)
            {
                return;
            }

            var now = (float)_graphStopwatch.Elapsed.TotalSeconds;
            var dt = Math.Clamp(now - _lastGraphTickTime, 0.001f, 0.1f);
            _lastGraphTickTime = now;

            _pulsePhase += dt * 5.5f;
            if (_pulsePhase > MathF.PI * 2)
            {
                _pulsePhase -= MathF.PI * 2;
            }

            var speedLerp = 1.0 - Math.Exp(-8.0 * dt);
            _smoothSpeedUp += (_targetSpeedUp - _smoothSpeedUp) * speedLerp;
            _smoothSpeedDown += (_targetSpeedDown - _smoothSpeedDown) * speedLerp;

            lock (_graphSamples)
            {
                if (now - _lastSampleTime >= 0.05f || _graphSamples.Count == 0)
                {
                    _graphSamples.Add(new GraphSample(now, (float)_smoothSpeedUp, (float)_smoothSpeedDown));
                    _lastSampleTime = now;

                    var cutoff = now - (TimeWindowSeconds + 1.0f);
                    var removeCount = 0;
                    while (removeCount < _graphSamples.Count && _graphSamples[removeCount].Time < cutoff)
                    {
                        removeCount++;
                    }

                    if (removeCount > 0)
                    {
                        _graphSamples.RemoveRange(0, removeCount);
                    }
                }

                var peak = 1024.0;
                for (var i = 0; i < _graphSamples.Count; i++)
                {
                    var s = _graphSamples[i];
                    if (s.SpeedUp > peak)
                    {
                        peak = s.SpeedUp;
                    }

                    if (s.SpeedDown > peak)
                    {
                        peak = s.SpeedDown;
                    }
                }

                var peakLerp = 1.0 - Math.Exp(-4.0 * dt);
                _smoothMaxSpeed += (peak - _smoothMaxSpeed) * peakLerp;
            }

            TrafficGraphCanvas.InvalidateSurface();
        };
        _graphAnimTimer.Start();
    }

    //остановка анимации графика скорости
    private void StopGraphAnimation()
    {
        _graphAnimTimer?.Stop();
        _graphAnimTimer = null;
        _graphStopwatch.Reset();
        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
        _targetSpeedUp = 0;
        _targetSpeedDown = 0;
        _smoothSpeedUp = 0;
        _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;

        lock (_graphSamples)
        {
            _graphSamples.Clear();
        }

        TrafficGraphCanvas?.InvalidateSurface();
    }

    //обработка поступления новых счетчиков трафика
    private void OnTrafficUpdated(long bytesSent, long bytesReceived)
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastTrafficUpdate).TotalSeconds;
        if (elapsed <= 0.05)
        {
            return;
        }

        var sentSpeed = Math.Max(0, (bytesSent - _lastBytesSent) / elapsed);
        var recvSpeed = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsed);

        _lastBytesSent = bytesSent;
        _lastBytesReceived = bytesReceived;
        _lastTrafficUpdate = now;

        _targetSpeedUp = sentSpeed;
        _targetSpeedDown = recvSpeed;

        PlatformThrottler.Post("ui:traffic", TimeSpan.FromMilliseconds(200), () =>
        {
            TrafficUpLabel.Text = $"{FormatBytes(sentSpeed)}/s";
            TrafficDownLabel.Text = $"{FormatBytes(recvSpeed)}/s";
        });
    }

    //отрисовка сплайнового графика скорости на холсте skia
    private void OnPaintGraphSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var w = e.Info.Width;
        var h = e.Info.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var paddingY = 6f;
        var usableH = h - (paddingY * 2f);

        for (var line = 1; line <= 3; line++)
        {
            var y = paddingY + (usableH * (line / 4f));
            canvas.DrawLine(0, y, w, y, s_skGridPaint);
        }

        List<GraphSample> samplesCopy;
        float now;
        double maxSpeed;

        lock (_graphSamples)
        {
            if (_graphSamples.Count == 0)
            {
                return;
            }

            samplesCopy = [with(_graphSamples)];
            now = (float)_graphStopwatch.Elapsed.TotalSeconds;
            maxSpeed = Math.Max(_smoothMaxSpeed, 1024.0);
        }

        if (samplesCopy.Count == 0)
        {
            return;
        }

        var graphStyle = ThemeManager.Instance?.ActiveTheme?.Vfx?.TrafficGraphStyle?.Trim().ToLowerInvariant() ?? "neon_wave";

        //построение сглаженных точек графика трафика
        void PopulatePoints(bool isUpStream)
        {
            _graphPointsBuffer.Clear();
            for (var i = 0; i < samplesCopy.Count; i++)
            {
                var s = samplesCopy[i];
                var age = now - s.Time;
                var x = w - (age / TimeWindowSeconds * w);
                var speed = isUpStream ? s.SpeedUp : s.SpeedDown;
                var ratio = Math.Clamp((float)(speed / maxSpeed), 0f, 1f);
                var y = h - paddingY - (ratio * usableH);
                _graphPointsBuffer.Add(new SKPoint(x, y));
            }

            if (_graphPointsBuffer.Count == 0)
            {
                return;
            }

            if (_graphPointsBuffer[0].X > 0)
            {
                _graphPointsBuffer.Insert(0, new SKPoint(0, _graphPointsBuffer[0].Y));
            }

            if (_graphPointsBuffer[^1].X < w)
            {
                _graphPointsBuffer.Add(new SKPoint(w, _graphPointsBuffer[^1].Y));
            }
        }

        void DrawStream(bool isUpStream, SKPaint strokePaint, SKColor startFillColor, SKColor strokeColor)
        {
            PopulatePoints(isUpStream);
            if (_graphPointsBuffer.Count < 2)
            {
                return;
            }

            if (graphStyle == "cyber_bars")
            {
                var barCount = 28;
                var slotW = w / (float)barCount;
                var barW = Math.Max(2f, slotW * 0.56f);
                for (var b = 0; b < barCount; b++)
                {
                    var sampleIdx = (int)Math.Clamp((float)b / barCount * _graphPointsBuffer.Count, 0, _graphPointsBuffer.Count - 1);
                    var ptY = _graphPointsBuffer[sampleIdx].Y;
                    var barX = (b * slotW) + ((slotW - barW) * 0.5f) + (isUpStream ? -1.5f : 1.5f);
                    var barTop = Math.Min(h - paddingY - 2f, ptY);
                    var rect = new SKRect(barX, barTop, barX + barW, h - paddingY);
                    _graphDotSolidPaint.Color = strokeColor.WithAlpha((byte)(isUpStream ? 110 : 165));
                    canvas.DrawRoundRect(rect, 2f, 2f, _graphDotSolidPaint);

                    _graphDotGlowPaint.Color = strokeColor.WithAlpha(235);
                    canvas.DrawRect(barX, barTop, barW, 2.5f, _graphDotGlowPaint);
                }
                return;
            }

            using var strokeBuilder = new SKPathBuilder();
            strokeBuilder.MoveTo(_graphPointsBuffer[0]);
            if (graphStyle == "pulse_line")
            {
                for (var i = 1; i < _graphPointsBuffer.Count; i++)
                {
                    strokeBuilder.LineTo(_graphPointsBuffer[i]);
                }
            }
            else
            {
                for (var i = 0; i < _graphPointsBuffer.Count - 1; i++)
                {
                    var p0 = _graphPointsBuffer[i];
                    var p1 = _graphPointsBuffer[i + 1];
                    var midX = (p0.X + p1.X) / 2f;
                    strokeBuilder.CubicTo(midX, p0.Y, midX, p1.Y, p1.X, p1.Y);
                }
            }
            using var strokePath = strokeBuilder.Detach();

            using var fillBuilder = new SKPathBuilder(strokePath);
            fillBuilder.LineTo(_graphPointsBuffer[^1].X, h);
            fillBuilder.LineTo(_graphPointsBuffer[0].X, h);
            fillBuilder.Close();
            using var fillPath = fillBuilder.Detach();

            SKShader fillShader;
            var effectiveFillColor = graphStyle == "aurora_ribbon"
                ? strokeColor.WithAlpha(115)
                : startFillColor;

            if (isUpStream)
            {
                if (_cachedGraphUpFillShader == null || _lastGraphH != h || _lastGraphUpColor != effectiveFillColor)
                {
                    _cachedGraphUpFillShader?.Dispose();
                    _cachedGraphUpFillShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(0, h),
                        [effectiveFillColor, SKColors.Transparent],
                        [0f, 1f],
                        SKShaderTileMode.Clamp);
                    _lastGraphUpColor = effectiveFillColor;
                }
                fillShader = _cachedGraphUpFillShader;
            }
            else
            {
                if (_cachedGraphDownFillShader == null || _lastGraphH != h || _lastGraphDownColor != effectiveFillColor)
                {
                    _cachedGraphDownFillShader?.Dispose();
                    _cachedGraphDownFillShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(0, h),
                        [effectiveFillColor, SKColors.Transparent],
                        [0f, 1f],
                        SKShaderTileMode.Clamp);
                    _lastGraphDownColor = effectiveFillColor;
                }
                fillShader = _cachedGraphDownFillShader;
            }
            _lastGraphH = h;

            if (graphStyle != "pulse_line")
            {
                _graphFillPaint.Shader = fillShader;
                canvas.DrawPath(fillPath, _graphFillPaint);
                _graphFillPaint.Shader = null;
            }

            if (graphStyle == "aurora_ribbon")
            {
                var origWidth = strokePaint.StrokeWidth;
                strokePaint.StrokeWidth = 5.5f;
                strokePaint.Color = strokeColor.WithAlpha(90);
                canvas.DrawPath(strokePath, strokePaint);
                strokePaint.StrokeWidth = origWidth;
                strokePaint.Color = strokeColor;
            }

            canvas.DrawPath(strokePath, strokePaint);

            var lastPt = _graphPointsBuffer[^1];
            var pulseRadius = 4.5f + (1.8f * MathF.Sin(_pulsePhase));
            var alphaGlow = (byte)Math.Clamp(50 + (35 * MathF.Sin(_pulsePhase)), 0, 255);

            _graphDotGlowPaint.Color = strokeColor.WithAlpha(alphaGlow);
            canvas.DrawCircle(lastPt.X, lastPt.Y, pulseRadius, _graphDotGlowPaint);

            _graphDotSolidPaint.Color = strokeColor;
            canvas.DrawCircle(lastPt.X, lastPt.Y, 2.5f, _graphDotSolidPaint);
        }

        var skPrimaryBright = GetSkiaThemeColor("PrimaryBright", s_skGraphUp);
        var skAccent = GetSkiaThemeColor("Accent", s_skGraphDown);
        var skGraphUpFill = skPrimaryBright.WithAlpha(55);
        var skGraphDownFill = skAccent.WithAlpha(75);

        _graphStrokeUpPaint.Color = skPrimaryBright;
        _graphStrokeDownPaint.Color = skAccent;

        DrawStream(true, _graphStrokeUpPaint, skGraphUpFill, skPrimaryBright);
        DrawStream(false, _graphStrokeDownPaint, skGraphDownFill, skAccent);
    }

    //форматирование байтов в кб и мб
    public static string FormatBytes(double bytes) => FormatHelper.FormatBytes(bytes);


}
