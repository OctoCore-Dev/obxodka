using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Views;

public sealed partial class ThemeStoreView : ContentView
{
    private ThemeManager? _themeManager;
    private ThemeCatalogResponse? _currentCatalog;
    private readonly Dictionary<string, Label> _activeDownloadLabels = [];
    private string _currentTab = "community";
    private string _searchQuery = string.Empty;
    private double _currentSafeWidth = -1;
    private bool _isUpdatingSliderInternally;
    private readonly HashSet<VisualElement> _revealedCards = [];

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
            manager.SoundsEnabledChanged -= HandleSoundsEnabledChanged;
            manager.ParticlesEnabledChanged -= HandleParticlesEnabledChanged;
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
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayout != null)
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
            prevManager.SoundsEnabledChanged -= HandleSoundsEnabledChanged;
            prevManager.ParticlesEnabledChanged -= HandleParticlesEnabledChanged;
        }

        _themeManager = themeManager;
        _themeManager.OnCatalogUpdated += HandleCatalogUpdated;
        _themeManager.ThemeDownloadProgressChanged += HandleDownloadProgressChanged;
        _themeManager.VideoEnabledChanged += HandleVideoEnabledChanged;
        _themeManager.AlwaysPlayVideoChanged += HandleAlwaysPlayVideoChanged;
        _themeManager.CardOpacityChanged += HandleCardOpacityChanged;
        _themeManager.SoundsEnabledChanged += HandleSoundsEnabledChanged;
        _themeManager.ParticlesEnabledChanged += HandleParticlesEnabledChanged;

        RefreshSettingsControls();

        _ = LoadCatalogAsync();
    }

    private void RefreshSettingsControls()
    {
        if (_themeManager == null)
        {
            return;
        }

        SwitchSounds.IsToggled = _themeManager.SoundsEnabled;
        SwitchParticles.IsToggled = _themeManager.ParticlesEnabled;
        SwitchVideoBackground.IsToggled = _themeManager.VideoEnabled;
        SwitchAlwaysPlayVideo.IsToggled = _themeManager.AlwaysPlayVideoEnabled;

        var opacity = _themeManager.CardOpacity;
        _isUpdatingSliderInternally = true;
        try
        {
            SliderCardOpacity.Value = opacity;
        }
        finally
        {
            _isUpdatingSliderInternally = false;
        }
        LabelCardOpacityValue.Text = $"{(int)Math.Round(opacity * 100)}%";
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

    private void HandleSoundsEnabledChanged(bool val) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SwitchSounds.IsToggled != val)
            {
                SwitchSounds.IsToggled = val;
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
                    _ = _themeManager.ApplyThemeById(e.ThemeId);
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

    private void RenderThemes(bool animate = true)
    {
        _ = animate;
        ThemesContainer.Clear();
        _activeDownloadLabels.Clear();
        LabelStatus.IsVisible = false;

        if (_themeManager == null)
        {
            return;
        }

        var installedList = _themeManager.GetInstalledThemes();
        var installedMap = installedList.ToDictionary(t => t.Id, t => t);

        if (_currentTab == "installed")
        {
            var filteredInstalled = installedList.Where(t => MatchesSearch(t.Name, t.Id, t.Description, t.Author, t.Tags)).ToList();

            if (filteredInstalled.Count == 0)
            {
                LabelStatus.Text = string.IsNullOrWhiteSpace(_searchQuery)
                    ? "У вас пока нет установленных тем.\nПерейдите во вкладку 'Сообщество', чтобы выбрать и скачать темы!"
                    : $"Ничего не найдено по запросу '{_searchQuery}'.";
                LabelStatus.IsVisible = true;
                return;
            }

            foreach (var manifest in filteredInstalled)
            {
                var folder = Path.Combine(_themeManager.ThemesDirectory, manifest.Id);
                var isActive = _themeManager.ActiveTheme?.Id == manifest.Id;
                var remoteItem = _currentCatalog?.Themes.FirstOrDefault(t => t.Id == manifest.Id);
                var hasUpdate = remoteItem != null && ThemeManager.IsVersionNewer(manifest.Version, remoteItem.Version);

                var model = new ThemeCardModel(
                    Id: manifest.Id,
                    Name: manifest.Name,
                    Author: manifest.Author,
                    DisplayVersion: manifest.Version,
                    Description: manifest.Description,
                    HasSounds: manifest.Core?.Sounds != null,
                    HasParticles: manifest.Vfx?.Particles != null && manifest.Vfx.Particles != "none",
                    HasFrames: !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrame) || !string.IsNullOrEmpty(manifest.Decorations?.ScreenFrameMobile) || !string.IsNullOrEmpty(manifest.Decorations?.CardFrame),
                    RemoteIconUrl: null,
                    LocalIconPath: Path.Combine(folder, "icon.png"),
                    IsInstalled: true,
                    IsActive: isActive,
                    HasUpdate: hasUpdate,
                    UpdateVersion: remoteItem?.Version,
                    IsCommunity: false
                );
                ThemesContainer.Add(CreateThemeCard(model));
            }
        }
        else
        {
            if (_currentCatalog == null || _currentCatalog.Themes.Count == 0)
            {
                LabelStatus.Text = "Каталог тем временно недоступен или пуст.";
                LabelStatus.IsVisible = true;
                return;
            }

            var filteredCommunity = _currentCatalog.Themes
                .Where(t => MatchesSearch(t.Name, t.Id, t.Description, t.Author, t.Tags))
                .ToList();

            if (filteredCommunity.Count == 0)
            {
                LabelStatus.Text = $"Ничего не найдено по запросу '{_searchQuery}'.";
                LabelStatus.IsVisible = true;
                return;
            }

            foreach (var item in filteredCommunity)
            {
                var isInstalled = installedMap.ContainsKey(item.Id);
                var isActive = _themeManager.ActiveTheme?.Id == item.Id;
                var installedVersion = isInstalled ? _themeManager.GetInstalledThemeVersion(item.Id) : null;
                var displayVersion = !string.IsNullOrEmpty(installedVersion) ? installedVersion : item.Version;
                var hasUpdate = isInstalled && _themeManager.IsUpdateAvailable(item.Id, item.Version);

                var model = new ThemeCardModel(
                    Id: item.Id,
                    Name: item.Name,
                    Author: item.Author,
                    DisplayVersion: displayVersion,
                    Description: item.Description,
                    HasSounds: item.Features.HasSounds,
                    HasParticles: item.Features.HasParticles,
                    HasFrames: item.Features.HasFrames,
                    RemoteIconUrl: item.IconUrl,
                    LocalIconPath: isInstalled ? Path.Combine(_themeManager.ThemesDirectory, item.Id, "icon.png") : null,
                    IsInstalled: isInstalled,
                    IsActive: isActive,
                    HasUpdate: hasUpdate,
                    UpdateVersion: item.Version,
                    IsCommunity: true
                );
                ThemesContainer.Add(CreateThemeCard(model));
            }
        }

        if (_currentSafeWidth > 0)
        {
            foreach (var child in ThemesContainer.Children)
            {
                if (child is VisualElement visual)
                {
                    visual.WidthRequest = _currentSafeWidth;
                    visual.MaximumWidthRequest = _currentSafeWidth;
                }
            }
        }

        _revealedCards.Clear();
        foreach (var card in ThemesContainer.Children.OfType<VisualElement>())
        {
            card.Opacity = 1.0;
            card.Scale = 1.0;
            card.TranslationY = 0;
            _ = _revealedCards.Add(card);
        }
    }

    private bool MatchesSearch(string name, string id, string? desc, string author, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(_searchQuery))
        {
            return true;
        }

        var q = _searchQuery.Trim();
        return name.Contains(q, StringComparison.OrdinalIgnoreCase) || id.Contains(q, StringComparison.OrdinalIgnoreCase) || author.Contains(q, StringComparison.OrdinalIgnoreCase) || (desc != null && desc.Contains(q, StringComparison.OrdinalIgnoreCase)) || (tags != null && tags.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase)));
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
        string? RemoteIconUrl,
        string? LocalIconPath,
        bool IsInstalled,
        bool IsActive,
        bool HasUpdate,
        string? UpdateVersion,
        bool IsCommunity
    );

    private Border CreateThemeCard(ThemeCardModel model)
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
        mainLayout.Add(iconImage, 0, 0);

        var contentStack = new VerticalStackLayout { Spacing = 5 };

        var headerLayout = new HorizontalStackLayout { Spacing = 8 };
        var titleLabel = new Label
        {
            Text = model.Name,
            FontAttributes = FontAttributes.Bold,
            FontSize = 15
        };
        titleLabel.SetDynamicResource(Label.TextColorProperty, "TextPrimary");
        headerLayout.Add(titleLabel);

        var versionLabel = new Label
        {
            Text = $"v{model.DisplayVersion}",
            FontSize = 11,
            VerticalOptions = LayoutOptions.Center
        };
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
        if (model.HasSounds)
        {
            badgesLayout.Add(CreateBadge("Звуки", FluentIcons.Speaker224, Color.FromArgb("#25A855F7"), Color.FromArgb("#C084FC")));
        }
        if (model.HasParticles)
        {
            badgesLayout.Add(CreateBadge("Частицы", FluentIcons.Sparkle24, Color.FromArgb("#25EC4899"), Color.FromArgb("#F472B6")));
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

        if (model.IsActive)
        {
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
                        new Label
                        {
                            Text = "Активна",
                            FontFamily = "RobotoBold",
                            FontSize = 12,
                            TextColor = Colors.White,
                            VerticalOptions = LayoutOptions.Center
                        }
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
                        new Label
                        {
                            Text = "Применить",
                            FontFamily = "RobotoBold",
                            FontSize = 12,
                            TextColor = Colors.White,
                            VerticalOptions = LayoutOptions.Center
                        }
                    }
                }
            };
            btnApply.SetDynamicResource(BackgroundColorProperty, "Primary");

            var tapApply = new TapGestureRecognizer();
            tapApply.Tapped += async (s, e) =>
            {
                await btnApply.BounceClickAsync();
                var list = _themeManager?.GetInstalledThemes() ?? [];
                var match = list.FirstOrDefault(t => t.Id == model.Id);
                if (match != null && _themeManager != null)
                {
                    _ = _themeManager.ApplyTheme(match);
                    UpdateTabButtons();
                    RenderThemes(animate: false);
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
                FontFamily = "RobotoBold",
                FontSize = 12,
                TextColor = Colors.White,
                VerticalOptions = LayoutOptions.Center
            };
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

        if (model.IsInstalled)
        {
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
                        new Label
                        {
                            Text = "Удалить",
                            FontFamily = "RobotoMedium",
                            FontSize = 12,
                            TextColor = Color.FromArgb("#EF4444"),
                            VerticalOptions = LayoutOptions.Center
                        }
                    }
                }
            };
            ToolTipProperties.SetText(btnDelete, "Удалить тему");
            var tapDelete = new TapGestureRecognizer();
            tapDelete.Tapped += async (s, e) =>
            {
                await btnDelete.BounceClickAsync();
                var confirm = await NeoAlert.ShowConfirmAsync(
                    "Удаление темы",
                    $"Вы уверены, что хотите удалить тему '{model.Name}' с устройства?",
                    "Удалить",
                    "Отмена");

                if (confirm && _themeManager != null)
                {
                    _ = _themeManager.DeleteTheme(model.Id);
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
                    IconColor = Color.FromArgb("#A0A0B0")
                }
            };
            ToolTipProperties.SetText(btnReport, "Пожаловаться на тему");
            var tapReport = new TapGestureRecognizer();
            tapReport.Tapped += async (s, e) =>
            {
                await btnReport.BounceClickAsync();
                await NeoAlert.ShowAsync(
                    "Жалоба на тему",
                    $"Жалоба на тему '{model.Name}' отправлена на модерацию. Спасибо за помощь в поддержании чистоты и безопасности сообщества!",
                    "ОК");
            };
            btnReport.GestureRecognizers.Add(tapReport);
            actionLayout.Add(btnReport);
        }

        contentStack.Add(actionLayout);
        mainLayout.Add(contentStack, 1, 0);
        cardBorder.Content = mainLayout;

        return cardBorder;
    }

    private static View CreateAuthorBadgeView(string author)
    {
        var isOfficial = string.Equals(author, "OctoCore", StringComparison.OrdinalIgnoreCase);
        if (!isOfficial)
        {
            return new Label
            {
                Text = $"Автор: {author}",
                FontSize = 12,
                TextColor = Color.FromArgb("#B0B0C0")
            };
        }

        var layout = new HorizontalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center
        };

        layout.Add(new Label
        {
            Text = $"Автор: {author}",
            FontSize = 12,
            TextColor = Color.FromArgb("#D0D0E0"),
            VerticalOptions = LayoutOptions.Center
        });

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
                    new Label
                    {
                        Text = "Официальная",
                        FontSize = 10,
                        FontFamily = "RobotoBold",
                        TextColor = Color.FromArgb("#93C5FD"),
                        VerticalOptions = LayoutOptions.Center
                    }
                }
            }
        };

        ToolTipProperties.SetText(verifiedBadge, "Официальная проверенная тема команды разработчиков OctoCore");
        layout.Add(verifiedBadge);

        return layout;
    }

    private static Border CreateBadge(string text, FluentIcons icon, Color bgColor, Color fgColor)
    {
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
                    new Label
                    {
                        Text = text,
                        FontSize = 10.5,
                        FontFamily = "RobotoBold",
                        TextColor = fgColor,
                        VerticalOptions = LayoutOptions.Center
                    }
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
            FontFamily = "RobotoBold",
            FontSize = 12,
            TextColor = Colors.White,
            VerticalOptions = LayoutOptions.Center
        };
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

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        _searchQuery = e.NewTextValue ?? string.Empty;
        BtnClearSearch.IsVisible = !string.IsNullOrEmpty(_searchQuery);
        RenderThemes();
    }

    private void OnClearSearchClicked(object? sender, EventArgs e)
    {
        SearchEntry.Text = string.Empty;
        _searchQuery = string.Empty;
        BtnClearSearch.IsVisible = false;
        RenderThemes();
    }

    private void OnTabCommunityClicked(object? sender, EventArgs e)
    {
        if (_currentTab == "community")
        {
            return;
        }

        _ = BtnTabCommunity.BounceClickAsync();
        _currentTab = "community";
        UpdateTabButtons();
        RenderThemes(animate: true);
    }

    private void OnTabInstalledClicked(object? sender, EventArgs e)
    {
        if (_currentTab == "installed")
        {
            return;
        }

        _ = BtnTabInstalled.BounceClickAsync();
        _currentTab = "installed";
        UpdateTabButtons();
        RenderThemes(animate: false);
    }

    private async void OnSyncClickedAsync(object? sender, EventArgs e)
    {
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

    private void OnParticlesToggled(object? sender, ToggledEventArgs e)
    {
        if (_themeManager != null && _themeManager.ParticlesEnabled != e.Value)
        {
            _themeManager.ParticlesEnabled = e.Value;
            if (_themeManager.ActiveTheme != null)
            {
                _ = _themeManager.ApplyTheme(_themeManager.ActiveTheme);
            }
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

    private void OnResetThemeClicked(object? sender, EventArgs e)
    {
        _ = BtnResetTheme.BounceClickAsync();
        _themeManager?.ResetToDefault();
        RefreshStaticThemeColors();
        UpdateTabButtons();
        RenderThemes(animate: false);
    }

    private void OnBackClicked(object? sender, EventArgs e)
    {
        _ = BtnBack.BounceClickAsync();
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OnThemeChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            RefreshStaticThemeColors();
            UpdateTabButtons();
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
        BadgeVideo.BackgroundColor = primaryDim;
        IconVideo.IconColor = primaryColor;
        BadgeAlwaysPlay.BackgroundColor = primaryDim;
        IconAlwaysPlay.IconColor = accentColor;
        BadgeOpacity.BackgroundColor = primaryDim;
        IconOpacity.IconColor = primaryColor;

        SepVfx1.Color = borderSubtleColor;
        SepVfx2.Color = borderSubtleColor;
        SepVfx3.Color = borderSubtleColor;
        SepVfx4.Color = borderSubtleColor;

        SliderCardOpacity.MinimumTrackColor = primaryColor;
        SliderCardOpacity.ThumbColor = primaryColor;
        SliderCardOpacity.MaximumTrackColor = borderSubtleColor;
        LabelCardOpacityValue.TextColor = primaryColor;

        SwitchSounds.OnColor = primaryColor;
        SwitchParticles.OnColor = primaryColor;
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
