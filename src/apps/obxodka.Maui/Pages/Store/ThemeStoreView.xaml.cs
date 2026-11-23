namespace obxodka.Pages.Store;

public sealed partial class ThemeStoreView : IAdaptivePage
{
    private readonly Dictionary<string, Label> _activeDownloadLabels = [];
    private readonly Dictionary<string, (Border CardBorder, HorizontalStackLayout ActionLayout, ThemeCardModel Model)> _cardMap = [];
    private readonly HashSet<VisualElement> _revealedCards = [];
    private string _activeFilter = "all";
    private ThemeCatalogResponse? _currentCatalog;
    private string _currentTab = "community";
    private bool _isUpdatingSliderInternally;
    private ThemeCardModel? _previewModalModel;
    private string _searchQuery = string.Empty;
    private ThemeManager? _themeManager;

    public ThemeStoreView()
    {
        InitializeComponent();

        ThemeScrollView.Scrolled += OnThemeScrollViewScrolled;

        var swipeLeft = new SwipeGestureRecognizer
        {
            Direction = SwipeDirection.Left
        };
        swipeLeft.Swiped += (s, e) =>
        {
            if (_currentTab != "community")
            {
                return;
            }

            ThemeAudioService.PlayClick();
            _ = SwitchTabAnimatedAsync("installed");
        };

        var swipeRight = new SwipeGestureRecognizer
        {
            Direction = SwipeDirection.Right
        };
        swipeRight.Swiped += (s, e) =>
        {
            if (_currentTab != "installed")
            {
                return;
            }

            ThemeAudioService.PlayClick();
            _ = SwitchTabAnimatedAsync("community");
        };

        ThemeCardsStack.GestureRecognizers.Add(swipeLeft);
        ThemeCardsStack.GestureRecognizers.Add(swipeRight);

        Unloaded += OnViewUnloaded;
    }

    private void OnViewUnloaded(object? sender, EventArgs e) => Cleanup();
    public event EventHandler? BackRequested;


    public void Cleanup()
    {
        if (_themeManager is not { } manager)
        {
            return;
        }

        manager.OnCatalogUpdated -= HandleCatalogUpdated;
        manager.ThemeDownloadProgressChanged -= HandleDownloadProgressChanged;
        manager.VideoEnabledChanged -= HandleVideoEnabledChanged;
        manager.AlwaysPlayVideoChanged -= HandleAlwaysPlayVideoChanged;
        manager.CardOpacityChanged -= HandleCardOpacityChanged;
        manager.GlowIntensityChanged -= HandleGlowIntensityChanged;
        manager.SoundsEnabledChanged -= HandleSoundsEnabledChanged;
        manager.ClickSoundsEnabledChanged -= HandleClickSoundsEnabledChanged;
        manager.SoundVolumeChanged -= HandleSoundVolumeChanged;
        manager.AmbientEnabledChanged -= HandleAmbientEnabledChanged;
        manager.ParticlesEnabledChanged -= HandleParticlesEnabledChanged;
        manager.FramesEnabledChanged -= HandleFramesEnabledChanged;
        _themeManager = null;
    }

