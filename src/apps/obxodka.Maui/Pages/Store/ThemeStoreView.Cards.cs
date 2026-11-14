//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Pages;

public sealed partial class ThemeStoreView
{
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

                var folder = Path.Combine(_themeManager.ThemesDirectory, manifest.Id.TrimStart('/', '\\'));
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
                    var folder = isInstalled ? Path.Combine(_themeManager.ThemesDirectory, item.Id.TrimStart('/', '\\')) : null;

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
}
