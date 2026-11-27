namespace obxodka.Pages;

public sealed partial class MainPage
{
    public void TriggerParticleBurst(float x, float y) =>
        ThemeParticles?.TriggerPointerBurst(x, y);

    private void HandlePointerInteraction(double x, double y, double width, double height)
    {
        ThemeParticles?.SetPointerPosition((float)x, (float)y, true);
        UpdateDecorationFade(x, y, width, height);
        UpdatePointerParallax(x, y, width, height);
    }

    private void UpdatePointerParallax(double x, double y, double width, double height)
    {
        if (width <= 1 || height <= 1)
        {
            return;
        }

        if (!_themeManager.ParallaxEnabled)
        {
            if (Math.Abs(ThemeBackgroundMover.TranslationX) > 0.001
                || Math.Abs(ThemeBackgroundMover.TranslationY) > 0.001
                || Math.Abs(ThemeBackgroundMover.Scale - 1.0) > 0.001)
            {
                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
            }

            if (Math.Abs(ThemeDecorationsContainer.TranslationX) < 0.001
                && Math.Abs(ThemeDecorationsContainer.TranslationY) < 0.001)
            {
                return;
            }

            ThemeDecorationsContainer.TranslationX = 0;
            ThemeDecorationsContainer.TranslationY = 0;

            return;
        }

        var rawParallax = _themeManager.ActiveTheme?.Vfx?.ParallaxIntensity ?? 0.35;
        var parallax = Math.Clamp(rawParallax <= 0.001 ? 0.35 : rawParallax, 0.0, 1.0);

        var nx = Math.Clamp((x / width - 0.5) * 2.0, -1.0, 1.0);
        var ny = Math.Clamp((y / height - 0.5) * 2.0, -1.0, 1.0);

        if (ThemeBackgroundImage.IsVisible || ThemeBackgroundVideo.IsVisible)
        {
            ThemeBackgroundMover.CancelAnimations();
            ThemeBackgroundMover.Scale = 1.015;
            var maxOffsetX = Math.Min(8.0 * parallax, 10.0);
            var maxOffsetY = Math.Min(3.0 * parallax, 4.0);
            ThemeBackgroundMover.TranslationX = -nx * maxOffsetX;
            ThemeBackgroundMover.TranslationY = -ny * maxOffsetY;
        }

        if (!ThemeDecorationsContainer.IsVisible)
        {
            return;
        }

        ThemeDecorationsContainer.TranslationX = nx * 3.0 * parallax;
        ThemeDecorationsContainer.TranslationY = ny * 2.0 * parallax;
    }