    //адаптация отступов и модального окна каталога тем по единому снимку экрана
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayout is null || ThemeCardsStack is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);

        var topAir = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 14.0, isCompact ? 44.0 : 24.0)
            : isCompact ? 14.0 : 24.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(110.0, viewport.SafeBottom + 96.0)
            : Math.Max(28.0, viewport.SafeBottom + 16.0);

        var pad = new Thickness(sidePad, topAir, sidePad, 0);
        RootLayout.Padding = pad;
        ThemeCardsStack.HorizontalOptions = LayoutOptions.Fill;
        ThemeCardsStack.Padding = new Thickness(0, 0, 0, bottomPad);
        ThemePreviewModalOverlay?.Margin = new Thickness(-pad.Left, -pad.Top, -pad.Right, -pad.Bottom);

        if (ThemePreviewModalCard is not null && viewport.ContentWidth > 0)
        {
            var modalW = Math.Clamp(viewport.ContentWidth - 40.0, 280.0, 500.0);
            ThemePreviewModalCard.MaximumWidthRequest = modalW;
            ThemePreviewModalCard.WidthRequest = modalW;
        }

        BtnBackText.IsVisible = !isCompact;
        BtnResetText.IsVisible = !isCompact;
        LabelHeaderSubtitle.IsVisible = !isCompact;
        BtnBack.Padding = isCompact ? new Thickness(0) : new Thickness(12, 0);
        BtnBack.WidthRequest = isCompact ? 40 : -1;
        BtnResetTheme.Padding = isCompact ? new Thickness(0) : new Thickness(12, 0);
        BtnResetTheme.WidthRequest = isCompact ? 40 : -1;
    }

    public void Initialize(ThemeManager themeManager)
    {
        if (_themeManager is { } prevManager)
        {
            prevManager.OnCatalogUpdated -= HandleCatalogUpdated;
            prevManager.ThemeDownloadProgressChanged -= HandleDownloadProgressChanged;
            prevManager.VideoEnabledChanged -= HandleVideoEnabledChanged;
            prevManager.AlwaysPlayVideoChanged -= HandleAlwaysPlayVideoChanged;
            prevManager.CardOpacityChanged -= HandleCardOpacityChanged;
            prevManager.GlowIntensityChanged -= HandleGlowIntensityChanged;
            prevManager.SoundsEnabledChanged -= HandleSoundsEnabledChanged;
            prevManager.ClickSoundsEnabledChanged -= HandleClickSoundsEnabledChanged;
            prevManager.SoundVolumeChanged -= HandleSoundVolumeChanged;
            prevManager.AmbientEnabledChanged -= HandleAmbientEnabledChanged;
            prevManager.ParticlesEnabledChanged -= HandleParticlesEnabledChanged;
            prevManager.FramesEnabledChanged -= HandleFramesEnabledChanged;
        }

        _themeManager = themeManager;
        _themeManager.OnCatalogUpdated += HandleCatalogUpdated;
        _themeManager.ThemeDownloadProgressChanged += HandleDownloadProgressChanged;
        _themeManager.VideoEnabledChanged += HandleVideoEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged += HandleAlwaysPlayVideoChanged;
        _themeManager.CardOpacityChanged += HandleCardOpacityChanged;
        _themeManager.GlowIntensityChanged += HandleGlowIntensityChanged;
        _themeManager.SoundsEnabledChanged += HandleSoundsEnabledChanged;
        _themeManager.ClickSoundsEnabledChanged += HandleClickSoundsEnabledChanged;
        _themeManager.SoundVolumeChanged += HandleSoundVolumeChanged;
        _themeManager.AmbientEnabledChanged += HandleAmbientEnabledChanged;
        _themeManager.ParticlesEnabledChanged += HandleParticlesEnabledChanged;
        _themeManager.FramesEnabledChanged += HandleFramesEnabledChanged;

        RefreshSettingsControls();
        UpdateFilterChipsUi();

        _ = LoadCatalogAsync();
    }

    private void RefreshSettingsControls()
    {
        if (_themeManager == null)
        {
            return;
        }

        SwitchSounds.IsToggled = _themeManager.SoundsEnabled;
        SwitchClickSounds.IsToggled = _themeManager.ClickSoundsEnabled;
        SwitchAmbientSound.IsToggled = _themeManager.AmbientEnabled;
        SwitchParticles.IsToggled = _themeManager.ParticlesEnabled;
        SwitchFrames.IsToggled = _themeManager.FramesEnabled;
        SwitchVideoBackground.IsToggled = _themeManager.VideoEnabled;
        SwitchAlwaysPlayVideo.IsToggled = _themeManager.AlwaysPlayVideoEnabled;
        SwitchParallax.IsToggled = _themeManager.ParallaxEnabled;

        var opacity = _themeManager.CardOpacity;
        var glow = _themeManager.GlowIntensity;
        var volume = _themeManager.SoundVolume;
        _isUpdatingSliderInternally = true;
        try
        {
            SliderCardOpacity.Value = opacity;
            SliderGlowIntensity.Value = glow;
            SliderSoundVolume.Value = volume;
        }
        finally
        {
            _isUpdatingSliderInternally = false;
        }

        LabelCardOpacityValue.Text = $"{(int)Math.Round(opacity * 100)}%";
        LabelGlowIntensityValue.Text = $"{(int)Math.Round(glow * 100)}%";
        LabelSoundVolumeValue.Text = $"{(int)Math.Round(volume * 100)}%";
    }

    private void HandleVideoEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchVideoBackground.IsToggled != val)
            {
                SwitchVideoBackground.IsToggled = val;
            }
        });

    private void HandleAlwaysPlayVideoChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchAlwaysPlayVideo.IsToggled != val)
            {
                SwitchAlwaysPlayVideo.IsToggled = val;
            }
        });

    private void HandleCardOpacityChanged(double val)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isUpdatingSliderInternally)
            {
                return;
            }

            _isUpdatingSliderInternally = true;
            try
            {
                if (Math.Abs(SliderCardOpacity.Value - val) >= 0.01)
                {
                    SliderCardOpacity.Value = val;
                }

                LabelCardOpacityValue.Text = $"{(int)Math.Round(val * 100)}%";
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        });
    }

    private void HandleGlowIntensityChanged(double val)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isUpdatingSliderInternally)
            {
                return;
            }

            _isUpdatingSliderInternally = true;
            try
            {
                if (Math.Abs(SliderGlowIntensity.Value - val) >= 0.01)
                {
                    SliderGlowIntensity.Value = val;
                }

                LabelGlowIntensityValue.Text = $"{(int)Math.Round(val * 100)}%";
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        });
    }

    private void HandleSoundVolumeChanged(double val)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_isUpdatingSliderInternally)
            {
                return;
            }

            _isUpdatingSliderInternally = true;
            try
            {
                if (Math.Abs(SliderSoundVolume.Value - val) >= 0.01)
                {
                    SliderSoundVolume.Value = val;
                }

                LabelSoundVolumeValue.Text = $"{(int)Math.Round(val * 100)}%";
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        });
    }

    private void HandleSoundsEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchSounds.IsToggled != val)
            {
                SwitchSounds.IsToggled = val;
            }
        });

    private void HandleClickSoundsEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchClickSounds.IsToggled != val)
            {
                SwitchClickSounds.IsToggled = val;
            }
        });

    private void HandleAmbientEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchAmbientSound.IsToggled != val)
            {
                SwitchAmbientSound.IsToggled = val;
            }
        });

    private void HandleParticlesEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchParticles.IsToggled != val)
            {
                SwitchParticles.IsToggled = val;
            }
        });

    private void HandleFramesEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchFrames.IsToggled != val)
            {
                SwitchFrames.IsToggled = val;
            }
        });

    private void OnThemeScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        var viewportHeight = ThemeScrollView.Height;
        if (viewportHeight <= 0)
        {
            return;
        }

        var scrollY = e.ScrollY;
        var revealThreshold = scrollY + viewportHeight + 60;

        foreach (var child in ThemesContainer.Children)
        {
            if (child is not VisualElement visual || _revealedCards.Contains(visual))
            {
                continue;
            }

            if (visual.Y > 0 && visual.Y <= revealThreshold)
            {
                _ = _revealedCards.Add(visual);
                visual.Opacity = 0;
                visual.TranslationY = 16;
                _ = visual.FadeToAsync(1.0, 200, Easing.CubicOut);
                _ = visual.TranslateToAsync(0, 0, 200, Easing.CubicOut);
            }
        }
    }

    private void HandleCatalogUpdated(ThemeCatalogResponse freshCatalog)
    {
        _currentCatalog = freshCatalog;
        UpdateTabButtons();
        RenderThemes(false);
    }

    private void HandleDownloadProgressChanged(object? sender,
        (string ThemeId, double Progress, bool IsCompleted, bool Success, string? Error) e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (e.IsCompleted)
            {
                if (e.Success && _themeManager?.ActiveTheme?.Id == e.ThemeId)
                {
                    _ = _themeManager.ApplyThemeByIdAsync(e.ThemeId);
                }

                UpdateTabButtons();
                RenderThemes(false);
            }
            else
            {
                UpdateDownloadingCardLabel(e.ThemeId, e.Progress);
            }
        });
    }

    private void UpdateDownloadingCardLabel(string themeId, double progress)
    {
        if (!_activeDownloadLabels.TryGetValue(themeId, out var label))
        {
            return;
        }

        var pct = (int)Math.Round(progress * 100);
        label.Text = pct > 0 ? $"Загрузка {pct}%..." : "Загрузка...";
    }

    public async Task PlayEntranceAnimationAsync()
    {
        RefreshSettingsControls();
        RefreshStaticThemeColors();

        Opacity = 1;
        TranslationY = 0;

        var cards = new List<VisualElement>
        {
            CardSearch,
            TabsGrid,
            FilterChipsScrollView,
            CardVfxSettings
        };

        foreach (var child in ThemesContainer.Children.Take(4))
        {
            if (child is not VisualElement visual)
            {
                continue;
            }

            cards.Add(visual);
            _ = _revealedCards.Add(visual);
        }

        await UiAnimations.PlayEntranceCascadeAsync(35, 240, [.. cards]);
    }

    public async Task LoadCatalogAsync(bool forceRefresh = false)
    {
        if (_themeManager == null)
        {
            return;
        }

        var shouldShowLoader = _currentCatalog == null || forceRefresh;
        if (shouldShowLoader)
        {
            CatalogLoadingIndicator.IsRunning = true;
            CatalogLoadingIndicator.IsVisible = true;
            LabelStatus.IsVisible = false;
        }

        try
        {
            _currentCatalog = await _themeManager.LoadCatalogAsync(forceRefresh);
            UpdateTabButtons();
            RenderThemes();
        }
        catch (Exception ex)
        {
            LabelStatus.Text = $"Ошибка загрузки каталога тем: {ex.Message}";
            LabelStatus.IsVisible = true;
        }
        finally
        {
            CatalogLoadingIndicator.IsRunning = false;
            CatalogLoadingIndicator.IsVisible = false;
        }
    }
}
