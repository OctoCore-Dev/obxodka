using Path = System.IO.Path;

namespace obxodka.Pages.Vpn;

//кастомная анимированная кнопка тем оформления и спрайтовый плеер
public sealed partial class VpnView
{
    //отрисовка кадра анимированной кнопки темы
    private void OnCustomButtonAnimatedCanvasPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var current = _buttonAnimatedCurrent;
        var bmp = current?.CurrentBitmap;
        if (bmp is null)
        {
            return;
        }

        var info = e.Info;
        var dst = new SKRect(0, 0, info.Width, info.Height);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        var prev = _buttonAnimatedPrev;
        if (prev?.CurrentBitmap is { } prevBmp && _buttonTransitionProgress < 1f)
        {
            var prevAlpha = (byte)Math.Clamp((1f - _buttonTransitionProgress) * 255f, 0f, 255f);
            _customButtonBitmapPaint.Color = SKColors.White.WithAlpha(prevAlpha);
            canvas.DrawBitmap(prevBmp, new SKRect(0, 0, prevBmp.Width, prevBmp.Height), dst, sampling, _customButtonBitmapPaint);

            var currAlpha = (byte)Math.Clamp(_buttonTransitionProgress * 255f, 0f, 255f);
            _customButtonBitmapPaint.Color = SKColors.White.WithAlpha(currAlpha);
            canvas.DrawBitmap(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), dst, sampling, _customButtonBitmapPaint);
            return;
        }

        _customButtonBitmapPaint.Color = SKColors.White;
        canvas.DrawBitmap(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), dst, sampling, _customButtonBitmapPaint);
    }

    //приостановка анимации кнопки при сворачивании окна
    public void OnWindowFocusChanged(bool isFocused)
    {
        _isWindowFocused = isFocused;
        if (!isFocused)
        {
            StopButtonAnimation();
        }
        else
        {
            if (CustomButtonAnimatedCanvas.IsVisible && _buttonAnimatedCurrent is not null)
            {
                StartButtonAnimation();
            }
        }
    }

    //запуск таймера анимации кастомной кнопки темы
    private void StartButtonAnimation()
    {
        if (!_isWindowFocused)
        {
            return;
        }

        if (_buttonAnimationTimer is not null)
        {
            return;
        }

        _buttonAnimStopwatch.Restart();
        _lastButtonAnimTicks = 0;
        _buttonAnimationTimer = Dispatcher.CreateTimer();
        _buttonAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
#pragma warning disable IDE0058
        _buttonAnimationTimer.Tick += (_, _) =>
        {
            var nowTicks = _buttonAnimStopwatch.ElapsedTicks;
            var dtMs = (int)Math.Clamp((nowTicks - _lastButtonAnimTicks) * 1000.0 / Stopwatch.Frequency, 4.0, 100.0);
            _lastButtonAnimTicks = nowTicks;

            _buttonAnimatedCurrent?.Advance(dtMs);
            if (_buttonAnimatedPrev is not null)
            {
                _buttonAnimatedPrev.Advance(dtMs);
                _buttonTransitionProgress = Math.Min(1f, _buttonTransitionProgress + dtMs / 300f);
                if (_buttonTransitionProgress >= 1f)
                {
                    _buttonAnimatedPrev = null;
                }
            }

            CustomButtonAnimatedCanvas.InvalidateSurface();
        };
#pragma warning restore IDE0058
        _buttonAnimationTimer.Start();
    }

    //остановка таймера анимации кнопки темы
    private void StopButtonAnimation()
    {
        _buttonAnimationTimer?.Stop();
        _buttonAnimationTimer = null;
        _buttonAnimStopwatch.Stop();
    }

    //очистка ресурсов всех состояний кнопки темы
    private void DisposeAnimatedButtonResources()
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;
        StopButtonAnimation();
        _buttonAnimatedCurrent?.Dispose();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedPrev?.Dispose();
        _buttonAnimatedPrev = null;
        _buttonTransitionProgress = 1f;
        _buttonAnimatedIdle?.Dispose();
        _buttonAnimatedIdle = null;
        _buttonAnimatedConnecting?.Dispose();
        _buttonAnimatedConnecting = null;
        _buttonAnimatedActive?.Dispose();
        _buttonAnimatedActive = null;
        _buttonAnimatedError?.Dispose();
        _buttonAnimatedError = null;
    }

    //получение пути к статичной картинке кнопки под состояние
    private string? GetButtonImagePathForState(AppVpnState state)
    {
        return state switch
        {
            AppVpnState.Connected => _buttonImageActivePath ?? _buttonImageIdlePath,
            AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => _buttonImageConnectingPath ??
                _buttonImageIdlePath,
            AppVpnState.Error => _buttonImageErrorPath ?? _buttonImageIdlePath,
            AppVpnState.Disconnected => _buttonImageIdlePath,
            _ => _buttonImageIdlePath
        };
    }

    //получение анимированного клипа кнопки под состояние
    private ButtonAnimatedClip? GetButtonAnimatedClipForState(AppVpnState state)
    {
        return state switch
        {
            AppVpnState.Connected => _buttonAnimatedActive ?? _buttonAnimatedIdle,
            AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => _buttonAnimatedConnecting ??
                _buttonAnimatedIdle,
            AppVpnState.Error => _buttonAnimatedError ?? _buttonAnimatedIdle,
            AppVpnState.Disconnected => _buttonAnimatedIdle,
            _ => null
        };
    }

    //применение текстур и анимаций кнопки из активной темы
    public void ApplyThemeButton(
        string? imageIdlePath,
        string? imageConnectingPath = null,
        string? imageActivePath = null,
        string? imageErrorPath = null,
        string? videoIdlePath = null,
        string? videoConnectingPath = null,
        string? videoActivePath = null,
        string? videoErrorPath = null,
        string? cardFramePath = null,
        string? buttonRingPath = null)
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;

        _buttonImageIdlePath = imageIdlePath;
        _buttonImageConnectingPath = imageConnectingPath;
        _buttonImageActivePath = imageActivePath;
        _buttonImageErrorPath = imageErrorPath;

        var hasStaticCustom = !string.IsNullOrEmpty(imageIdlePath) ||
                              !string.IsNullOrEmpty(imageConnectingPath) ||
                              !string.IsNullOrEmpty(imageActivePath) ||
                              !string.IsNullOrEmpty(imageErrorPath);

        var idleCandidate = videoIdlePath ?? (IsPotentialAnimatedFile(imageIdlePath) ? imageIdlePath : null);
        var connCandidate = videoConnectingPath ?? (IsPotentialAnimatedFile(imageConnectingPath) ? imageConnectingPath : null);
        var activeCandidate = videoActivePath ?? (IsPotentialAnimatedFile(imageActivePath) ? imageActivePath : null);
        var errorCandidate = videoErrorPath ?? (IsPotentialAnimatedFile(imageErrorPath) ? imageErrorPath : null);

        var hasAnyAnim = !string.IsNullOrEmpty(idleCandidate) ||
                         !string.IsNullOrEmpty(connCandidate) ||
                         !string.IsNullOrEmpty(activeCandidate) ||
                         !string.IsNullOrEmpty(errorCandidate);

        _activeCardFramePath = cardFramePath;
        var framesEnabled = ThemeManager.Instance?.FramesEnabled ?? true;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!string.IsNullOrEmpty(cardFramePath) && File.Exists(cardFramePath))
            {
                CardFrameImage.Source = ImageSource.FromFile(cardFramePath);
                CardFrameImage.IsVisible = framesEnabled;
            }
            else
            {
                CardFrameImage.IsVisible = false;
                CardFrameImage.Source = null;
            }

            if (!string.IsNullOrEmpty(buttonRingPath) && File.Exists(buttonRingPath))
            {
                CustomButtonRingImage.Source = ImageSource.FromFile(buttonRingPath);
                CustomButtonRingImage.IsVisible = true;
            }
            else
            {
                CustomButtonRingImage.IsVisible = false;
                CustomButtonRingImage.Source = null;
            }

            if (hasStaticCustom && !hasAnyAnim)
            {
                DefaultButtonContent.IsVisible = false;
                CustomButtonAnimatedCanvas.IsVisible = false;
                ConnectButtonCore.BackgroundColor = Colors.Transparent;
                ConnectButtonCore.Stroke = Colors.Transparent;

                var currentState = _vpnService.CurrentState;
                var initialPath = GetButtonImagePathForState(currentState);
                if (string.IsNullOrEmpty(initialPath) || !File.Exists(initialPath))
                {
                    return;
                }

                CustomButtonImage.Source = ImageSource.FromFile(initialPath);
                CustomButtonImage.IsVisible = true;
                CustomButtonImage.Opacity = 1;
            }
            else if (!hasAnyAnim)
            {
                ResetCustomButtonStateOnly();
            }
        });

        if (!hasAnyAnim)
        {
            DisposeAnimatedButtonResources();
            MainThread.BeginInvokeOnMainThread(() => CustomButtonAnimatedCanvas.IsVisible = false);
            return;
        }

        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        var cts = new CancellationTokenSource();
        _buttonAnimCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var idleClip = !string.IsNullOrEmpty(idleCandidate) && File.Exists(idleCandidate)
                    ? ButtonAnimatedClip.Load(idleCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    return;
                }

                var connClip = !string.IsNullOrEmpty(connCandidate) && File.Exists(connCandidate)
                    ? ButtonAnimatedClip.Load(connCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    return;
                }

                var activeClip = !string.IsNullOrEmpty(activeCandidate) && File.Exists(activeCandidate)
                    ? ButtonAnimatedClip.Load(activeCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    return;
                }

                var errorClip = !string.IsNullOrEmpty(errorCandidate) && File.Exists(errorCandidate)
                    ? ButtonAnimatedClip.Load(errorCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    errorClip?.Dispose();
                    return;
                }

                var hasLoadedAnim = idleClip != null || connClip != null || activeClip != null || errorClip != null;
                if (!hasLoadedAnim || token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    errorClip?.Dispose();
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (token.IsCancellationRequested)
                    {
                        idleClip?.Dispose();
                        connClip?.Dispose();
                        activeClip?.Dispose();
                        errorClip?.Dispose();
                        return;
                    }

                    StopButtonAnimation();
                    _buttonAnimatedCurrent = null;
                    _buttonAnimatedPrev = null;
                    _buttonTransitionProgress = 1f;
                    _buttonAnimatedIdle?.Dispose();
                    _buttonAnimatedConnecting?.Dispose();
                    _buttonAnimatedActive?.Dispose();
                    _buttonAnimatedError?.Dispose();

                    _buttonAnimatedIdle = idleClip;
                    _buttonAnimatedConnecting = connClip;
                    _buttonAnimatedActive = activeClip;
                    _buttonAnimatedError = errorClip;

                    var currentState = _vpnService.CurrentState;
                    await UpdateCustomButtonStateAsync(currentState);
                });
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[VpnView] Ошибка фоновой загрузки анимации кнопки: {ex.Message}", ex);
            }
        }, token);
    }

    //проверка расширения анимированного файла
    private static bool IsPotentialAnimatedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".apng", StringComparison.OrdinalIgnoreCase);
    }

    //сброс кнопки к стандартному стилю приложения
    public void ResetThemeButton()
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;

        _buttonImageIdlePath = null;
        _buttonImageConnectingPath = null;
        _buttonImageActivePath = null;
        _buttonImageErrorPath = null;
        _activeCardFramePath = null;
        DisposeAnimatedButtonResources();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CardFrameImage.IsVisible = false;
            CardFrameImage.Source = null;
            CustomButtonRingImage.IsVisible = false;
            CustomButtonRingImage.Source = null;
            ResetCustomButtonStateOnly();
            OnThemeChanged();
        });
    }

    //управление видимостью декоративной рамки карточки
    public void SetCardFrameVisibility(bool visible)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var hasFrame = !string.IsNullOrEmpty(_activeCardFramePath) && File.Exists(_activeCardFramePath);
            CardFrameImage.IsVisible = visible && hasFrame;
        });
    }

    //сброс внутренних ссылок на анимацию кнопки
    private void ResetCustomButtonStateOnly()
    {
        CustomButtonImage.IsVisible = false;
        CustomButtonImage.Source = null;
        CustomButtonAnimatedCanvas.IsVisible = false;
        DefaultButtonContent.IsVisible = true;
        ConnectButtonCore.ClearValue(BackgroundColorProperty);
        ConnectButtonCore.ClearValue(Border.StrokeProperty);
        ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
        ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
    }

    //обновление текстуры и анимации кнопки под статус vpn
    private async Task UpdateCustomButtonStateAsync(AppVpnState state)
    {
        var targetClip = GetButtonAnimatedClipForState(state);
        var targetImagePath = GetButtonImagePathForState(state);

        if (targetClip != null)
        {
            if (_buttonAnimatedCurrent != null && _buttonAnimatedCurrent != targetClip)
            {
                _buttonAnimatedPrev = _buttonAnimatedCurrent;
                _buttonTransitionProgress = 0f;
            }

            _buttonAnimatedCurrent = targetClip;
            CustomButtonImage.IsVisible = false;
            CustomButtonAnimatedCanvas.IsVisible = true;
            DefaultButtonContent.IsVisible = false;
            ConnectButtonCore.BackgroundColor = Colors.Transparent;
            ConnectButtonCore.Stroke = Colors.Transparent;
            StartButtonAnimation();
            CustomButtonAnimatedCanvas.InvalidateSurface();
            return;
        }

        StopButtonAnimation();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedPrev = null;
        _buttonTransitionProgress = 1f;
        CustomButtonAnimatedCanvas.IsVisible = false;

        if (!string.IsNullOrEmpty(targetImagePath) && File.Exists(targetImagePath))
        {
            DefaultButtonContent.IsVisible = false;
            ConnectButtonCore.BackgroundColor = Colors.Transparent;
            ConnectButtonCore.Stroke = Colors.Transparent;

            if (CustomButtonImage.IsVisible)
            {
#pragma warning disable IDE0058
                await CustomButtonImage.FadeToAsync(0, 90, Easing.CubicOut);
                CustomButtonImage.Source = ImageSource.FromFile(targetImagePath);
                CustomButtonImage.Scale = 0.92;
                var fadeIn = CustomButtonImage.FadeToAsync(1, 130, Easing.CubicIn);
                var scaleIn = CustomButtonImage.ScaleToAsync(1.0, 200, Easing.SpringOut);
                await Task.WhenAll(fadeIn, scaleIn);
#pragma warning restore IDE0058
            }
            else
            {
                CustomButtonImage.Opacity = 1;
                CustomButtonImage.Scale = 1.0;
                CustomButtonImage.Source = ImageSource.FromFile(targetImagePath);
                CustomButtonImage.IsVisible = true;
            }

            return;
        }

        CustomButtonImage.IsVisible = false;
        CustomButtonImage.Source = null;
        DefaultButtonContent.IsVisible = true;
    }
}
