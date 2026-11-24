namespace obxodka.Pages.Vpn;

//сплайновый график скорости и счетчики трафика на skia canvas
public sealed partial class VpnView
{
    //обработка одного кадра интерполяции и перерисовки графика скорости
    private void StepGraphFrame()
    {
        if (_disposed || !_isTabVisible || !_isWindowVisible || !IsVisible || TrafficGraphCanvas is null || !TrafficGraphCanvas.IsVisible)
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
            foreach (var s in _graphSamples)
            {
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
    }

#if WINDOWS
    //аппаратный такт перерисовки графика скорости по vsync экрана
    private void OnGraphVsyncRendering(object? sender, object e)
    {
        var nowTicks = Environment.TickCount64;
        if (nowTicks - _graphLastVsyncTicks < 15)
        {
            return;
        }

        _graphLastVsyncTicks = nowTicks;
        StepGraphFrame();
    }
#endif

    //пауза цикла отрисовки графика без потери накопленных точек
    private void PauseGraphLoop()
    {
#if WINDOWS
        if (_graphVsyncActive)
        {
            _graphVsyncActive = false;
            Platforms.Windows.WinUiVsync.Unsubscribe(OnGraphVsyncRendering);
        }
#else
        _graphAnimTimer?.Stop();
        _graphAnimTimer = null;
#endif
    }

    //возобновление цикла отрисовки графика при возврате на вкладку
    private void ResumeGraphLoop()
    {
        if (_disposed || !_isTabVisible || !_isWindowVisible)
        {
            return;
        }

        if (!_graphStopwatch.IsRunning)
        {
            _graphStopwatch.Start();
        }

        _lastGraphTickTime = (float)_graphStopwatch.Elapsed.TotalSeconds;
#if WINDOWS
        if (!_graphVsyncActive)
        {
            _graphLastVsyncTicks = 0;
            _graphVsyncActive = true;
            Platforms.Windows.WinUiVsync.Subscribe(OnGraphVsyncRendering);
        }
#else
        if (_graphAnimTimer is null)
        {
            _graphAnimTimer = Dispatcher.CreateTimer();
            _graphAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
            _graphAnimTimer.Tick += (_, _) => StepGraphFrame();
            _graphAnimTimer.Start();
        }
#endif
    }

    //запуск цикла интерполяции и отрисовки графика трафика
    private void StartGraphAnimation()
    {
        _graphStopwatch.Restart();
        lock (_graphSamples)
        {
            _graphSamples.Clear();
            _graphSamples.Add(new GraphSample(0f, 0f, 0f));
        }

        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
        _lastBytesSent = OctopusEngine.Current.TotalBytesSent;
        _lastBytesReceived = OctopusEngine.Current.TotalBytesReceived;
        _lastTrafficUpdate = DateTime.Now;
        _targetSpeedUp = _smoothSpeedUp = 0;
        _targetSpeedDown = _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;

        ResumeGraphLoop();
    }

    //остановка анимации графика скорости
    private void StopGraphAnimation()
    {
        PauseGraphLoop();
        _graphStopwatch.Reset();
        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
        _targetSpeedUp = 0;
        _targetSpeedDown = 0;
        _smoothSpeedUp = 0;
        _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;
        _lastBytesSent = 0;
        _lastBytesReceived = 0;

        lock (_graphSamples)
        {
            _graphSamples.Clear();
        }

        if (MainThread.IsMainThread)
        {
            ResetTrafficLabels();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(ResetTrafficLabels);
        }

        TrafficGraphCanvas?.InvalidateSurface();
        return;

        void ResetTrafficLabels()
        {
            TrafficUpLabel?.Text = "0 B/s";
            TrafficDownLabel?.Text = "0 B/s";
        }
    }

    //обработка поступления новых счетчиков трафика
    private void OnTrafficUpdated(long bytesSent, long bytesReceived)
    {
        if (_vpnService.CurrentState != AppVpnState.Connected)
        {
            return;
        }

        var now = DateTime.Now;
        var elapsed = (now - _lastTrafficUpdate).TotalSeconds;
        if (elapsed <= 0.05)
        {
            return;
        }

        if (bytesSent < _lastBytesSent || bytesReceived < _lastBytesReceived)
        {
            _lastBytesSent = bytesSent;
            _lastBytesReceived = bytesReceived;
            _lastTrafficUpdate = now;
            return;
        }

        var sentSpeed = Math.Max(0, (bytesSent - _lastBytesSent) / elapsed);
        var recvSpeed = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsed);

        _lastBytesSent = bytesSent;
        _lastBytesReceived = bytesReceived;
        _lastTrafficUpdate = now;

        _targetSpeedUp = sentSpeed;
        _targetSpeedDown = recvSpeed;

        //даже когда вкладка или окно скрыты, фиксируем точки в истории чтобы при возврате график сразу показывал реальный трафик
        if (_graphStopwatch.IsRunning && (!_isTabVisible || !_isWindowVisible))
        {
            var sampleTime = (float)_graphStopwatch.Elapsed.TotalSeconds;
            lock (_graphSamples)
            {
                _graphSamples.Add(new GraphSample(sampleTime, (float)sentSpeed, (float)recvSpeed));
                _lastSampleTime = sampleTime;
                var cutoff = sampleTime - (TimeWindowSeconds + 1.0f);
                var removeCount = 0;
                while (removeCount < _graphSamples.Count && _graphSamples[removeCount].Time < cutoff)
                {
                    removeCount++;
                }

                if (removeCount > 0)
                {
                    _graphSamples.RemoveRange(0, removeCount);
                }

                var samplePeak = Math.Max(sentSpeed, recvSpeed);
                if (samplePeak > _smoothMaxSpeed)
                {
                    _smoothMaxSpeed = samplePeak;
                }
            }
        }

        PlatformThrottler.Post("ui:traffic", TimeSpan.FromMilliseconds(120), () =>
        {
            if (_vpnService.CurrentState != AppVpnState.Connected)
            {
                return;
            }

            TrafficUpLabel.Text = $"{FormatBytes(sentSpeed)}/s";
            TrafficDownLabel.Text = $"{FormatBytes(recvSpeed)}/s";
        });
    }

