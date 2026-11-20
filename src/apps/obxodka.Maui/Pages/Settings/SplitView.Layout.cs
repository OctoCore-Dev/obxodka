namespace obxodka.Pages.Settings;

public sealed partial class SplitView
{
    private void OnParentOrSidebarSizeChanged(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            ForceLayoutWidth();
        }
    }

    private double ComputeAvailableContentWidth()
    {
        if (RootLayout is null)
        {
            return 0;
        }

        var pad = RootLayout.Padding.HorizontalThickness;
        if (_parent.Width > 0 && DeviceInfo.Idiom != DeviceIdiom.Phone)
        {
            var sidebarW = _sidebar?.OccupiedWidth ?? 0;
            var fromParent = _parent.Width - sidebarW - pad;
            if (fromParent > 0)
            {
                return fromParent;
            }
        }

        if (!(RootLayout.Width > 0))
        {
            return Width > 0 ? Math.Max(0, Width - pad) : 0;
        }

        var fromRoot = RootLayout.Width - pad;
        return !(fromRoot > 0) ? Width > 0 ? Math.Max(0, Width - pad) : 0 : fromRoot;
    }

    public void ForceLayoutWidth()
    {
        var avail = ComputeAvailableContentWidth();
        if (avail > 0)
        {
            ApplyCardWidth(avail);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        ForceLayoutWidth();
    }

    private void OnRootLayoutSizeChanged(object? sender, EventArgs e) => ForceLayoutWidth();

    private void OnHeaderContainerSizeChanged(object? sender, EventArgs e)
    {
        if (HeaderContainer.Height <= 0)
        {
            return;
        }

        var spacerH = HeaderContainer.Height + HeaderContainer.Margin.Top + 10;
        if (AppsListTopSpacer is not null && Math.Abs(AppsListTopSpacer.HeightRequest - spacerH) > 1)
        {
            AppsListTopSpacer.HeightRequest = spacerH;
        }

        if (SitesListTopSpacer is not null && Math.Abs(SitesListTopSpacer.HeightRequest - spacerH) > 1)
        {
            SitesListTopSpacer.HeightRequest = spacerH;
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || MainContentGrid is null)
        {
            return;
        }

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            if (_lastAppliedWidth == 0)
            {
                return;
            }

            _lastAppliedWidth = 0;
            MainContentGrid.ClearValue(WidthRequestProperty);
            MainContentGrid.ClearValue(MaximumWidthRequestProperty);
            SplitAppsScrollView?.ClearValue(WidthRequestProperty);
            SplitAppsScrollView?.ClearValue(MaximumWidthRequestProperty);
            SplitAppsContentStack?.ClearValue(WidthRequestProperty);
            SplitAppsContentStack?.ClearValue(MaximumWidthRequestProperty);
            SitesSplitScrollView?.ClearValue(WidthRequestProperty);
            SitesSplitScrollView?.ClearValue(MaximumWidthRequestProperty);
            SitesContentStack?.ClearValue(WidthRequestProperty);
            SitesContentStack?.ClearValue(MaximumWidthRequestProperty);
            HeaderContainer?.ClearValue(WidthRequestProperty);
            HeaderContainer?.ClearValue(MaximumWidthRequestProperty);
            ActiveBypassedScrollView?.ClearValue(MaximumWidthRequestProperty);
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0 || Math.Abs(_lastAppliedWidth - safeWidth) < 1.0)
        {
            return;
        }

        _lastAppliedWidth = safeWidth;

        MainContentGrid.ClearValue(WidthRequestProperty);
        MainContentGrid.ClearValue(MaximumWidthRequestProperty);
        SplitAppsScrollView?.ClearValue(WidthRequestProperty);
        SplitAppsScrollView?.ClearValue(MaximumWidthRequestProperty);
        SitesSplitScrollView?.ClearValue(WidthRequestProperty);
        SitesSplitScrollView?.ClearValue(MaximumWidthRequestProperty);

        if (SplitAppsContentStack is not null)
        {
            SplitAppsContentStack.WidthRequest = safeWidth;
            SplitAppsContentStack.MaximumWidthRequest = safeWidth;
        }

        if (SitesContentStack is not null)
        {
            SitesContentStack.WidthRequest = safeWidth;
            SitesContentStack.MaximumWidthRequest = safeWidth;
        }

        if (HeaderContainer is not null)
        {
            HeaderContainer.WidthRequest = safeWidth;
            HeaderContainer.MaximumWidthRequest = safeWidth;
        }

        ActiveBypassedScrollView?.MaximumWidthRequest = safeWidth;
    }

    private void OnSplitAppsScrollViewScrolled(object? sender, ScrolledEventArgs e)
    {
        if (SplitAppsScrollView.Height <= 0)
        {
            return;
        }

        var currentScrollY = e.ScrollY;
        var deltaY = currentScrollY - _lastAppsScrollY;
        _lastAppsScrollY = currentScrollY;
        UpdateQuickReturnHeader(currentScrollY, deltaY);

        var containerOffset = SplitAppsItemsStack.Y > 0 ? SplitAppsItemsStack.Y : AppsListTopSpacer?.HeightRequest ?? 0.0;
        CardStackScrollHelper.ApplyToChildren(
            SplitAppsItemsStack.Children.OfType<VisualElement>(),
            currentScrollY,
            SplitAppsScrollView.Height,
            containerOffset,
            70.0,
            0.90,
            2.2);
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

        if (SitesSplitScrollView.Content is VerticalStackLayout stack)
        {
            CardStackScrollHelper.ApplyToChildren(
                stack.Children.OfType<VisualElement>().Where(c => c != SitesListTopSpacer),
                currentScrollY,
                SitesSplitScrollView.Height,
                0.0,
                70.0,
                0.90,
                2.2);
        }
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
            if (!_isHeaderHidden && !(Math.Abs(HeaderContainer.TranslationY) > 0.5))
            {
                return;
            }

            _isHeaderHidden = false;
            _headerAnimCts?.Cancel();
            _headerAnimCts = new CancellationTokenSource();
            _ = AnimateHeaderAsync(0, 1.0, false);

            return;
        }

        switch (deltaY)
        {
            case > 12.0 when !_isHeaderHidden && scrollY > 70.0:
                _isHeaderHidden = true;
                _headerAnimCts?.Cancel();
                _headerAnimCts = new CancellationTokenSource();
                _ = AnimateHeaderAsync(-maxHideOffset, 0.0, true);
                break;
            case < -10.0 when _isHeaderHidden:
                _isHeaderHidden = false;
                _headerAnimCts?.Cancel();
                _headerAnimCts = new CancellationTokenSource();
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
        _headerAnimCts?.Cancel();
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
            await UiAnimations.PlayEntranceCascadeAsync(35, 220, HeaderContainer, SplitAppsScrollView);
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

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone)
        {
            return;
        }

        var safeTop = DeviceInfo.Platform == DevicePlatform.Android
            ? Math.Max(top + 8, 38)
            : Math.Max(top, 14);

        HeaderContainer.Margin = new Thickness(16, safeTop, 16, 0);

        var spacerH = HeaderContainer.Height > 0
            ? HeaderContainer.Height + safeTop + 10
            : safeTop + 240;

        if (AppsListTopSpacer is not null && Math.Abs(AppsListTopSpacer.HeightRequest - spacerH) > 1)
        {
            AppsListTopSpacer.HeightRequest = spacerH;
        }

        if (SitesListTopSpacer is not null && Math.Abs(SitesListTopSpacer.HeightRequest - spacerH) > 1)
        {
            SitesListTopSpacer.HeightRequest = spacerH;
        }
    }
}
