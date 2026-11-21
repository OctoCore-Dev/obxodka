namespace obxodka.Pages.Store;

public sealed partial class ThemeStoreView
{
    private (Border CardBorder, HorizontalStackLayout ActionLayout) CreateThemeCard(ThemeCardModel model)
    {
        var cornerRadius = Application.Current?.Resources.TryGetValue("CardCornerRadius", out var cr) == true &&
                           cr is CornerRadius themeRadius
            ? themeRadius
            : new CornerRadius(16);

        var cardBorder = new Border
        {
            HorizontalOptions = LayoutOptions.Fill,
            Padding = new Thickness(16),
            StrokeThickness = model.IsActive ? 2.0 : 1.0,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = cornerRadius
            }
        };
        cardBorder.SetDynamicResource(BackgroundColorProperty, "BgSurface");
        cardBorder.SetDynamicResource(Border.StrokeProperty, model.IsActive ? "Primary" : "BorderSubtle");

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
        tapPreviewIcon.Tapped += (_, _) => OpenThemePreviewModal(model);
        iconImage.GestureRecognizers.Add(tapPreviewIcon);
        mainLayout.Add(iconImage, 0);

        var contentStack = new VerticalStackLayout
        {
            Spacing = 5
        };

        var headerLayout = new HorizontalStackLayout
        {
            Spacing = 8
        };
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

        var badgesLayout = new HorizontalStackLayout
        {
            Spacing = 6,
            Margin = new Thickness(0, 2, 0, 6)
        };
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

        var actionLayout = new HorizontalStackLayout
        {
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };
        PopulateActionLayout(actionLayout, model);
        contentStack.Add(actionLayout);
        mainLayout.Add(contentStack, 1);
        cardBorder.Content = mainLayout;