    //отрисовка сплайнового графика скорости на холсте skia
    private void OnPaintGraphSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var w = e.Info.Width;
        var h = e.Info.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var paddingY = 6f;
        var usableH = h - paddingY * 2f;

        s_skGridPaint.Color = GetSkiaThemeColor("BorderSubtle", new SKColor(128, 128, 128)).WithAlpha(130);
        for (var line = 1; line <= 3; line++)
        {
            var y = paddingY + usableH * (line / 4f);
            canvas.DrawLine(0, y, w, y, s_skGridPaint);
        }

        float now;
        double maxSpeed;

        lock (_graphSamples)
        {
            if (_graphSamples.Count == 0)
            {
                return;
            }

            _graphSamplesSnapshot.Clear();
            _graphSamplesSnapshot.AddRange(_graphSamples);
            now = (float)_graphStopwatch.Elapsed.TotalSeconds;
            maxSpeed = Math.Max(_smoothMaxSpeed, 1024.0);
        }

        if (_graphSamplesSnapshot.Count == 0)
        {
            return;
        }

        var graphStyle = ThemeManager.Instance?.ActiveTheme?.Vfx?.TrafficGraphStyle.Trim().ToLowerInvariant() ?? "neon_wave";

        var skPrimaryBright = GetSkiaThemeColor("PrimaryBright", s_skGraphUp);
        var skAccent = GetSkiaThemeColor("Accent", s_skGraphDown);
        var skGraphUpFill = skPrimaryBright.WithAlpha(55);
        var skGraphDownFill = skAccent.WithAlpha(75);

        _graphStrokeUpPaint.Color = skPrimaryBright;
        _graphStrokeDownPaint.Color = skAccent;

