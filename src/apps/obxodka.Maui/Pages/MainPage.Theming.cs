namespace obxodka.Pages;

public sealed partial class MainPage
{
    private static void ApplyThemeToVpnTab(VpnView vpn, ThemeAppliedEventArgs e) =>
        vpn.ApplyThemeButton(
            e.ButtonImageIdlePath,
            e.ButtonImageConnectingPath,
            e.ButtonImageActivePath,
            e.ButtonImageErrorPath,
            e.ButtonVideoIdlePath,
            e.ButtonVideoConnectingPath,
            e.ButtonVideoActivePath,
            e.ButtonVideoErrorPath,
            e.CardFramePath,
            e.ButtonRingPath);

    private void OnVpnStateChangedForTheme(AppVpnState state) =>
        MainThread.BeginInvokeOnMainThread(() => ThemeParticles?.TriggerVpnStateEffect(state));

    private void OnCardOpacityChanged(double opacity) =>
        MainThread.BeginInvokeOnMainThread(UpdateAllViewsOpacity);

    private void OnGlowIntensityChanged(double glow) =>
        MainThread.BeginInvokeOnMainThread(() => _tabContentVpn?.OnThemeChanged());

    private void OnEngineTrafficUpdated(long bytesSent, long bytesReceived)
    {
        var nowTicks = Environment.TickCount64;
        var elapsedSec = (nowTicks - _lastEngineTrafficTicks) / 1000.0;
        if (elapsedSec <= 0.05)
        {
            return;
        }

        var deltaBytes = Math.Max(0L, bytesSent - _lastEngineBytesSent + (bytesReceived - _lastEngineBytesReceived));
        _lastEngineBytesSent = bytesSent;
        _lastEngineBytesReceived = bytesReceived;
        _lastEngineTrafficTicks = nowTicks;

        var totalMbps = deltaBytes * 8.0 / (elapsedSec * 1_000_000.0);
        ThemeParticles?.SetTrafficSpeedMbps(totalMbps);
    }

    public void NotifyVpnConnected()
    {
        DesktopSidebar.UpdateVpnStatus(true);
        if (_vpnCts is null && RemainingSeconds > 0)
        {
            _vpnCts = new CancellationTokenSource();
            _ = ConsumeTimeLoopAsync(_vpnCts.Token);
        }

        ThemeParticles?.TriggerVpnStateEffect(AppVpnState.Connected);

        if (_themeManager.SoundsEnabled)
        {
            ThemeAudioService.PlayConnect();
        }
    }

    public void NotifyVpnDisconnected()
    {
        DesktopSidebar.UpdateVpnStatus(false);
        var prevCts = Interlocked.Exchange(ref _vpnCts, null);
        if (prevCts is not null)
        {
            try
            {
                prevCts.Cancel();
            }
            catch
            {
                // ignored
            }
            finally
            {
                prevCts.Dispose();
            }
        }

        ThemeParticles?.TriggerVpnStateEffect(AppVpnState.Disconnected);
        ThemeParticles?.SetTrafficSpeedMbps(0);
        ThemeAudioService.UpdateAmbientState(false);

        if (_themeManager.SoundsEnabled)
        {
            ThemeAudioService.PlayDisconnect();
        }
    }