    private void OnThemeParallaxEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!enabled)
            {
                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;
            }
            else
            {
                ThemeBackgroundMover.Scale = 1.015;
            }
        });
    }

    private void UpdateDecorationFade(double x, double y, double width, double height)
    {
        if (!ThemeDecorationsContainer.IsVisible || width <= 0 || height <= 0)
        {
            return;
        }

        const double topThreshold = 140;
        const double bottomThreshold = 80;
        const double sideThreshold = 100;

        var isNearEdgeOrCorner = y < topThreshold
                                 || y > height - bottomThreshold
                                 || x < sideThreshold
                                 || x > width - sideThreshold;

        if (isNearEdgeOrCorner)
        {
            if (_isDecorationsFaded)
            {
                return;
            }

            _isDecorationsFaded = true;
            ThemeParticles?.SetScreenFrameOpacity(0f);
            ThemeDecorationsContainer.CancelAnimations();
            _ = ThemeDecorationsContainer.FadeToAsync(0.0, 120, Easing.CubicOut);
        }
        else if (_isDecorationsFaded)
        {
            _isDecorationsFaded = false;
            ThemeParticles?.SetScreenFrameOpacity(1f);
            ThemeDecorationsContainer.CancelAnimations();
            _ = ThemeDecorationsContainer.FadeToAsync(1.0, 180, Easing.CubicIn);
        }
    }

    private void RestoreDecorationsOpacity()
    {
        ThemeParticles?.SetPointerPosition(0, 0, false);

        if (ThemeBackgroundImage.IsVisible || ThemeBackgroundVideo.IsVisible)
        {
            ThemeBackgroundMover.CancelAnimations();
            _ = ThemeBackgroundMover.TranslateToAsync(0, 0, 220, Easing.CubicOut);
        }

        if (!ThemeDecorationsContainer.IsVisible)
        {
            return;
        }

        ThemeDecorationsContainer.TranslationX = 0;
        ThemeDecorationsContainer.TranslationY = 0;
        if (!_isDecorationsFaded)
        {
            return;
        }

        _isDecorationsFaded = false;
        ThemeParticles?.SetScreenFrameOpacity(1f);
        ThemeDecorationsContainer.CancelAnimations();
        _ = ThemeDecorationsContainer.FadeToAsync(1.0, 180, Easing.CubicIn);
    }

    private void OnPagePointerMoved(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            HandlePointerInteraction(pos.Value.X, pos.Value.Y, ThisPage.Width, ThisPage.Height);
        }
    }

    private void OnPagePointerPressed(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            ThemeParticles?.TriggerPointerBurst((float)pos.Value.X, (float)pos.Value.Y);
        }
    }

    private void OnPageTapped(object? sender, TappedEventArgs e)
    {
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue)
        {
            ThemeParticles?.TriggerPointerBurst((float)pos.Value.X, (float)pos.Value.Y);
        }
    }

    private void OnPagePointerExited(object? sender, PointerEventArgs e)
    {
#if WINDOWS
        var pos = e.GetPosition(ThisPage);
        if (pos.HasValue && ThisPage.Width > 0 && ThisPage.Height > 0)
        {
            var p = pos.Value;
            if (p is { X: > 4, Y: > 4 } && p.X < ThisPage.Width - 4 && p.Y < ThisPage.Height - 4)
            {
                return;
            }
        }
#endif
        RestoreDecorationsOpacity();
    }

    private void OnAppResumed() =>
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                if (_activeTab != "auth")
                {
                    SyncLiveBackgroundVpnState();
                    if (_activeTab == "configuration")
                    {
                        _tabContentConfiguration?.OnAppearing();
                    }

                    await SyncBalanceFromServerAsync();
#if ANDROID
                    if (OperatingSystem.IsAndroidVersionAtLeast(30))
                    {
                        _ = AndroidBillingRecoveryService.RecoverPendingPurchasesAsync(_apiService, TriggerBalanceSyncAsync);
                    }
#endif
                }

                await HandlePendingBackgroundActionAsync();
            }
            catch
            {
                // ignored
            }
        });

    private void OnWindowActivated()
    {
        _isWindowActive = true;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
            {
                try
                {
                    ThemeBackgroundVideo.Play();
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[MainPage] Error resuming video on activation: {ex.Message}");
                }
            }

            if (ThemeParticles.IsVisible)
            {
                try
                {
                    ThemeParticles.StartAnimation();
                }
                catch
                {
                    // ignored
                }
            }

            _tabContentVpn?.OnWindowVisibilityChanged(true);
            if (!string.IsNullOrEmpty(_activeTab) && _activeTab != "auth")
            {
                _ = SyncBalanceFromServerAsync();
            }
        });
    }

    private void OnWindowVisibilityChanged(bool isVisible) =>
        MainThread.BeginInvokeOnMainThread(() => _tabContentVpn?.OnWindowVisibilityChanged(isVisible));

    private void OnWindowDeactivated()
    {
        _isWindowActive = false;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var alwaysPlay = _themeManager.AlwaysPlayVideoEnabled;
            if (alwaysPlay)
            {
                return;
            }

            if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
            {
                try
                {
                    ThemeBackgroundVideo.Pause();
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[MainPage] Error pausing video on deactivation: {ex.Message}");
                }
            }

            if (ThemeParticles.IsVisible)
            {
                try
                {
                    ThemeParticles.StopAnimation();
                }
                catch
                {
                    // ignored
                }
            }
        });
    }
}
