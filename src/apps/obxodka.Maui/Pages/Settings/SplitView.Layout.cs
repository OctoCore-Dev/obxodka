namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
    //единая адаптация экрана разделения трафика по ssot-снимку геометрии окна без widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayout is null || HeaderContainer is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);
        RootLayout.Padding = new Thickness(sidePad, 0);

        var safeTop = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 10.0, isCompact ? 38.0 : 22.0)
            : isCompact ? 10.0 : 12.0;
        HeaderContainer.Margin = new Thickness(0, safeTop, 0, 0);

        UpdateTopSpacers();
    }

    private void OnHeaderContainerSizeChanged(object? sender, EventArgs e) => UpdateTopSpacers();

    private void UpdateTopSpacers()
    {
        if (HeaderContainer is null)
        {
            return;
        }

        var topMargin = HeaderContainer.Margin.Top;
        var spacerH = HeaderContainer.Height > 0
            ? HeaderContainer.Height + topMargin + 10.0
            : topMargin + 210.0;

        if (AppsListTopSpacer is not null && Math.Abs(AppsListTopSpacer.HeightRequest - spacerH) > 1.0)
        {
            AppsListTopSpacer.HeightRequest = spacerH;
        }

        if (SitesListTopSpacer is not null && Math.Abs(SitesListTopSpacer.HeightRequest - spacerH) > 1.0)
        {
            SitesListTopSpacer.HeightRequest = spacerH;
        }
    }

#if WINDOWS
    //нативный winui 3 обработчик прокрутки списка приложений для надежного скрытия и возврата шапки
    private void OnNativeAppsScrollOffsetChanged(double scrollY)
    {
        if (SplitAppsCollectionView.Height <= 0 || _isSitesTabActive)
        {
            return;
        }

        var deltaY = scrollY - _lastAppsScrollY;
        _lastAppsScrollY = scrollY;
        UpdateQuickReturnHeader(scrollY, deltaY);
    }
#endif

    private void OnSplitAppsCollectionScrolled(object? sender, ItemsViewScrolledEventArgs e)
    {
#if WINDOWS
        if (_detachNativeAppsScroll is not null)
        {
            return;
        }
#endif
        if (SplitAppsCollectionView.Height <= 0 || _isSitesTabActive)
        {
            return;
        }

        var effectiveOffset = Math.Max(e.VerticalOffset, e.FirstVisibleItemIndex * 70.0);
        var rawDelta = Math.Abs(e.VerticalDelta) > 0.01
            ? e.VerticalDelta
            : effectiveOffset - _lastAppsScrollY;
        _lastAppsScrollY = effectiveOffset;

        UpdateQuickReturnHeader(effectiveOffset, rawDelta);
    }

    private void OnSitesScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        if (SitesSplitScrollView.Height <= 0)
        {
            return;
        }

        var currentScrollY = e.ScrollY;
        var deltaY = currentScrollY - _lastSitesScrollY;
        _lastSitesScrollY = currentScrollY;
        UpdateQuickReturnHeader(currentScrollY, deltaY);
    }

    private void UpdateQuickReturnHeader(double scrollY, double deltaY)
    {
        if (HeaderContainer is null || HeaderContainer.Height <= 0 || SplitSearchEntry is not null && SplitSearchEntry.IsFocused)
        {
            return;
        }

        var maxHideOffset = HeaderContainer.Height + HeaderContainer.Margin.Top + 20.0;

        if (scrollY <= 25.0)
        {
            _accumulatedScrollDelta = 0;
            if (!_isHeaderHidden && !(Math.Abs(HeaderContainer.TranslationY) > 0.5))
            {
                return;
            }

            _isHeaderHidden = false;
            _ = ReplaceCtsToken(ref _headerAnimCts);
            _ = AnimateHeaderAsync(0, 1.0, false);

            return;
        }

        //накапливаем смещение одного направления чтобы плавные мелкие шаги колесика мыши надежно скрывали и возвращали шапку
        if (deltaY > 0 && _accumulatedScrollDelta < 0 || deltaY < 0 && _accumulatedScrollDelta > 0)
        {
            _accumulatedScrollDelta = deltaY;
        }
        else
        {
            _accumulatedScrollDelta += deltaY;
        }

        switch (_accumulatedScrollDelta)
        {
            case > 14.0 when !_isHeaderHidden && scrollY > 55.0:
                _accumulatedScrollDelta = 0;
                _isHeaderHidden = true;
                _ = ReplaceCtsToken(ref _headerAnimCts);
                _ = AnimateHeaderAsync(-maxHideOffset, 0.0, true);
                break;
            case < -14.0 when _isHeaderHidden:
                _accumulatedScrollDelta = 0;
                _isHeaderHidden = false;
                _ = ReplaceCtsToken(ref _headerAnimCts);
                _ = AnimateHeaderAsync(0, 1.0, false);
                break;
        }
    }

    private async Task AnimateHeaderAsync(double targetY, double targetOpacity, bool inputTransparent)
    {
        try
        {
            HeaderContainer.InputTransparent = inputTransparent;
            var dist = Math.Abs(HeaderContainer.TranslationY - targetY);
            var duration = (uint)Math.Clamp(dist * 0.8, 140, 240);
            var tTask = HeaderContainer.TranslateToAsync(0, targetY, duration, Easing.CubicOut);
            var fTask = HeaderContainer.FadeToAsync(targetOpacity, duration, Easing.CubicOut);
            _ = await Task.WhenAll(tTask, fTask).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ResetHeaderToVisible()
    {
        _isHeaderHidden = false;
        _accumulatedScrollDelta = 0;
        CancelAndDisposeCts(ref _headerAnimCts);
        if (HeaderContainer is not null)
        {
            HeaderContainer.TranslationY = 0;
            HeaderContainer.Opacity = 1.0;
            HeaderContainer.InputTransparent = false;
        }

        _lastAppsScrollY = 0;
        _lastSitesScrollY = 0;
    }

    public async Task PlayEntranceAnimationAsync()
    {
#if WINDOWS
        LoadWindowsSitesAndPresetsState();
#endif
        ResetHeaderToVisible();
        Opacity = 1;
        TranslationY = 0;
        if (_isSitesTabActive)
        {
            await UiAnimations.PlayEntranceCascadeAsync(
                30,
                230,
                HeaderContainer,
                SitesInfoBanner,
                PresetGovCard,
                PresetBanksCard,
                PresetMediaCard,
                PresetMarketCard,
                PresetRuTldsCard,
                CustomRulesCard,
                ActiveRoutesCard);
        }
        else
        {
            await UiAnimations.PlayEntranceCascadeAsync(35, 220, HeaderContainer, SplitAppsCollectionView);
        }
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
            ? bsc
            : Color.FromArgb("#282838");

        HeaderContainer.BackgroundColor = bgSurface;
        HeaderContainer.Stroke = borderSubtle;
        ChipResetAll.BackgroundColor = InactiveChipBg;
        ChipRefreshApps.BackgroundColor = InactiveChipBg;
        UpdateFilterChipStyles();
        UpdateModeTabStyles();
    }
}