    private static ImageSource? LoadLocalImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            //не удерживаем сырой byte[] в замыкании лямбды — читаем с диска по требованию и отпускаем память после декодирования
            return ImageSource.FromStream(() => new MemoryStream(File.ReadAllBytes(path), false));
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[MainPage] Error loading local image '{path}': {ex.Message}");
            return null;
        }
    }

    private static void SetDecorationElement(Image target, string? path, ref bool hasAny)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            target.Source = LoadLocalImage(path);
            target.IsVisible = true;
            hasAny = true;
        }
        else
        {
            target.Source = null;
            target.IsVisible = false;
        }
    }

    private static void ClearImageElement(Image target)
    {
        target.Source = null;
        target.IsVisible = false;
    }

    private void UpdateAllViewsOpacity()
    {
        _tabContentVpn?.UpdateCardOpacity();
        DesktopSidebar?.UpdateCardOpacity();
        MobileBottomBar?.UpdateCardOpacity();
        _tabContentConfiguration?.UpdateCardOpacity();
        _tabContentProfile?.UpdateCardOpacity();
        _tabContentThemeStore?.UpdateCardOpacity();
        _tabContentDevices?.UpdateCardOpacity();
        _tabContentPayment?.UpdateCardOpacity();
        _tabContentDelete?.UpdateCardOpacity();
        _tabContentSplit?.UpdateCardOpacity();
        _tabContentAuth?.UpdateCardOpacity();
    }

    private void NotifyViewsThemeChanged()
    {
        UpdateAllViewsOpacity();
        _tabContentVpn?.OnThemeChanged();
        _tabContentConfiguration?.OnThemeChanged();
        _tabContentSplit?.OnThemeChanged();
        DesktopSidebar?.OnThemeChanged();
        MobileBottomBar?.OnThemeChanged();
        _tabContentProfile?.OnThemeChanged();
        _tabContentThemeStore?.OnThemeChanged();
        _tabContentDevices?.OnThemeChanged();
        _tabContentPayment?.OnThemeChanged();
        _tabContentDelete?.OnThemeChanged();
        _tabContentAuth?.OnThemeChanged();
        NeoDialog?.OnThemeChanged();
    }

    private async Task ShowThemeTransitionAsync(string title, string? subtitle = null)
    {
        if (SplashOverlay.IsVisible)
        {
            return;
        }

        try
        {
            ThemeTransitionTitle.Text = title;
            ThemeTransitionSubtitle.Text = subtitle ?? "Настройка визуального оформления";
            ThemeTransitionSpinner.IsRunning = true;
            if (ThemeTransitionOverlay.IsVisible && ThemeTransitionOverlay.Opacity >= 0.9)
            {
                return;
            }

            ThemeTransitionOverlay.Opacity = 0;
            ThemeTransitionOverlay.IsVisible = true;
            _ = await ThemeTransitionOverlay.FadeToAsync(1.0, 140, Easing.CubicOut);
        }
        catch
        {
            // ignored
        }
    }

    private async Task HideThemeTransitionAsync()
    {
        if (!ThemeTransitionOverlay.IsVisible)
        {
            return;
        }

        try
        {
            await Task.Delay(180);
            _ = await ThemeTransitionOverlay.FadeToAsync(0.0, 160, Easing.CubicIn);
            ThemeTransitionOverlay.IsVisible = false;
            ThemeTransitionSpinner.IsRunning = false;
        }
        catch
        {
            ThemeTransitionOverlay.IsVisible = false;
        }
    }

    private void HandleThemeApplied(ThemeAppliedEventArgs e)
    {
        _lastAppliedThemeArgs = e;
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                await ShowThemeTransitionAsync("Применение темы...", e.Manifest.Name);

                var bgBaseColor = Color.TryParse(e.Manifest.Core.Colors.BgBase, out var parsedBgBase)
                    ? parsedBgBase
                    : Color.FromArgb("#0E0E14");

                ThisPage.BackgroundColor = bgBaseColor;
                BackgroundColor = bgBaseColor;
#if ANDROID
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    MainActivity.UpdateSystemBarsTheme(bgBaseColor);
                }
