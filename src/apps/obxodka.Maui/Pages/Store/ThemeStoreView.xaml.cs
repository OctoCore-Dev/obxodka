namespace obxodka.Pages;

public sealed partial class ThemeStoreView : ContentView
{
    private ThemeManager? _themeManager;
    private ThemeCatalogResponse? _currentCatalog;
    private readonly Dictionary<string, Label> _activeDownloadLabels = [];
    private string _currentTab = "community";
    private string _activeFilter = "all";
    private string _searchQuery = string.Empty;
    private double _currentSafeWidth = -1;
    private bool _isUpdatingSliderInternally;
    private readonly HashSet<VisualElement> _revealedCards = [];
    private readonly Dictionary<string, (Border CardBorder, HorizontalStackLayout ActionLayout, ThemeCardModel Model)> _cardMap = [];
    private ThemeCardModel? _previewModalModel;

    public event EventHandler? BackRequested;

    public ThemeStoreView()
    {
        InitializeComponent();

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            BtnBackText.IsVisible = false;
            BtnResetText.IsVisible = false;
        }

        ThemeScrollView.SizeChanged += (s, e) =>
        {
            if (ThemeScrollView.Width > 0)
            {
                ApplyCardWidth(ThemeScrollView.Width);
            }
        };

        ThemeScrollView.Scrolled += OnThemeScrollViewScrolled;

        var swipeLeft = new SwipeGestureRecognizer { Direction = SwipeDirection.Left };
        swipeLeft.Swiped += (s, e) =>
        {
            if (_currentTab == "community")
            {
                ThemeAudioService.PlayClick();
                _ = SwitchTabAnimatedAsync("installed");
            }
        };

        var swipeRight = new SwipeGestureRecognizer { Direction = SwipeDirection.Right };
        swipeRight.Swiped += (s, e) =>
        {
            if (_currentTab == "installed")
            {
                ThemeAudioService.PlayClick();
                _ = SwitchTabAnimatedAsync("community");
            }
        };

        ThemeCardsStack.GestureRecognizers.Add(swipeLeft);
        ThemeCardsStack.GestureRecognizers.Add(swipeRight);

        Unloaded += OnViewUnloaded;
    }

    private void OnViewUnloaded(object? sender, EventArgs e) => Cleanup();

    public void Cleanup()
    {
        if (_themeManager is { } manager)
        {
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
    }

    public void ForceLayoutWidth()
    {
        if (ThemeScrollView.Width > 0)
        {
            ApplyCardWidth(ThemeScrollView.Width);
        }
        else if (Width > 0 && RootLayout is not null)
        {
            var avail = Width - RootLayout.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
        {
            var topAir = Math.Max(top + 16, 50);
            RootLayout.Padding = new Thickness(16, topAir, 16, 0);
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
        {
            RootLayout.Padding = new Thickness(16, Math.Max(top + 4, 10), 16, 0);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width > 0 && RootLayout is not null)
        {
            var availableWidth = width - RootLayout.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        _currentSafeWidth = safeWidth;

        ThemeCardsStack.WidthRequest = safeWidth;
        ThemeCardsStack.MaximumWidthRequest = safeWidth;

        CardSearch.WidthRequest = safeWidth;
        CardSearch.MaximumWidthRequest = safeWidth;

        TabsGrid.WidthRequest = safeWidth;
        TabsGrid.MaximumWidthRequest = safeWidth;

        FilterChipsScrollView.WidthRequest = safeWidth;
        FilterChipsScrollView.MaximumWidthRequest = safeWidth;

        CardVfxSettings.WidthRequest = safeWidth;
        CardVfxSettings.MaximumWidthRequest = safeWidth;

        ThemesContainer.WidthRequest = safeWidth;
        ThemesContainer.MaximumWidthRequest = safeWidth;

        foreach (var child in ThemesContainer.Children)
        {
            if (child is VisualElement visual)
            {
                visual.WidthRequest = safeWidth;
                visual.MaximumWidthRequest = safeWidth;
            }
        }
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
        UpdateFilterChipsUI();

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
            if (child is VisualElement visual && !_revealedCards.Contains(visual))
            {
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

        if (ThemesContainer.Children.Count > 0)
        {
            CardStackScrollHelper.ApplyToChildren(
                ThemesContainer.Children.OfType<VisualElement>(),
                scrollY,
                viewportHeight,
                containerOffsetY: ThemesContainer.Y,
                zone: 70.0,
                minScale: 0.92,
                power: 2.2);
        }
    }

    private void HandleCatalogUpdated(ThemeCatalogResponse freshCatalog)
    {
        _currentCatalog = freshCatalog;
        UpdateTabButtons();
        RenderThemes(animate: false);
    }

    private void HandleDownloadProgressChanged(object? sender, (string ThemeId, double Progress, bool IsCompleted, bool Success, string? Error) e)
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
                RenderThemes(animate: false);
            }
            else
            {
                UpdateDownloadingCardLabel(e.ThemeId, e.Progress);
            }
        });
    }

    private void UpdateDownloadingCardLabel(string themeId, double progress)
    {
        if (_activeDownloadLabels.TryGetValue(themeId, out var label))
        {
            var pct = (int)Math.Round(progress * 100);
            label.Text = pct > 0 ? $"Загрузка {pct}%..." : "Загрузка...";
        }
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
            if (child is VisualElement visual)
            {
                cards.Add(visual);
                _ = _revealedCards.Add(visual);
            }
        }

        await UIAnimations.PlayEntranceCascadeAsync(35, 240, [.. cards]);
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
            _currentCatalog = await _themeManager.LoadCatalogAsync(forceRefresh: forceRefresh);
            UpdateTabButtons();
            RenderThemes(animate: true);
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
