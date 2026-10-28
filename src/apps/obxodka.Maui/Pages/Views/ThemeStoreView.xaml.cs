using Microsoft.Maui.Controls.Shapes;
using obxodka.Helpers;
using Path = System.IO.Path;

namespace obxodka.Views;

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

        CardStackScrollHelper.ApplyToChildren(
            [CardSearch, CardVfxSettings],
            scrollY,
            viewportHeight,
            containerOffsetY: 0.0,
            zone: 65.0,
            minScale: 0.94,
            power: 2.2);

        if (ThemesContainer.Children.Count > 0)
        {
            CardStackScrollHelper.ApplyToChildren(
                ThemesContainer.Children.OfType<VisualElement>(),
                scrollY,
                viewportHeight,
                containerOffsetY: ThemesContainer.Y,
                zone: 65.0,
                minScale: 0.94,
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

    private void UpdateTabButtons()
    {
        var primaryColor = GetAppColor("Primary", Color.FromArgb("#7C3AED"));
        var cardBgColor = GetAppColor("BgSurface", Color.FromArgb("#161622"));
        var textMutedColor = GetAppColor("TextMuted", Color.FromArgb("#A0A0B0"));
        var borderSubtleColor = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));

        var installedCount = _themeManager?.GetInstalledThemes().Count ?? 0;
        LabelTabInstalled.Text = installedCount > 0 ? $"Установленные ({installedCount})" : "Установленные";
        LabelTabCommunity.Text = "Сообщество";

        if (_currentTab == "community")
        {
            BtnTabCommunity.BackgroundColor = primaryColor;
            BtnTabCommunity.Stroke = Colors.Transparent;
            BtnTabCommunity.StrokeThickness = 0;
            IconTabCommunity.IconColor = Colors.White;
            LabelTabCommunity.TextColor = Colors.White;

            BtnTabInstalled.BackgroundColor = cardBgColor;
            BtnTabInstalled.Stroke = borderSubtleColor;
            BtnTabInstalled.StrokeThickness = 1;
            IconTabInstalled.IconColor = textMutedColor;
            LabelTabInstalled.TextColor = textMutedColor;
        }
        else
        {
            BtnTabInstalled.BackgroundColor = primaryColor;
            BtnTabInstalled.Stroke = Colors.Transparent;
            BtnTabInstalled.StrokeThickness = 0;
            IconTabInstalled.IconColor = Colors.White;
            LabelTabInstalled.TextColor = Colors.White;

            BtnTabCommunity.BackgroundColor = cardBgColor;
            BtnTabCommunity.Stroke = borderSubtleColor;
            BtnTabCommunity.StrokeThickness = 1;
            IconTabCommunity.IconColor = textMutedColor;
            LabelTabCommunity.TextColor = textMutedColor;
        }
    }

    private void UpdateFilterChipsUI()
    {
        var primaryColor = GetAppColor("Primary", Color.FromArgb("#7C3AED"));
        var cardBgColor = GetAppColor("BgSurface", Color.FromArgb("#161622"));
        var textMutedColor = GetAppColor("TextMuted", Color.FromArgb("#A0A0B0"));
        var borderSubtleColor = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));

        void StyleChip(Border chip, Label label, bool isActive)
        {
            chip.BackgroundColor = isActive ? primaryColor : cardBgColor;
            chip.Stroke = isActive ? Colors.Transparent : borderSubtleColor;
            label.TextColor = isActive ? Colors.White : textMutedColor;
            label.SetDynamicResource(Label.FontFamilyProperty, isActive ? "AppFontBold" : "AppFontMedium");
        }

        StyleChip(ChipFilterAll, LabelFilterAll, _activeFilter == "all");
        StyleChip(ChipFilterVideo, LabelFilterVideo, _activeFilter == "video");
        StyleChip(ChipFilterAnim, LabelFilterAnim, _activeFilter == "anim");
        StyleChip(ChipFilterSound, LabelFilterSound, _activeFilter == "sound");
        StyleChip(ChipFilterShaders, LabelFilterShaders, _activeFilter == "shaders");
    }

    private bool MatchesCategoryFilter(ThemeCardModel model)
    {
        return _activeFilter switch
        {
            "video" => model.HasVideoBackground,
            "anim" => model.HasAnimatedButton || model.HasParticles,
            "sound" => model.HasSounds,
            "shaders" => model.HasScreenOverlay || model.HasReactiveVfx,
            _ => true
        };
    }

    private void RenderThemes(bool animate = true)
    {
        if (_themeManager == null)
        {
            ThemesContainer.Clear();
            _cardMap.Clear();
            _activeDownloadLabels.Clear();
            LabelStatus.IsVisible = false;
            return;
        }

        var installedList = _themeManager.GetInstalledThemes();
        var installedMap = installedList.ToDictionary(t => t.Id, t => t);
        var models = new List<ThemeCardModel>();

        if (_currentTab == "installed")
        {
            foreach (var manifest in installedList)
            {
                if (!MatchesSearch(manifest.Name, manifest.Id, manifest.Description, manifest.Author, manifest.Tags))
                {
                    continue;
                }

                var folder = Path.Combine(_themeManager.ThemesDirectory, manifest.Id);
                var isActive = _themeManager.ActiveTheme?.Id == manifest.Id;
                var remoteItem = _currentCatalog?.Themes.FirstOrDefault(t => t.Id == manifest.Id);
                var hasUpdate = remoteItem != null && ThemeManager.IsVersionNewer(manifest.Version, remoteItem.Version);

                var hasVideoBg = !string.IsNullOrEmpty(manifest.Background?.VideoSource);
                var hasAnimBtn = !string.IsNullOrEmpty(manifest.Decorations?.ButtonVideoIdle) ||
                                 !string.IsNullOrEmpty(manifest.Decorations?.ButtonVideoActive) ||
                                 (manifest.Decorations?.ButtonImageIdle?.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) == true);
                var hasOverlay = !string.IsNullOrEmpty(manifest.Vfx?.ScreenOverlay) &&
                                 !manifest.Vfx.ScreenOverlay.Equals("none", StringComparison.OrdinalIgnoreCase);
                var hasReactive = manifest.Vfx?.InteractiveParticles == true ||
                                  manifest.Vfx?.TrafficReactive == true ||
                                  manifest.Vfx?.ParallaxIntensity > 0 ||
                                  manifest.Vfx?.ActiveOverrides != null;

                var model = new ThemeCardModel(
                    Id: manifest.Id,
                    Name: manifest.Name,
                    Author: manifest.Author,
                    DisplayVersion: manifest.Version,
                    Description: manifest.Description,
                    HasSounds: manifest.Core?.Sounds != null,
                    HasParticles: manifest.Vfx?.Particles != null && manifest.Vfx.Particles != "none",
                    HasFrames: !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrame) || !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrameMobile) || !string.IsNullOrEmpty(manifest.Decorations?.CardFrame),
                    HasVideoBackground: hasVideoBg,
                    HasAnimatedButton: hasAnimBtn,
                    HasScreenOverlay: hasOverlay,
                    HasReactiveVfx: hasReactive,
                    RemoteIconUrl: remoteItem?.IconUrl,
                    LocalIconPath: Path.Combine(folder, "icon.png"),
                    RemotePreviewUrl: remoteItem?.PreviewUrl,
                    LocalPreviewPath: Path.Combine(folder, "preview.png"),
                    Colors: manifest.Core?.Colors,
                    IsInstalled: true,
                    IsActive: isActive,
                    HasUpdate: hasUpdate,
                    UpdateVersion: remoteItem?.Version,
                    IsCommunity: false
                );

                if (MatchesCategoryFilter(model))
                {
                    models.Add(model);
                }
            }
        }
        else
        {
            if (_currentCatalog != null && _currentCatalog.Themes.Count > 0)
            {
                foreach (var item in _currentCatalog.Themes)
                {
                    if (!MatchesSearch(item.Name, item.Id, item.Description, item.Author, item.Tags))
                    {
                        continue;
                    }

                    var isInstalled = installedMap.TryGetValue(item.Id, out var installedManifest);
                    var isActive = _themeManager.ActiveTheme?.Id == item.Id;
                    var installedVersion = isInstalled ? _themeManager.GetInstalledThemeVersion(item.Id) : null;
                    var displayVersion = !string.IsNullOrEmpty(installedVersion) ? installedVersion : item.Version;
                    var hasUpdate = isInstalled && _themeManager.IsUpdateAvailable(item.Id, item.Version);
                    var folder = isInstalled ? Path.Combine(_themeManager.ThemesDirectory, item.Id) : null;

                    var model = new ThemeCardModel(
                        Id: item.Id,
                        Name: item.Name,
                        Author: item.Author,
                        DisplayVersion: displayVersion,
                        Description: item.Description,
                        HasSounds: item.Features.HasSounds,
                        HasParticles: item.Features.HasParticles,
                        HasFrames: item.Features.HasFrames,
                        HasVideoBackground: item.Features.HasVideo,
                        HasAnimatedButton: item.Features.HasButtonVideo || item.Features.HasIdolButton,
                        HasScreenOverlay: item.Features.HasScreenOverlay,
                        HasReactiveVfx: item.Features.HasReactiveVfx,
                        RemoteIconUrl: item.IconUrl,
                        LocalIconPath: folder != null ? Path.Combine(folder, "icon.png") : null,
                        RemotePreviewUrl: item.PreviewUrl,
                        LocalPreviewPath: folder != null ? Path.Combine(folder, "preview.png") : null,
                        Colors: installedManifest?.Core?.Colors,
                        IsInstalled: isInstalled,
                        IsActive: isActive,
                        HasUpdate: hasUpdate,
                        UpdateVersion: item.Version,
                        IsCommunity: true
                    );

                    if (MatchesCategoryFilter(model))
                    {
                        models.Add(model);
                    }
                }
            }
        }

        if (models.Count == 0)
        {
            ThemesContainer.Clear();
            _cardMap.Clear();
            _activeDownloadLabels.Clear();
            LabelStatus.Text = _currentTab == "installed"
                ? (string.IsNullOrWhiteSpace(_searchQuery) && _activeFilter == "all"
                    ? "У вас пока нет установленных тем.\nПерейдите во вкладку 'Сообщество', чтобы выбрать и скачать темы!"
                    : "Ничего не найдено по выбранным фильтрам.")
                : (_currentCatalog == null || _currentCatalog.Themes.Count == 0
                    ? "Каталог тем временно недоступен или пуст."
                    : "Ничего не найдено по выбранным фильтрам.");
            LabelStatus.IsVisible = true;
            return;
        }

        LabelStatus.IsVisible = false;

        var canUpdateInPlace = _cardMap.Count > 0 &&
                               _cardMap.Count == models.Count &&
                               models.All(m => _cardMap.ContainsKey(m.Id));

        if (canUpdateInPlace)
        {
            for (var i = 0; i < models.Count; i++)
            {
                var model = models[i];
                if (_cardMap.TryGetValue(model.Id, out var holder))
                {
                    holder.CardBorder.StrokeThickness = model.IsActive ? 2.0 : 1.0;
                    holder.CardBorder.SetDynamicResource(Border.StrokeProperty, model.IsActive ? "Primary" : "BorderSubtle");
                    holder.ActionLayout.Children.Clear();
                    PopulateActionLayout(holder.ActionLayout, model);
                    _cardMap[model.Id] = (holder.CardBorder, holder.ActionLayout, model);
                }
            }
            return;
        }

        var prevScrollY = ThemeScrollView.ScrollY;

        ThemesContainer.Clear();
        _cardMap.Clear();
        _activeDownloadLabels.Clear();
        _revealedCards.Clear();

        var index = 0;
        foreach (var model in models)
        {
            var (card, actionLayout) = CreateThemeCard(model);
            ThemesContainer.Add(card);
            _cardMap[model.Id] = (card, actionLayout, model);

            if (_currentSafeWidth > 0)
            {
                card.WidthRequest = _currentSafeWidth;
                card.MaximumWidthRequest = _currentSafeWidth;
            }

            if (!animate || index < 4)
            {
                card.Opacity = 1.0;
                card.Scale = 1.0;
                card.TranslationY = 0;
                _ = _revealedCards.Add(card);
            }
            index++;
        }

        if (prevScrollY > 10)
        {
            _ = Dispatcher.DispatchDelayed(TimeSpan.FromMilliseconds(40), () => _ = ThemeScrollView.ScrollToAsync(0, prevScrollY, animated: false));
        }
    }

    private bool MatchesSearch(string name, string id, string? desc, string author, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return true;
        }

        var q = _searchQuery.Trim();
        return name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || id.Contains(q, StringComparison.OrdinalIgnoreCase)
            || author.Contains(q, StringComparison.OrdinalIgnoreCase)
            || (desc != null && desc.Contains(q, StringComparison.OrdinalIgnoreCase))
            || (tags != null && tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
    }

    private sealed record ThemeCardModel(
        string Id,
        string Name,
        string Author,
        string DisplayVersion,
        string? Description,
        bool HasSounds,
        bool HasParticles,
        bool HasFrames,
        bool HasVideoBackground,
        bool HasAnimatedButton,
        bool HasScreenOverlay,
        bool HasReactiveVfx,
        string? RemoteIconUrl,
        string? LocalIconPath,
        string? RemotePreviewUrl,
        string? LocalPreviewPath,
        ThemeColors? Colors,
        bool IsInstalled,
        bool IsActive,
        bool HasUpdate,
        string? UpdateVersion,
        bool IsCommunity
    );

    private (Border CardBorder, HorizontalStackLayout ActionLayout) CreateThemeCard(ThemeCardModel model)
    {
        var cardBorder = new Border
        {
            Padding = new Thickness(16),
            StrokeThickness = model.IsActive ? 2.0 : 1.0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(16) }
        };
        cardBorder.SetDynamicResource(BackgroundColorProperty, "BgSurface");
        cardBorder.SetDynamicResource(Border.StrokeProperty, model.IsActive ? "Primary" : "BorderSubtle");

        if (_currentSafeWidth > 0)
        {
            cardBorder.WidthRequest = _currentSafeWidth;
            cardBorder.MaximumWidthRequest = _currentSafeWidth;
        }

        var mainLayout = new Grid
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star)
            ],
            ColumnSpacing = 14
        };

        var iconSource = !string.IsNullOrEmpty(model.LocalIconPath) && File.Exists(model.LocalIconPath)
            ? LoadLocalImage(model.LocalIconPath)
            : !string.IsNullOrEmpty(model.RemoteIconUrl)
                ? new UriImageSource
                {
                    Uri = new Uri(ThemeManager.ToMirrorUrl(model.RemoteIconUrl)),
                    CachingEnabled = true,
                    CacheValidity = TimeSpan.FromDays(14)
                }
                : (ImageSource)"splash_logo.png";

        var iconImage = new Image
        {
            WidthRequest = 56,
            HeightRequest = 56,
            Aspect = Aspect.AspectFill,
            Clip = new RoundRectangleGeometry
            {
                CornerRadius = new CornerRadius(12),
                Rect = new Rect(0, 0, 56, 56)
            },
            Source = iconSource
        };
        var tapPreviewIcon = new TapGestureRecognizer();
        tapPreviewIcon.Tapped += (s, e) => OpenThemePreviewModal(model);
        iconImage.GestureRecognizers.Add(tapPreviewIcon);
        mainLayout.Add(iconImage, 0, 0);

        var contentStack = new VerticalStackLayout { Spacing = 5 };

        var headerLayout = new HorizontalStackLayout { Spacing = 8 };
        var titleLabel = new Label
        {
            Text = model.Name,
            FontAttributes = FontAttributes.Bold,
            FontSize = 15
        };
        titleLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");
        headerLayout.Add(titleLabel);

        var versionLabel = new Label
        {
            Text = $"v{model.DisplayVersion}",
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center
        };
        versionLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
        versionLabel.SetDynamicResource(Label.TextColorProperty, "TextMuted");
        headerLayout.Add(versionLabel);
        contentStack.Add(headerLayout);

        contentStack.Add(CreateAuthorBadgeView(model.Author));

        if (!string.IsNullOrWhiteSpace(model.Description))
        {
            var descLabel = new Label
            {
                Text = model.Description,
                FontSize = 12,
                MaxLines = 2,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            descLabel.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            contentStack.Add(descLabel);
        }

        var badgesLayout = new HorizontalStackLayout { Spacing = 6, Margin = new Thickness(0, 2, 0, 6) };
        if (model.HasVideoBackground)
        {
            badgesLayout.Add(CreateBadge("Видео", FluentIcons.Video24, Color.FromArgb("#253B82F6"), Color.FromArgb("#60A5FA")));
        }
        if (model.HasAnimatedButton)
        {
            badgesLayout.Add(CreateBadge("Анимация", FluentIcons.PlayCircle24, Color.FromArgb("#2510B981"), Color.FromArgb("#34D399")));
        }
        if (model.HasSounds)
        {
            badgesLayout.Add(CreateBadge("Звуки", FluentIcons.Speaker224, Color.FromArgb("#25A855F7"), Color.FromArgb("#C084FC")));
        }
        if (model.HasParticles)
        {
            badgesLayout.Add(CreateBadge("Частицы", FluentIcons.Sparkle24, Color.FromArgb("#25EC4899"), Color.FromArgb("#F472B6")));
        }
        if (model.HasScreenOverlay || model.HasReactiveVfx)
        {
            badgesLayout.Add(CreateBadge("Шейдеры", FluentIcons.Flash24, Color.FromArgb("#25F59E0B"), Color.FromArgb("#FBBF24")));
        }
        if (model.HasFrames)
        {
            badgesLayout.Add(CreateBadge("Рамка", FluentIcons.Image24, Color.FromArgb("#2506B6D4"), Color.FromArgb("#22D3EE")));
        }
        if (badgesLayout.Children.Count > 0)
        {
            contentStack.Add(badgesLayout);
        }

        var actionLayout = new HorizontalStackLayout { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        PopulateActionLayout(actionLayout, model);
        contentStack.Add(actionLayout);
        mainLayout.Add(contentStack, 1, 0);
        cardBorder.Content = mainLayout;

        return (cardBorder, actionLayout);
    }

    private void PopulateActionLayout(HorizontalStackLayout actionLayout, ThemeCardModel model)
    {

        if (model.IsActive)
        {
            var activeLabel = new Label
            {
                Text = "Активна",
                FontSize = 12,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center
            };
            activeLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");

            var btnActive = new Border
            {
                HeightRequest = 36,
                Padding = new Thickness(14, 0),
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                StrokeThickness = 0,
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new MauiIcon
                        {
                            Icon = FluentIcons.Checkmark24,
                            IconSize = 16,
                            IconColor = Colors.White,
                            VerticalOptions = LayoutOptions.Center
                        },
                        activeLabel
                    }
                }
            };
            btnActive.SetDynamicResource(BackgroundColorProperty, "Success");
            actionLayout.Add(btnActive);

            if (model.HasUpdate && !string.IsNullOrEmpty(model.UpdateVersion))
            {
                actionLayout.Add(CreateUpdateButton(model.Id, model.Name, model.UpdateVersion, isActive: true));
            }
        }
        else if (model.IsInstalled)
        {
            var applyLabel = new Label
            {
                Text = "Применить",
                FontSize = 12,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center
            };
            applyLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");

            var btnApply = new Border
            {
                HeightRequest = 36,
                Padding = new Thickness(14, 0),
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                StrokeThickness = 0,
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new MauiIcon
                        {
                            Icon = FluentIcons.Play24,
                            IconSize = 15,
                            IconColor = Colors.White,
                            VerticalOptions = LayoutOptions.Center
                        },
                        applyLabel
                    }
                }
            };
            btnApply.SetDynamicResource(BackgroundColorProperty, "Primary");

            var tapApply = new TapGestureRecognizer();
            tapApply.Tapped += async (s, e) =>
            {
                ThemeAudioService.PlayClick();
                await btnApply.BounceClickAsync();
                var list = _themeManager?.GetInstalledThemes() ?? [];
                var match = list.FirstOrDefault(t => t.Id == model.Id);
                if (match != null && _themeManager != null)
                {
                    _ = await _themeManager.ApplyThemeAsync(match);
                }
            };
            btnApply.GestureRecognizers.Add(tapApply);
            actionLayout.Add(btnApply);

            if (model.HasUpdate && !string.IsNullOrEmpty(model.UpdateVersion))
            {
                actionLayout.Add(CreateUpdateButton(model.Id, model.Name, model.UpdateVersion, isActive: false));
            }
        }
        else
        {
            var downloadIcon = new MauiIcon
            {
                Icon = FluentIcons.ArrowDownload24,
                IconSize = 16,
                IconColor = Colors.White,
                VerticalOptions = LayoutOptions.Center
            };

            var curProg = 0.0;
            var isDownloading = _themeManager?.IsThemeDownloading(model.Id, out curProg) == true;
            var downloadLabel = new Label
            {
                Text = isDownloading ? (curProg > 0 ? $"Загрузка {(int)Math.Round(curProg * 100)}%" : "Загрузка...") : "Скачать",
                FontSize = 12,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center
            };
            downloadLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
            _activeDownloadLabels[model.Id] = downloadLabel;

            var btnDownload = new Border
            {
                HeightRequest = 36,
                Padding = new Thickness(14, 0),
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                StrokeThickness = 0,
                IsEnabled = !isDownloading,
                Opacity = isDownloading ? 0.7 : 1.0,
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children = { downloadIcon, downloadLabel }
                }
            };
            btnDownload.SetDynamicResource(BackgroundColorProperty, "Primary");

            var tapDownload = new TapGestureRecognizer();
            tapDownload.Tapped += async (s, e) =>
            {
                ThemeAudioService.PlayClick();
                await btnDownload.BounceClickAsync();
                downloadLabel.Text = "Загрузка...";
                btnDownload.IsEnabled = false;
                btnDownload.Opacity = 0.7;
                LabelStatus.IsVisible = false;

                try
                {
                    var ok = await (_themeManager?.DownloadAndInstallThemeAsync(model.Id) ?? Task.FromResult(false));
                    if (!ok)
                    {
                        downloadLabel.Text = "Ошибка";
                        btnDownload.IsEnabled = true;
                        btnDownload.Opacity = 1.0;
                        var err = _themeManager?.LastError ?? "Неизвестная ошибка загрузки";
                        LabelStatus.Text = $"Ошибка загрузки темы: {err}";
                        LabelStatus.IsVisible = true;
                        await NeoAlert.ShowAsync("Ошибка загрузки темы", err, "OK");
                    }
                }
                catch (Exception ex)
                {
                    downloadLabel.Text = "Ошибка";
                    btnDownload.IsEnabled = true;
                    btnDownload.Opacity = 1.0;
                    LabelStatus.Text = $"Исключение: {ex.Message}";
                    LabelStatus.IsVisible = true;
                    await NeoAlert.ShowAsync("Ошибка загрузки темы", ex.Message, "OK");
                }
            };
            btnDownload.GestureRecognizers.Add(tapDownload);
            actionLayout.Add(btnDownload);
        }

        var btnPreview = new Border
        {
            HeightRequest = 36,
            WidthRequest = 36,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            BackgroundColor = Color.FromArgb("#15FFFFFF"),
            Stroke = Color.FromArgb("#25FFFFFF"),
            StrokeThickness = 1,
            VerticalOptions = LayoutOptions.Center,
            Content = new MauiIcon
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Icon = FluentIcons.Eye24,
                IconSize = 16,
                IconColor = GetAppColor("TextSecondary", Color.FromArgb("#A0A0B0"))
            }
        };
        ToolTipProperties.SetText(btnPreview, "Предпросмотр темы");
        var tapPreview = new TapGestureRecognizer();
        tapPreview.Tapped += async (s, e) =>
        {
            ThemeAudioService.PlayClick();
            await btnPreview.BounceClickAsync();
            OpenThemePreviewModal(model);
        };
        btnPreview.GestureRecognizers.Add(tapPreview);
        actionLayout.Add(btnPreview);

        if (model.IsInstalled)
        {
            var deleteLabel = new Label
            {
                Text = "Удалить",
                FontSize = 12,
                TextColor = Color.FromArgb("#EF4444"),
                VerticalOptions = LayoutOptions.Center
            };
            deleteLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");

            var btnDelete = new Border
            {
                HeightRequest = 36,
                Padding = new Thickness(12, 0),
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                BackgroundColor = Color.FromArgb("#22EF4444"),
                Stroke = Color.FromArgb("#44EF4444"),
                StrokeThickness = 1,
                VerticalOptions = LayoutOptions.Center,
                Content = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center,
                    Children =
                    {
                        new MauiIcon
                        {
                            Icon = FluentIcons.Delete24,
                            IconSize = 16,
                            IconColor = Color.FromArgb("#EF4444"),
                            VerticalOptions = LayoutOptions.Center
                        },
                        deleteLabel
                    }
                }
            };
            ToolTipProperties.SetText(btnDelete, "Удалить тему");
            var tapDelete = new TapGestureRecognizer();
            tapDelete.Tapped += async (s, e) =>
            {
                ThemeAudioService.PlayClick();
                await btnDelete.BounceClickAsync();
                var confirm = await NeoAlert.ShowConfirmAsync(
                    "Удаление темы",
                    $"Вы уверены, что хотите удалить тему '{model.Name}' с устройства?",
                    "Удалить",
                    "Отмена");

                if (confirm && _themeManager != null)
                {
                    if (!_themeManager.DeleteTheme(model.Id))
                    {
                        await NeoAlert.ShowAsync("Ошибка", "Не удалось удалить тему.", "OK");
                    }
                    UpdateTabButtons();
                    RenderThemes(animate: false);
                }
            };
            btnDelete.GestureRecognizers.Add(tapDelete);
            actionLayout.Add(btnDelete);
        }

        if (model.IsCommunity)
        {
            var btnReport = new Border
            {
                HeightRequest = 36,
                WidthRequest = 36,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                BackgroundColor = Color.FromArgb("#15FFFFFF"),
                Stroke = Color.FromArgb("#25FFFFFF"),
                StrokeThickness = 1,
                VerticalOptions = LayoutOptions.Center,
                Content = new MauiIcon
                {
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Icon = FluentIcons.Flag24,
                    IconSize = 16,
                    IconColor = GetAppColor("TextMuted", Color.FromArgb("#A0A0B0"))
                }
            };
            ToolTipProperties.SetText(btnReport, "Пожаловаться на тему");
            var tapReport = new TapGestureRecognizer();
            tapReport.Tapped += async (s, e) =>
            {
                ThemeAudioService.PlayClick();
                await btnReport.BounceClickAsync();
                await NeoAlert.ShowAsync(
                    "Жалоба на тему",
                    $"Жалоба на тему '{model.Name}' отправлена на модерацию. Спасибо за помощь в поддержании чистоты и безопасности сообщества!",
                    "ОК");
            };
            btnReport.GestureRecognizers.Add(tapReport);
            actionLayout.Add(btnReport);
        }

    }

    private static View CreateAuthorBadgeView(string author)
    {
        var isOfficial = string.Equals(author, "OctoCore", StringComparison.OrdinalIgnoreCase);
        if (!isOfficial)
        {
            var lbl = new Label
            {
                Text = $"Автор: {author}",
                FontSize = 12
            };
            lbl.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
            return lbl;
        }

        var layout = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center
        };

        var authorLbl = new Label
        {
            Text = $"Автор: {author}",
            FontSize = 12,
            VerticalOptions = LayoutOptions.Center
        };
        authorLbl.SetDynamicResource(Label.TextColorProperty, "TextSecondary");
        layout.Add(authorLbl);

        var officialLabel = new Label
        {
            Text = "Официальная",
            FontSize = 10,
            TextColor = Color.FromArgb("#93C5FD"),
            VerticalOptions = LayoutOptions.Center
        };
        officialLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");

        var verifiedBadge = new Border
        {
            Padding = new Thickness(6, 2),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
            BackgroundColor = Color.FromArgb("#253B82F6"),
            Stroke = Color.FromArgb("#603B82F6"),
            StrokeThickness = 1,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 4,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new MauiIcon
                    {
                        Icon = FluentIcons.CheckmarkCircle24,
                        IconSize = 13,
                        IconColor = Color.FromArgb("#60A5FA"),
                        VerticalOptions = LayoutOptions.Center
                    },
                    officialLabel
                }
            }
        };

        ToolTipProperties.SetText(verifiedBadge, "Официальная проверенная тема команды разработчиков OctoCore");
        layout.Add(verifiedBadge);

        return layout;
    }

    private static Border CreateBadge(string text, FluentIcons icon, Color bgColor, Color fgColor)
    {
        var badgeLabel = new Label
        {
            Text = text,
            FontSize = 10.5,
            TextColor = fgColor,
            VerticalOptions = LayoutOptions.Center
        };
        badgeLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");

        return new Border
        {
            Padding = new Thickness(8, 3),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(6) },
            StrokeThickness = 0,
            BackgroundColor = bgColor,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 5,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new MauiIcon
                    {
                        Icon = icon,
                        IconSize = 13,
                        IconColor = fgColor,
                        VerticalOptions = LayoutOptions.Center
                    },
                    badgeLabel
                }
            }
        };
    }

    private Border CreateUpdateButton(string themeId, string themeName, string targetVersion, bool isActive)
    {
        var updateIcon = new MauiIcon
        {
            Icon = FluentIcons.ArrowDownload24,
            IconSize = 16,
            IconColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };

        var curProg = 0.0;
        var isDownloading = _themeManager?.IsThemeDownloading(themeId, out curProg) == true;
        var updateLabel = new Label
        {
            Text = isDownloading ? (curProg > 0 ? $"Обновление {(int)Math.Round(curProg * 100)}%" : "Обновление...") : $"Обновить (v{targetVersion})",
            FontSize = 12,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };
        updateLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
        _activeDownloadLabels[themeId] = updateLabel;

        var btnUpdate = new Border
        {
            HeightRequest = 36,
            Padding = new Thickness(14, 0),
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
            BackgroundColor = Color.FromArgb("#D97706"),
            StrokeThickness = 0,
            IsEnabled = !isDownloading,
            Opacity = isDownloading ? 0.7 : 1.0,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center,
                Children = { updateIcon, updateLabel }
            }
        };

        ToolTipProperties.SetText(btnUpdate, $"Доступно обновление до версии v{targetVersion}");

        var tapUpdate = new TapGestureRecognizer();
        tapUpdate.Tapped += async (s, e) =>
        {
            ThemeAudioService.PlayClick();
            await btnUpdate.BounceClickAsync();
            updateLabel.Text = "Обновление...";
            btnUpdate.IsEnabled = false;
            btnUpdate.Opacity = 0.7;
            LabelStatus.IsVisible = false;

            try
            {
                var ok = await (_themeManager?.DownloadAndInstallThemeAsync(themeId) ?? Task.FromResult(false));
                if (ok)
                {
                    var msg = isActive || _themeManager?.ActiveTheme?.Id == themeId
                        ? $"Тема '{themeName}' успешно обновлена до версии v{targetVersion} и применена!"
                        : $"Тема '{themeName}' успешно обновлена до версии v{targetVersion}!";
                    await NeoAlert.ShowAsync("Обновление темы", msg, "OK");
                }
                else
                {
                    updateLabel.Text = "Ошибка";
                    btnUpdate.IsEnabled = true;
                    btnUpdate.Opacity = 1.0;
                    var err = _themeManager?.LastError ?? "Неизвестная ошибка при обновлении";
                    LabelStatus.Text = $"Ошибка обновления темы: {err}";
                    LabelStatus.IsVisible = true;
                    await NeoAlert.ShowAsync("Ошибка обновления темы", err, "OK");
                }
            }
            catch (Exception ex)
            {
                updateLabel.Text = "Ошибка";
                btnUpdate.IsEnabled = true;
                btnUpdate.Opacity = 1.0;
                LabelStatus.Text = $"Исключение: {ex.Message}";
                LabelStatus.IsVisible = true;
                await NeoAlert.ShowAsync("Ошибка обновления темы", ex.Message, "OK");
            }
        };

        btnUpdate.GestureRecognizers.Add(tapUpdate);
        return btnUpdate;
    }

    private void OpenThemePreviewModal(ThemeCardModel model)
    {
        _previewModalModel = model;
        PreviewModalTitleLabel.Text = model.Name;
        PreviewModalVersionLabel.Text = $"v{model.DisplayVersion}";
        PreviewModalAuthorLabel.Text = $"Автор: {model.Author}";
        PreviewModalDescLabel.Text = !string.IsNullOrWhiteSpace(model.Description)
            ? model.Description
            : "Кастомная тема оформления Obxodka.";

        PreviewModalBannerImage.Source = ResolvePreviewBannerSource(model);

        PreviewModalSwatchesLayout.Children.Clear();
        if (model.Colors is { } colors)
        {
            string?[] hexes = [colors.Primary, colors.Accent, colors.BgBase, colors.BgSurface, colors.Success];
            foreach (var hex in hexes)
            {
                if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex, out var swatchColor))
                {
                    PreviewModalSwatchesLayout.Children.Add(new Border
                    {
                        WidthRequest = 24,
                        HeightRequest = 24,
                        StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(12) },
                        BackgroundColor = swatchColor,
                        Stroke = GetAppColor("BorderMedium", Colors.Gray),
                        StrokeThickness = 1
                    });
                }
            }
        }
        PreviewModalSwatchesLayout.IsVisible = PreviewModalSwatchesLayout.Children.Count > 0;

        PreviewModalBadgesLayout.Children.Clear();
        if (model.HasVideoBackground)
        {
            PreviewModalBadgesLayout.Children.Add(CreateBadge("Видео-фон", FluentIcons.Video24, Color.FromArgb("#253B82F6"), Color.FromArgb("#60A5FA")));
        }
        if (model.HasAnimatedButton)
        {
            PreviewModalBadgesLayout.Children.Add(CreateBadge("Анимированная кнопка", FluentIcons.PlayCircle24, Color.FromArgb("#2510B981"), Color.FromArgb("#34D399")));
        }
        if (model.HasSounds)
        {
            PreviewModalBadgesLayout.Children.Add(CreateBadge("Звуковой пакет", FluentIcons.Speaker224, Color.FromArgb("#25A855F7"), Color.FromArgb("#C084FC")));
        }
        if (model.HasParticles)
        {
            PreviewModalBadgesLayout.Children.Add(CreateBadge("Частицы", FluentIcons.Sparkle24, Color.FromArgb("#25EC4899"), Color.FromArgb("#F472B6")));
        }
        if (model.HasScreenOverlay || model.HasReactiveVfx)
        {
            PreviewModalBadgesLayout.Children.Add(CreateBadge("Экранные шейдеры", FluentIcons.Flash24, Color.FromArgb("#25F59E0B"), Color.FromArgb("#FBBF24")));
        }

        LabelPreviewModalAction.Text = model.IsActive
            ? "Тема уже активна"
            : model.IsInstalled
                ? "Применить тему"
                : "Скачать и применить";
        BtnPreviewModalAction.Opacity = model.IsActive ? 0.6 : 1.0;

        ThemePreviewModalOverlay.IsVisible = true;
        ThemePreviewModalOverlay.InputTransparent = false;
        ThemePreviewModalCard.Scale = 0.92;
        ThemePreviewModalCard.Opacity = 0;

        _ = ThemePreviewModalOverlay.FadeToAsync(1.0, 180, Easing.CubicOut);
        _ = ThemePreviewModalCard.FadeToAsync(1.0, 180, Easing.CubicOut);
        _ = ThemePreviewModalCard.ScaleToAsync(1.0, 220, Easing.SpringOut);
    }

    private void OnClosePreviewModalClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = ClosePreviewModalAsync();
    }

    private static ImageSource ResolvePreviewBannerSource(ThemeCardModel model) =>
        model switch
        {
            { LocalPreviewPath: { Length: > 0 } previewPath } when File.Exists(previewPath) => LoadLocalImage(previewPath),
            { RemotePreviewUrl: { Length: > 0 } remotePreview } => new UriImageSource
            {
                Uri = new Uri(ThemeManager.ToMirrorUrl(remotePreview)),
                CachingEnabled = true,
                CacheValidity = TimeSpan.FromDays(14)
            },
            { LocalIconPath: { Length: > 0 } iconPath } when File.Exists(iconPath) => LoadLocalImage(iconPath),
            { RemoteIconUrl: { Length: > 0 } remoteIcon } => new UriImageSource
            {
                Uri = new Uri(ThemeManager.ToMirrorUrl(remoteIcon)),
                CachingEnabled = true,
                CacheValidity = TimeSpan.FromDays(14)
            },
            _ => "splash_logo.png"
        };

    private async Task ClosePreviewModalAsync()
    {
        ThemePreviewModalOverlay.InputTransparent = true;
        _ = ThemePreviewModalCard.ScaleToAsync(0.92, 150, Easing.CubicIn);
        _ = ThemePreviewModalCard.FadeToAsync(0, 150, Easing.CubicIn);
        _ = await ThemePreviewModalOverlay.FadeToAsync(0, 150, Easing.CubicIn);
        ThemePreviewModalOverlay.IsVisible = false;
    }

    private async void OnPreviewModalActionClickedAsync(object? sender, EventArgs e)
    {
        if (_previewModalModel is not { } model || _themeManager is null)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        if (model.IsActive)
        {
            await ClosePreviewModalAsync();
            return;
        }

        if (model.IsInstalled)
        {
            _ = await _themeManager.ApplyThemeByIdAsync(model.Id);
            await ClosePreviewModalAsync();
            return;
        }

        LabelPreviewModalAction.Text = "Загрузка...";
        var ok = await _themeManager.DownloadAndInstallThemeAsync(model.Id);
        if (ok)
        {
            _ = await _themeManager.ApplyThemeByIdAsync(model.Id);
            await ClosePreviewModalAsync();
        }
        else
        {
            LabelPreviewModalAction.Text = "Ошибка загрузки";
            var err = _themeManager.LastError ?? "Не удалось скачать тему.";
            await NeoAlert.ShowAsync("Ошибка загрузки темы", err, "OK");
        }
    }

    private void OnFilterAllClicked(object? sender, EventArgs e) => SetActiveFilter("all");
    private void OnFilterVideoClicked(object? sender, EventArgs e) => SetActiveFilter("video");
    private void OnFilterAnimClicked(object? sender, EventArgs e) => SetActiveFilter("anim");
    private void OnFilterSoundClicked(object? sender, EventArgs e) => SetActiveFilter("sound");
    private void OnFilterShadersClicked(object? sender, EventArgs e) => SetActiveFilter("shaders");

    private void SetActiveFilter(string filter)
    {
        if (_activeFilter == filter)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _activeFilter = filter;
        UpdateFilterChipsUI();
        RenderThemes(animate: false);
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchQuery = e.NewTextValue ?? string.Empty;
        BtnClearSearch.IsVisible = !string.IsNullOrEmpty(_searchQuery);
        RenderThemes();
    }

    private void OnClearSearchClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        SearchEntry.Text = string.Empty;
        _searchQuery = string.Empty;
        BtnClearSearch.IsVisible = false;
        RenderThemes();
    }

    private async Task SwitchTabAnimatedAsync(string newTab)
    {
        if (_currentTab == newTab)
        {
            return;
        }

        var movingRight = newTab == "installed";
        var slideOutX = movingRight ? -36.0 : 36.0;
        var slideInX = movingRight ? 36.0 : -36.0;

        _currentTab = newTab;
        UpdateTabButtons();

        try
        {
            _ = await Task.WhenAll(
                ThemesContainer.FadeToAsync(0.0, 100, Easing.CubicIn),
                ThemesContainer.TranslateToAsync(slideOutX, 0, 100, Easing.CubicIn)
            );
        }
        catch
        {
        }

        RenderThemes(animate: false);
        ThemesContainer.TranslationX = slideInX;

        try
        {
            _ = await Task.WhenAll(
                ThemesContainer.FadeToAsync(1.0, 150, Easing.CubicOut),
                ThemesContainer.TranslateToAsync(0, 0, 150, Easing.CubicOut)
            );
        }
        catch
        {
            ThemesContainer.Opacity = 1.0;
            ThemesContainer.TranslationX = 0;
        }
    }

    private void OnTabCommunityClicked(object? sender, EventArgs e)
    {
        if (_currentTab == "community")
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = BtnTabCommunity.BounceClickAsync();
        _ = SwitchTabAnimatedAsync("community");
    }

    private void OnTabInstalledClicked(object? sender, EventArgs e)
    {
        if (_currentTab == "installed")
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _ = BtnTabInstalled.BounceClickAsync();
        _ = SwitchTabAnimatedAsync("installed");
    }

    private async void OnSyncClickedAsync(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        await BtnSync.BounceClickAsync();
        await LoadCatalogAsync(forceRefresh: true);
    }

    private void OnSoundsToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.SoundsEnabled != e.Value)
        {
            _themeManager.SoundsEnabled = e.Value;
        }
    }

    private void OnSoundVolumeChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        var rounded = Math.Round(e.NewValue, 2);
        LabelSoundVolumeValue.Text = $"{(int)Math.Round(rounded * 100)}%";

        if (_themeManager != null && Math.Abs(_themeManager.SoundVolume - rounded) >= 0.01)
        {
            _isUpdatingSliderInternally = true;
            try
            {
                _themeManager.SoundVolume = rounded;
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        }
    }

    private void OnAmbientSoundToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.AmbientEnabled != e.Value)
        {
            _themeManager.AmbientEnabled = e.Value;
        }
    }

    private void OnParticlesToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.ParticlesEnabled != e.Value)
        {
            _themeManager.ParticlesEnabled = e.Value;
        }
    }

    private void OnFramesToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.FramesEnabled != e.Value)
        {
            _themeManager.FramesEnabled = e.Value;
        }
    }

    private void OnClickSoundsToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.ClickSoundsEnabled != e.Value)
        {
            _themeManager.ClickSoundsEnabled = e.Value;
        }
    }

    private void OnVideoBackgroundToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.VideoEnabled != e.Value)
        {
            _themeManager.VideoEnabled = e.Value;
        }
    }

    private void OnAlwaysPlayVideoToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.AlwaysPlayVideoEnabled != e.Value)
        {
            _themeManager.AlwaysPlayVideoEnabled = e.Value;
        }
    }

    private void OnParallaxToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.ParallaxEnabled != e.Value)
        {
            _themeManager.ParallaxEnabled = e.Value;
        }
    }

    private void OnCardOpacityChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        var rounded = Math.Round(e.NewValue, 2);
        LabelCardOpacityValue.Text = $"{(int)Math.Round(rounded * 100)}%";

        if (_themeManager != null && Math.Abs(_themeManager.CardOpacity - rounded) >= 0.01)
        {
            _isUpdatingSliderInternally = true;
            try
            {
                _themeManager.CardOpacity = rounded;
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        }
    }

    private void OnGlowIntensityChanged(object? sender, ValueChangedEventArgs e)
    {
        if (_isUpdatingSliderInternally)
        {
            return;
        }

        var rounded = Math.Round(e.NewValue, 2);
        LabelGlowIntensityValue.Text = $"{(int)Math.Round(rounded * 100)}%";

        if (_themeManager != null && Math.Abs(_themeManager.GlowIntensity - rounded) >= 0.01)
        {
            _isUpdatingSliderInternally = true;
            try
            {
                _themeManager.GlowIntensity = rounded;
            }
            finally
            {
                _isUpdatingSliderInternally = false;
            }
        }
    }

    private void OnResetThemeClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BtnResetTheme.BounceClickAsync();
        if (_themeManager != null)
        {
            _ = _themeManager.ResetToDefaultAsync();
        }
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = BtnBack.BounceClickAsync();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OnThemeChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshStaticThemeColors();
            UpdateTabButtons();
            UpdateFilterChipsUI();
            RenderThemes(animate: false);
        });
    }

    public void UpdateCardOpacity()
    {
        var cardBgColor = GetAppColor("BgSurface", Color.FromArgb("#161622"));

        BtnBack.BackgroundColor = cardBgColor;
        BtnResetTheme.BackgroundColor = cardBgColor;
        CardSearch.BackgroundColor = cardBgColor;
        BtnSync.BackgroundColor = cardBgColor;
        CardVfxSettings.BackgroundColor = cardBgColor;
        UpdateFilterChipsUI();

        foreach (var child in ThemesContainer.Children)
        {
            if (child is Border cardBorder)
            {
                cardBorder.BackgroundColor = cardBgColor;
            }
        }
    }

    private void RefreshStaticThemeColors()
    {
        var primaryColor = GetAppColor("Primary", Color.FromArgb("#7C3AED"));
        var primaryDim = GetAppColor("PrimaryDim", primaryColor.WithAlpha(0.22f));
        var accentColor = GetAppColor("Accent", Color.FromArgb("#00E5FF"));
        var textPrimaryColor = GetAppColor("TextPrimary", Colors.White);
        var textMutedColor = GetAppColor("TextMuted", Color.FromArgb("#A0A0B0"));
        var borderSubtleColor = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));

        UpdateCardOpacity();

        BtnBack.Stroke = borderSubtleColor;
        BtnResetTheme.Stroke = borderSubtleColor;

        HeaderIconBadge.BackgroundColor = primaryDim;
        HeaderIcon.IconColor = primaryColor;

        CardSearch.Stroke = borderSubtleColor;
        SearchEntry.TextColor = textPrimaryColor;
        SearchEntry.PlaceholderColor = textMutedColor;

        BtnSync.Stroke = borderSubtleColor;
        IconSync.IconColor = textPrimaryColor;

        CardVfxSettings.Stroke = borderSubtleColor;

        BadgeSounds.BackgroundColor = primaryDim;
        IconSounds.IconColor = primaryColor;
        BadgeParticles.BackgroundColor = primaryDim;
        IconParticles.IconColor = accentColor;
        BadgeFrames.BackgroundColor = primaryDim;
        IconFrames.IconColor = primaryColor;
        BadgeVideo.BackgroundColor = primaryDim;
        IconVideo.IconColor = primaryColor;
        BadgeAlwaysPlay.BackgroundColor = primaryDim;
        IconAlwaysPlay.IconColor = accentColor;
        BadgeOpacity.BackgroundColor = primaryDim;
        IconOpacity.IconColor = primaryColor;
        BadgeGlow.BackgroundColor = primaryDim;
        IconGlow.IconColor = accentColor;

        SepVfx1.Color = borderSubtleColor;
        SepVfxFrames.Color = borderSubtleColor;
        SepVfx2.Color = borderSubtleColor;
        SepVfx3.Color = borderSubtleColor;
        SepVfx4.Color = borderSubtleColor;
        SepVfx5.Color = borderSubtleColor;

        SliderSoundVolume.MinimumTrackColor = primaryColor;
        SliderSoundVolume.ThumbColor = primaryColor;
        SliderSoundVolume.MaximumTrackColor = borderSubtleColor;
        LabelSoundVolumeValue.TextColor = primaryColor;

        SliderCardOpacity.MinimumTrackColor = primaryColor;
        SliderCardOpacity.ThumbColor = primaryColor;
        SliderCardOpacity.MaximumTrackColor = borderSubtleColor;
        LabelCardOpacityValue.TextColor = primaryColor;

        SliderGlowIntensity.MinimumTrackColor = accentColor;
        SliderGlowIntensity.ThumbColor = accentColor;
        SliderGlowIntensity.MaximumTrackColor = borderSubtleColor;
        LabelGlowIntensityValue.TextColor = accentColor;

        SwitchSounds.OnColor = primaryColor;
        SwitchClickSounds.OnColor = primaryColor;
        SwitchAmbientSound.OnColor = primaryColor;
        SwitchParticles.OnColor = primaryColor;
        SwitchFrames.OnColor = primaryColor;
        SwitchVideoBackground.OnColor = primaryColor;
        SwitchAlwaysPlayVideo.OnColor = primaryColor;
    }

    private static Color GetAppColor(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.Resources != null &&
                Application.Current.Resources.TryGetValue(key, out var val) &&
                val is Color color)
            {
                return color;
            }
        }
        catch
        {
        }
        return fallback;
    }

    private static ImageSource LoadLocalImage(string? path, string fallback = "splash_logo.png")
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return (ImageSource)fallback;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            return ImageSource.FromStream(() => new MemoryStream(bytes));
        }
        catch
        {
            return (ImageSource)fallback;
        }
    }
}