#endif

                var willPlayVideo = !string.IsNullOrEmpty(e.BackgroundVideoPath) &&
                                    File.Exists(e.BackgroundVideoPath) &&
                                    _themeManager.VideoEnabled;

                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = _themeManager.ParallaxEnabled ? 1.015 : 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;

                if (!string.IsNullOrEmpty(e.BackgroundImagePath) && File.Exists(e.BackgroundImagePath))
                {
                    ThemeBackgroundImage.Source = LoadLocalImage(e.BackgroundImagePath);
                    ThemeBackgroundImage.Opacity = willPlayVideo ? 1.0 : e.Manifest.Background?.Opacity ?? 0.45;
                    ThemeBackgroundImage.Scale = 1.0;
                    ThemeBackgroundImage.TranslationX = 0;
                    ThemeBackgroundImage.TranslationY = 0;
                    ThemeBackgroundImage.IsVisible = true;
                }
                else
                {
                    ClearImageElement(ThemeBackgroundImage);
                }

                var framesEnabled = _themeManager.FramesEnabled;
                _activeThemeScreenFramePath = e.ScreenFramePath;
                _activeThemeFrameSlice = e.FrameSlice;

                var hasScreenFrame = !string.IsNullOrEmpty(e.ScreenFramePath) && File.Exists(e.ScreenFramePath);
                if (framesEnabled && hasScreenFrame)
                {
                    ThemeParticles.SetScreenFrame(e.ScreenFramePath, e.FrameSlice);
                }
                else
                {
                    ThemeParticles.ClearScreenFrame();
                }

                var hasTopDecorations = false;
                SetDecorationElement(ThemeCornerTopLeft, e.CornerTopLeftPath, ref hasTopDecorations);
                SetDecorationElement(ThemeCornerTopRight, e.CornerTopRightPath, ref hasTopDecorations);

                var hasBottomDecorations = false;
                SetDecorationElement(ThemeCornerBottomLeft, e.CornerBottomLeftPath, ref hasBottomDecorations);
                SetDecorationElement(ThemeCornerBottomRight, e.CornerBottomRightPath, ref hasBottomDecorations);

                var hasDecorations = hasTopDecorations || hasBottomDecorations;
                _hasThemeCornerDecorations = hasDecorations;
                ThemeDecorationsContainer.IsVisible = framesEnabled && hasDecorations;
                ThemeDecorationsContainer.Opacity = 1.0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;

                MainContentContainer.Margin = new Thickness(0);

                _ = Color.TryParse(e.Manifest.Core.Colors.Primary, out var primary);
                _ = Color.TryParse(e.Manifest.Core.Colors.Accent ?? string.Empty, out var accent);
                _activeParticleSpritePaths = e.ParticleSpritePaths;
                _activeVfx = e.Manifest.Vfx;
                _activeParticlePrimary = primary;
                _activeParticleAccent = accent;

                ThemeParticles.Configure(
                    e.ParticleSpritePaths,
                    e.Manifest.Vfx,
                    primary,
                    accent,
                    _themeManager.ParticlesEnabled,
                    e.ScreenOverlayPath);

                if (_tabContentVpn is not null)
                {
                    ApplyThemeToVpnTab(_tabContentVpn, e);
                }

                try
                {
                    ThemeBackgroundVideo.Stop();
                }
                catch
                {
                    // ignored
                }

                if (willPlayVideo && e.BackgroundVideoPath is not null)
                {
                    try
                    {
                        _isThemeVideoActive = true;
                        ThemeBackgroundVideo.ShouldShowPlaybackControls = false;
                        ThemeBackgroundVideo.Opacity = 1.0;
                        ThemeBackgroundVideo.Scale = 1.0;
                        ThemeBackgroundVideo.TranslationX = 0;
                        ThemeBackgroundVideo.TranslationY = 0;
                        ThemeBackgroundVideo.Source = MediaSource.FromFile(e.BackgroundVideoPath);
                        ThemeBackgroundVideo.IsVisible = true;
                        if (_isWindowActive)
                        {
                            ThemeBackgroundVideo.Play();
                        }
                        else
                        {
                            ThemeBackgroundVideo.Pause();
                        }

                        var targetOpacity = e.Manifest.Background?.Opacity ?? 0.45;
                        ThemeBackgroundVideoDimmer.Color = bgBaseColor;
                        ThemeBackgroundVideoDimmer.Opacity = Math.Clamp(1.0 - targetOpacity, 0.0, 1.0);
                        ThemeBackgroundVideoDimmer.IsVisible = true;

#if WINDOWS
                        WinUiMainPageHelper.ConfigureVideoPlayer(ThemeBackgroundVideo);
#endif
                    }
                    catch (Exception ex)
                    {
                        AppLogger.LogWarning($"[MainPage] Video playback error: {ex.Message}");
                    }
                }
                else
                {
                    try
                    {
                        _isThemeVideoActive = false;
#if WINDOWS
                        WinUiMainPageHelper.StopVideoPlayer(ThemeBackgroundVideo);
#endif
                        ThemeBackgroundVideo.IsVisible = false;
                        ThemeBackgroundVideo.Source = null;
                        ThemeBackgroundVideoDimmer.IsVisible = false;
                    }
                    catch
                    {
                        // ignored
                    }
                }

                ThemeAudioService.UpdateAmbientState(false);

                NotifyViewsThemeChanged();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[MainPage] HandleThemeApplied error: {ex.Message}");
            }
            finally
            {
                await HideThemeTransitionAsync();
            }
        });
    }

    //мгновенная синхронизация системной темы ос без оверлея перехода и без влияния на кастомные темы
    private void HandleSystemThemeSynced()
    {
        if (_themeManager.ActiveTheme is not null)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_themeManager.ActiveTheme is not null)
            {
                return;
            }

            var bgBase = Application.Current?.Resources.TryGetValue("BgBase", out var bgObj) == true && bgObj is Color c
                ? c
                : Color.FromArgb("#202020");

            ThisPage.BackgroundColor = bgBase;
            BackgroundColor = bgBase;
#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                MainActivity.UpdateSystemBarsTheme(bgBase);
            }
