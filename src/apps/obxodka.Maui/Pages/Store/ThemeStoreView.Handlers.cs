namespace obxodka.Pages;

public sealed partial class ThemeStoreView
{
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