        DrawStream(true, _graphStrokeUpPaint, skGraphUpFill, skPrimaryBright);
        DrawStream(false, _graphStrokeDownPaint, skGraphDownFill, skAccent);
        return;

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
                    var barX = b * slotW + (slotW - barW) * 0.5f + (isUpStream ? -1.5f : 1.5f);
                    var barTop = Math.Min(h - paddingY - 2f, ptY);
                    var rect = new SKRect(barX, barTop, barX + barW, h - paddingY);
                    _graphDotSolidPaint.Color = strokeColor.WithAlpha((byte)(isUpStream ? 110 : 165));
                    canvas.DrawRoundRect(rect, 2f, 2f, _graphDotSolidPaint);

                    _graphDotGlowPaint.Color = strokeColor.WithAlpha(235);
                    canvas.DrawRect(barX, barTop, barW, 2.5f, _graphDotGlowPaint);
                }

                return;
            }

            _graphStrokeBuilder.Reset();
            _graphFillBuilder.Reset();
            _graphStrokeBuilder.MoveTo(_graphPointsBuffer[0]);
            _graphFillBuilder.MoveTo(_graphPointsBuffer[0]);
            if (graphStyle == "pulse_line")
            {
                for (var i = 1; i < _graphPointsBuffer.Count; i++)
                {
                    _graphStrokeBuilder.LineTo(_graphPointsBuffer[i]);
                    _graphFillBuilder.LineTo(_graphPointsBuffer[i]);
                }
            }
            else
            {
                for (var i = 0; i < _graphPointsBuffer.Count - 1; i++)
                {
                    var p0 = _graphPointsBuffer[i];
                    var p1 = _graphPointsBuffer[i + 1];
                    var midX = (p0.X + p1.X) / 2f;
                    _graphStrokeBuilder.CubicTo(midX, p0.Y, midX, p1.Y, p1.X, p1.Y);
                    _graphFillBuilder.CubicTo(midX, p0.Y, midX, p1.Y, p1.X, p1.Y);
                }
            }

            using var strokePath = _graphStrokeBuilder.Detach();

            _graphFillBuilder.LineTo(_graphPointsBuffer[^1].X, h);
            _graphFillBuilder.LineTo(_graphPointsBuffer[0].X, h);
            _graphFillBuilder.Close();
            using var fillPath = _graphFillBuilder.Detach();

            SKShader fillShader;
            var effectiveFillColor = graphStyle == "aurora_ribbon"
                ? strokeColor.WithAlpha(115)
                : startFillColor;

            if (isUpStream)
            {
                if (_cachedGraphUpFillShader == null || Math.Abs(_lastGraphH - h) > 0.5f || _lastGraphUpColor != effectiveFillColor)
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
                if (_cachedGraphDownFillShader == null || Math.Abs(_lastGraphH - h) > 0.5f || _lastGraphDownColor != effectiveFillColor)
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
            var pulseRadius = 4.5f + 1.8f * MathF.Sin(_pulsePhase);
            var alphaGlow = (byte)Math.Clamp(50 + 35 * MathF.Sin(_pulsePhase), 0, 255);

            _graphDotGlowPaint.Color = strokeColor.WithAlpha(alphaGlow);
            canvas.DrawCircle(lastPt.X, lastPt.Y, pulseRadius, _graphDotGlowPaint);

            _graphDotSolidPaint.Color = strokeColor;
            canvas.DrawCircle(lastPt.X, lastPt.Y, 2.5f, _graphDotSolidPaint);
        }

        //построение сглаженных точек графика трафика
        void PopulatePoints(bool isUpStream)
        {
            _graphPointsBuffer.Clear();
            foreach (var s in _graphSamplesSnapshot)
            {
                var age = now - s.Time;
                var x = w - age / TimeWindowSeconds * w;
                var speed = isUpStream ? s.SpeedUp : s.SpeedDown;
                var ratio = Math.Clamp((float)(speed / maxSpeed), 0f, 1f);
                var y = h - paddingY - ratio * usableH;
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
    }

    //форматирование байтов в кб и мб
    public static string FormatBytes(double bytes) => FormatHelper.FormatBytes(bytes);
}