#endif
            NotifyViewsThemeChanged();
        });
    }

    private void HandleThemeReset()
    {
        _lastAppliedThemeArgs = null;
        MainThread.BeginInvokeOnMainThread(async void () =>
        {
            try
            {
                await ShowThemeTransitionAsync("Сброс темы...", "Возвращение к системному оформлению");

                var defaultBg = Application.Current?.Resources.TryGetValue("BgBase", out var bgObj) == true && bgObj is Color c
                    ? c
                    : Color.FromArgb("#202020");
                ThisPage.BackgroundColor = defaultBg;
                BackgroundColor = defaultBg;
#if ANDROID
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    MainActivity.UpdateSystemBarsTheme(defaultBg);
                }
#endif

                ThemeBackgroundMover.CancelAnimations();
                ThemeBackgroundMover.Scale = 1.0;
                ThemeBackgroundMover.TranslationX = 0;
                ThemeBackgroundMover.TranslationY = 0;
                ClearImageElement(ThemeBackgroundImage);
                ThemeBackgroundImage.Opacity = 1.0;
                ThemeBackgroundImage.Scale = 1.0;
                ThemeBackgroundImage.TranslationX = 0;
                ThemeBackgroundImage.TranslationY = 0;
                ThemeParticles.ClearScreenFrame();
                ClearImageElement(ThemeCornerTopLeft);
                ClearImageElement(ThemeCornerTopRight);
                ClearImageElement(ThemeCornerBottomLeft);
                ClearImageElement(ThemeCornerBottomRight);
                ThemeDecorationsContainer.IsVisible = false;
                ThemeDecorationsContainer.Opacity = 1.0;
                ThemeDecorationsContainer.TranslationX = 0;
                ThemeDecorationsContainer.TranslationY = 0;
                _isDecorationsFaded = false;
                MainContentContainer.Margin = new Thickness(0);

                ThemeParticles.Configure(null, null, null, null, false);
                _activeThemeScreenFramePath = null;
                _activeThemeFrameSlice = null;
                _hasThemeCornerDecorations = false;
                _activeParticleSpritePaths = null;
                _activeVfx = null;
                _activeParticlePrimary = Colors.Transparent;
                _activeParticleAccent = Colors.Transparent;

                ThemeAudioService.ReleaseMediaResources();

#if WINDOWS
                WinUiMainPageHelper.StopVideoPlayer(ThemeBackgroundVideo);
#endif
                try
                {
                    _isThemeVideoActive = false;
                    ThemeBackgroundVideo.Stop();
                }
                catch
                {
                    // ignored
                }

                ThemeBackgroundVideo.IsVisible = false;
                ThemeBackgroundVideo.Source = null;
                ThemeBackgroundVideoDimmer.IsVisible = false;
                ThemeBackgroundVideoDimmer.Color = Colors.Transparent;
                ThemeBackgroundVideoDimmer.Opacity = 0;

                _tabContentVpn?.ResetThemeButton();
                NotifyViewsThemeChanged();
                ScheduleMemoryTrim();
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[MainPage] HandleThemeReset error: {ex.Message}");
            }
            finally
            {
                await HideThemeTransitionAsync();
            }
        });
    }

    private void OnThemeParticlesEnabledChanged(bool enabled) =>
        MainThread.BeginInvokeOnMainThread(() =>
            ThemeParticles.Configure(
                _activeParticleSpritePaths,
                _activeVfx,
                _activeParticlePrimary,
                _activeParticleAccent,
                enabled));

    private void OnThemeFramesEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var hasFrame = !string.IsNullOrEmpty(_activeThemeScreenFramePath) && File.Exists(_activeThemeScreenFramePath);
            if (enabled && hasFrame)
            {
                ThemeParticles.SetScreenFrame(_activeThemeScreenFramePath, _activeThemeFrameSlice);
            }
            else
            {
                ThemeParticles.ClearScreenFrame();
            }

            ThemeDecorationsContainer.IsVisible = enabled && _hasThemeCornerDecorations;
            ThemeParticles.InvalidateSurface();
            _tabContentVpn?.SetCardFrameVisibility(enabled);
        });
    }

    private void OnThemeVideoEnabledChanged(bool enabled)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!enabled)
            {
                _isThemeVideoActive = false;
                try
                {
                    ThemeBackgroundVideo.Stop();
                }
                catch
                {
                    // ignored
                }

                ThemeBackgroundVideo.IsVisible = false;
                ThemeBackgroundVideo.Source = null;
                ThemeBackgroundVideoDimmer.IsVisible = false;
                if (ThemeBackgroundImage.IsVisible)
                {
                    ThemeBackgroundImage.Opacity = _themeManager.ActiveTheme?.Background?.Opacity ?? 0.45;
                }
            }
            else if (_themeManager.ActiveTheme is not null)
            {
                _themeManager.ReapplyActiveTheme();
            }
        });
    }

    private void OnAlwaysPlayVideoChanged(bool alwaysPlay)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            switch (alwaysPlay)
            {
                case true when !_isWindowActive:
                    {
                        if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
                        {
                            try
                            {
                                ThemeBackgroundVideo.Play();
                            }
                            catch
                            {
                                // ignored
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

                        _tabContentVpn?.OnWindowFocusChanged(true);
                        break;
                    }
                case false when !_isWindowActive:
                    {
                        if (_isThemeVideoActive && ThemeBackgroundVideo.IsVisible)
                        {
                            try
                            {
                                ThemeBackgroundVideo.Pause();
                            }
                            catch
                            {
                                // ignored
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

                        _tabContentVpn?.OnWindowFocusChanged(false);
                        break;
                    }
            }
        });
    }
}