        return (cardBorder, actionLayout);
    }

    private void PopulateActionLayout(HorizontalStackLayout actionLayout, ThemeCardModel model)
    {
        if (model.IsActive)
        {
            var (btnActive, _) = CreateActionPill(
                "Активна",
                FluentIcons.Checkmark24,
                16,
                "Success",
                null,
                null,
                Colors.White,
                "AppFontBold");
            actionLayout.Add(btnActive);

            if (model.HasUpdate && !string.IsNullOrEmpty(model.UpdateVersion))
            {
                actionLayout.Add(CreateUpdateButton(model.Id, model.Name, model.UpdateVersion, true));
            }
        }
        else if (model.IsInstalled)
        {
            var (btnApply, _) = CreateActionPill(
                "Применить",
                FluentIcons.Play24,
                15,
                "Primary",
                null,
                null,
                Colors.White,
                "AppFontBold");
            AttachTapAction(btnApply, async () =>
            {
                ThemeAudioService.PlayClick();
                await btnApply.BounceClickAsync();
                var list = _themeManager?.GetInstalledThemes() ?? [];
                var match = list.FirstOrDefault(t => t.Id == model.Id);
                if (match is not null && _themeManager is not null)
                {
                    _ = await _themeManager.ApplyThemeAsync(match);
                }
            });
            actionLayout.Add(btnApply);

            if (model.HasUpdate && !string.IsNullOrEmpty(model.UpdateVersion))
            {
                actionLayout.Add(CreateUpdateButton(model.Id, model.Name, model.UpdateVersion, false));
            }
        }
        else
        {
            var curProg = 0.0;
            var isDownloading = _themeManager?.IsThemeDownloading(model.Id, out curProg) == true;
            var initialText = isDownloading
                ? curProg > 0 ? $"Загрузка {(int)Math.Round(curProg * 100)}%" : "Загрузка..."
                : "Скачать";

            var (btnDownload, downloadLabel) = CreateActionPill(
                initialText,
                FluentIcons.ArrowDownload24,
                16,
                "Primary",
                null,
                null,
                Colors.White,
                "AppFontBold");
            btnDownload.IsEnabled = !isDownloading;
            btnDownload.Opacity = isDownloading ? 0.7 : 1.0;
            _activeDownloadLabels[model.Id] = downloadLabel;

            AttachTapAction(btnDownload, async () =>
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
                    if (ok)
                    {
                        return;
                    }

                    downloadLabel.Text = "Ошибка";
                    btnDownload.IsEnabled = true;
                    btnDownload.Opacity = 1.0;
                    var err = _themeManager?.LastError ?? "Неизвестная ошибка загрузки";
                    LabelStatus.Text = $"Ошибка загрузки темы: {err}";
                    LabelStatus.IsVisible = true;
                    await NeoAlert.ShowAsync("Ошибка загрузки темы", err);
                }
                catch (Exception ex)
                {
                    downloadLabel.Text = "Ошибка";
                    btnDownload.IsEnabled = true;
                    btnDownload.Opacity = 1.0;
                    LabelStatus.Text = $"Исключение: {ex.Message}";
                    LabelStatus.IsVisible = true;
                    await NeoAlert.ShowAsync("Ошибка загрузки темы", ex.Message);
                }
            });
            actionLayout.Add(btnDownload);
        }

        actionLayout.Add(CreateIconSquareButton(
            FluentIcons.Eye24,
            GetAppColor("TextSecondary", Color.FromArgb("#A0A0B0")),
            "Предпросмотр темы",
            () =>
            {
                OpenThemePreviewModal(model);
                return Task.CompletedTask;
            }));

        if (model.IsInstalled)
        {
            var (btnDelete, _) = CreateActionPill(
                "Удалить",
                FluentIcons.Delete24,
                16,
                null,
                Color.FromArgb("#22EF4444"),
                Color.FromArgb("#44EF4444"),
                Color.FromArgb("#EF4444"),
                "AppFontMedium",
                12);
            ToolTipProperties.SetText(btnDelete, "Удалить тему");
            AttachTapAction(btnDelete, async () =>
            {
                ThemeAudioService.PlayClick();
                await btnDelete.BounceClickAsync();
                var confirm = await NeoAlert.ShowConfirmAsync(
                    "Удаление темы",
                    $"Вы уверены, что хотите удалить тему '{model.Name}' с устройства?",
                    "Удалить");

                if (!confirm || _themeManager is null)
                {
                    return;
                }

                if (!_themeManager.DeleteTheme(model.Id))
                {
                    await NeoAlert.ShowAsync("Ошибка", "Не удалось удалить тему.");
                }

                UpdateTabButtons();
                RenderThemes(false);
            });
            actionLayout.Add(btnDelete);
        }

        if (model.IsCommunity)
        {
            actionLayout.Add(CreateIconSquareButton(
                FluentIcons.Flag24,
                GetAppColor("TextMuted", Color.FromArgb("#A0A0B0")),
                "Пожаловаться на тему",
                () => NeoAlert.ShowAsync(
                    "Жалоба на тему",
                    $"Жалоба на тему '{model.Name}' отправлена на модерацию. Спасибо за помощь в поддержании чистоты и безопасности сообщества!",
                    "ОК")));
        }
    }

    private static (Border Button, Label TextLabel) CreateActionPill(
        string text,
        FluentIcons icon,
        int iconSize,
        string? bgResourceKey,
        Color? bgColor,
        Color? strokeColor,
        Color textColor,
        string fontResourceKey,
        double paddingX = 14)
    {
        var label = new Label
        {
            Text = text,
            FontSize = 12,
            TextColor = textColor,
            VerticalOptions = LayoutOptions.Center
        };
        label.SetDynamicResource(Label.FontFamilyProperty, fontResourceKey);

        var border = new Border
        {
            HeightRequest = 36,
            Padding = new Thickness(paddingX, 0),
            StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(10)
            },
            StrokeThickness = strokeColor is not null ? 1 : 0,
            VerticalOptions = LayoutOptions.Center,
            Content = new HorizontalStackLayout
            {
                Spacing = 6,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    new MauiIcon
                    {
                        Icon = icon,
                        IconSize = iconSize,
                        IconColor = textColor,
                        VerticalOptions = LayoutOptions.Center
                    },
                    label
                }
            }
        };

        if (bgResourceKey is not null)
        {
            border.SetDynamicResource(BackgroundColorProperty, bgResourceKey);
        }
        else if (bgColor is not null)
        {
            border.BackgroundColor = bgColor;
        }

        if (strokeColor is not null)
        {
            border.Stroke = strokeColor;
        }

        return (border, label);
    }

    private static Border CreateIconSquareButton(
        FluentIcons icon,
        Color iconColor,
        string tooltip,
        Func<Task> onTapAsync)
    {
        var border = new Border
        {
            HeightRequest = 36,
            WidthRequest = 36,
            StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(10)
            },
            BackgroundColor = Color.FromArgb("#15FFFFFF"),
            Stroke = Color.FromArgb("#25FFFFFF"),
            StrokeThickness = 1,
            VerticalOptions = LayoutOptions.Center,
            Content = new MauiIcon
            {
                HorizontalOptions = LayoutOptions.Center,
                VerticalOptions = LayoutOptions.Center,
                Icon = icon,
                IconSize = 16,
                IconColor = iconColor
            }
        };
        ToolTipProperties.SetText(border, tooltip);
        AttachTapAction(border, async () =>
        {
            ThemeAudioService.PlayClick();
            await border.BounceClickAsync();
            await onTapAsync();
        });
        return border;
    }

    private static void AttachTapAction(Border target, Func<Task> actionAsync)
    {
        var tap = new TapGestureRecognizer();
        tap.Tapped += async (_, _) =>
        {
            try
            {
                await actionAsync();
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[THEME STORE ACTION ERROR] {ex.Message}", ex);
            }
        };
        target.GestureRecognizers.Add(tap);
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
            StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(6)
            },
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
            StrokeShape = new RoundRectangle
            {
                CornerRadius = new CornerRadius(6)
            },
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
        var curProg = 0.0;
        var isDownloading = _themeManager?.IsThemeDownloading(themeId, out curProg) == true;
        var text = isDownloading
            ? curProg > 0 ? $"Обновление {(int)Math.Round(curProg * 100)}%" : "Обновление..."
            : $"Обновить (v{targetVersion})";

        var (btnUpdate, updateLabel) = CreateActionPill(
            text,
            FluentIcons.ArrowDownload24,
            16,
            null,
            Color.FromArgb("#D97706"),
            null,
            Colors.White,
            "AppFontBold");
        btnUpdate.IsEnabled = !isDownloading;
        btnUpdate.Opacity = isDownloading ? 0.7 : 1.0;
        _activeDownloadLabels[themeId] = updateLabel;
        ToolTipProperties.SetText(btnUpdate, $"Доступно обновление до версии v{targetVersion}");

        AttachTapAction(btnUpdate, async () =>
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
                    await NeoAlert.ShowAsync("Обновление темы", msg);
                }
                else
                {
                    updateLabel.Text = "Ошибка";
                    btnUpdate.IsEnabled = true;
                    btnUpdate.Opacity = 1.0;
                    var err = _themeManager?.LastError ?? "Неизвестная ошибка при обновлении";
                    LabelStatus.Text = $"Ошибка обновления темы: {err}";
                    LabelStatus.IsVisible = true;
                    await NeoAlert.ShowAsync("Ошибка обновления темы", err);
                }
            }
            catch (Exception ex)
            {
                updateLabel.Text = "Ошибка";
                btnUpdate.IsEnabled = true;
                btnUpdate.Opacity = 1.0;
                LabelStatus.Text = $"Исключение: {ex.Message}";
                LabelStatus.IsVisible = true;
                await NeoAlert.ShowAsync("Ошибка обновления темы", ex.Message);
            }
        });

        return btnUpdate;
    }
}
