//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
using Microsoft.Maui.Controls.Shapes;

namespace obxodka.Pages;

public sealed partial class ThemeStoreView
{
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

        _ = MainPage.Current?.SetSidebarDimmedAsync(true);
        ThemePreviewModalOverlay.IsVisible = true;
        ThemePreviewModalOverlay.InputTransparent = false;
        ThemePreviewModalCard.Scale = 0.92;
        ThemePreviewModalCard.Opacity = 0;

        _ = ThemePreviewModalOverlay.FadeToAsync(1.0, 180, Easing.CubicOut);
        _ = ThemePreviewModalCard.FadeToAsync(1.0, 180, Easing.CubicOut);
        _ = ThemePreviewModalCard.ScaleToAsync(1.0, 220, Easing.SpringOut);
    }

    //флаг открытого состояния превью темы
    public bool IsPreviewModalOpen => ThemePreviewModalOverlay?.IsVisible == true;

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

    public async Task ClosePreviewModalAsync()
    {
        ThemePreviewModalOverlay.InputTransparent = true;
        _ = MainPage.Current?.SetSidebarDimmedAsync(false);
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
}
