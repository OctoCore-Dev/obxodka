namespace obxodka.Pages.Vpn;

//адаптивная верстка, отступы под safe area и реакция на смену темы
public sealed partial class VpnView
{
    private double _lastAppliedButtonSize = -1;
    private int _lastVpnLayoutMode = -1;
    private AdaptiveViewport _lastViewport;

    //актуализация прозрачности карточек под активную тему
    public void UpdateCardOpacity()
    {
        var bgSurface = GetAppColor("BgSurface", Color.FromArgb("#161622"));
        var bgElevated = GetAppColor("BgElevated", Color.FromArgb("#202030"));

        CardIp.BackgroundColor = bgSurface;
        Card2.BackgroundColor = bgSurface;
        Card5.BackgroundColor = bgSurface;
        Card6.BackgroundColor = bgSurface;
        Card1Wrapper.BackgroundColor = bgSurface;
        ServerSelectorButton.BackgroundColor = bgElevated;
    }

    //единая адаптация экрана vpn по ssot-снимку геометрии окна без ручного widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayoutGrid is null || ContentGrid is null)
        {
            return;
        }

        if (_lastViewport == viewport && _lastAppliedButtonSize > 0)
        {
            return;
        }

        _lastViewport = viewport;

        var isCompact = viewport.IsCompact;
        var isWide = viewport.UseTwoColumnVpn;
        var isCompactLandscape = viewport.IsCompactLandscape;
        var baseSidePad = isCompactLandscape ? 12.0 : isCompact ? 12.0 : isWide ? 24.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, isWide ? 1040.0 : 760.0);
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(96.0, viewport.SafeBottom + 86.0)
            : isCompactLandscape
                ? Math.Max(10.0, viewport.SafeBottom + 6.0)
                : Math.Max(20.0, viewport.SafeBottom + 12.0);
        var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? isCompactLandscape
                ? Math.Max(viewport.SafeTop + 6.0, 10.0)
                : Math.Max(viewport.SafeTop + 4.0, isCompact ? 28.0 : 16.0)
            : isCompactLandscape
                ? 10.0
                : isCompact ? 10.0 : 16.0;

        RootLayoutGrid.Padding = new Thickness(sidePad, 0, sidePad, 0);
        ContentGrid.HorizontalOptions = LayoutOptions.Fill;
        ContentGrid.Padding = new Thickness(0, topPad, 0, bottomPad);

        ServerModalOverlay?.Margin = new Thickness(-sidePad, 0, -sidePad, 0);

        if (ServerModalCard is not null && viewport.ContentWidth > 0 && viewport.WindowHeight > 0)
        {
            var modalW = Math.Clamp(viewport.ContentWidth - 40.0, 280.0, 440.0);
            ServerModalCard.MaximumWidthRequest = modalW;
            ServerModalCard.WidthRequest = modalW;
            ServerModalCard.MaximumHeightRequest = Math.Clamp(viewport.WindowHeight - 28.0, 260.0, 560.0);
        }

        if (isWide)
        {
            var targetMode = isCompactLandscape ? 1 : 2;
            if (_lastVpnLayoutMode != targetMode)
            {
                _lastVpnLayoutMode = targetMode;
                ContentGrid.ColumnDefinitions.Clear();
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5, GridUnitType.Star)));
                ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(isCompactLandscape ? 5 : 4, GridUnitType.Star)));
                ContentGrid.RowDefinitions.Clear();
                ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                ContentGrid.RowSpacing = 0;
                Grid.SetColumn(Card1Wrapper, 0);
                Grid.SetRow(Card1Wrapper, 0);
                Grid.SetColumn(TopCardsGrid, 1);
                Grid.SetRow(TopCardsGrid, 0);

                TopCardsGrid.RowDefinitions.Clear();
                if (isCompactLandscape)
                {
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }
                else
                {
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                }
            }

            if (isCompactLandscape)
            {
                //компактный двухколоночный дашборд для альбомных телефонов и низких окон с живым графиком
                ContentGrid.ColumnSpacing = 12;
                ContentGrid.MinimumHeightRequest = Math.Max(320.0, viewport.WindowHeight);
                TopCardsGrid.RowSpacing = 8;

                Card1Wrapper.MinimumHeightRequest = -1;
                Card1ContentGrid.Padding = new Thickness(14, 10);
                Card1ContentGrid.RowSpacing = 8;
                StatusLabel.Margin = new Thickness(0, 4, 0, 0);
                StatusLabel.FontSize = 13;

                CardIp.Padding = new Thickness(8, 6);
                Card2.Padding = new Thickness(12, 8);
                GraphContainer.IsVisible = true;
                GraphContainer.HeightRequest = -1;
                GraphContainer.MinimumHeightRequest = 56;

                Card5.Padding = new Thickness(10, 8);
                Card6.Padding = new Thickness(10, 8);
                Card5.HeightRequest = -1;
                Card6.HeightRequest = -1;
                Card5.MinimumHeightRequest = 64;
                Card6.MinimumHeightRequest = 64;
                TokenAmountLabel.FontSize = 14.5;
                TotalTrafficLabelMain.FontSize = 14.5;
            }
            else
            {
                ContentGrid.ColumnSpacing = 22;
                ContentGrid.MinimumHeightRequest = viewport.WindowHeight >= 520.0 ? viewport.WindowHeight : -1;
                TopCardsGrid.RowSpacing = 16;

                Card1Wrapper.MinimumHeightRequest = -1;
                Card1ContentGrid.Padding = new Thickness(24, 24);
                Card1ContentGrid.RowSpacing = 16;
                StatusLabel.Margin = new Thickness(0, 12, 0, 0);
                StatusLabel.FontSize = 15;

                CardIp.Padding = new Thickness(14, 10);
                Card2.Padding = new Thickness(16);
                GraphContainer.IsVisible = true;
                GraphContainer.HeightRequest = -1;
                GraphContainer.MinimumHeightRequest = 140;

                Card5.Padding = new Thickness(14, 12);
                Card6.Padding = new Thickness(14, 12);
                Card5.HeightRequest = -1;
                Card6.HeightRequest = -1;
                Card5.MinimumHeightRequest = 72;
                Card6.MinimumHeightRequest = 72;
                TokenAmountLabel.FontSize = 18;
                TotalTrafficLabelMain.FontSize = 18;
            }

            var wideBtn = AdaptiveLayoutHelper.CalculateWideVpnButtonSize(viewport);
            ApplyButtonMetrics(
                wideBtn,
                (int)Math.Max(26, Math.Round(wideBtn * 0.165)),
                Math.Max(12, Math.Round(wideBtn * 0.058)));
            TrafficGraphCanvas.InvalidateSurface();
            return;
        }

        if (_lastVpnLayoutMode != 0 || ContentGrid.ColumnDefinitions.Count != 1 || Grid.GetRow(Card1Wrapper) != 1)
        {
            _lastVpnLayoutMode = 0;
            ContentGrid.ColumnDefinitions.Clear();
            ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            ContentGrid.RowDefinitions.Clear();
            ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            ContentGrid.ColumnSpacing = 0;
            ContentGrid.RowSpacing = 12;
            Grid.SetColumn(TopCardsGrid, 0);
            Grid.SetRow(TopCardsGrid, 0);
            Grid.SetColumn(Card1Wrapper, 0);
            Grid.SetRow(Card1Wrapper, 1);
            TopCardsGrid.RowDefinitions.Clear();
            TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }

        var metrics = AdaptiveLayoutHelper.CalculateVerticalVpnLayout(viewport, topPad, bottomPad);
        ContentGrid.MinimumHeightRequest = metrics.ContentMinHeight;
        CardIp.Padding = isCompact ? new Thickness(8, 6) : new Thickness(12, 8);
        Card2.Padding = isCompact ? new Thickness(12, 8) : new Thickness(14, 12);
        Card5.Padding = isCompact ? new Thickness(12, 8) : new Thickness(14, 10);
        Card6.Padding = isCompact ? new Thickness(12, 8) : new Thickness(14, 10);
        Card5.HeightRequest = -1;
        Card6.HeightRequest = -1;
        Card5.MinimumHeightRequest = metrics.StatCardHeight;
        Card6.MinimumHeightRequest = metrics.StatCardHeight;
        TokenAmountLabel.FontSize = isCompact ? 15 : 18;
        TotalTrafficLabelMain.FontSize = isCompact ? 15 : 18;
        Card1ContentGrid.RowSpacing = isCompact ? 8 : 12;
        StatusLabel.Margin = isCompact ? new Thickness(0, 4, 0, 0) : new Thickness(0, 8, 0, 0);
        StatusLabel.FontSize = isCompact ? 13 : 15;

        if (!metrics.ShowGraph)
        {
            Card1Wrapper.MinimumHeightRequest = -1;
            Card1ContentGrid.Padding = new Thickness(14, 10, 14, 12);
            TopCardsGrid.RowSpacing = 8;
            ContentGrid.RowSpacing = 8;
            GraphContainer.IsVisible = false;
            GraphContainer.MinimumHeightRequest = 0;
            GraphContainer.HeightRequest = 0;
        }
        else
        {
            Card1ContentGrid.Padding = isCompact
                ? new Thickness(14, 12, 14, 12)
                : new Thickness(18, 16, 18, 18);
            TopCardsGrid.RowSpacing = isCompact ? 8 : 10;
            ContentGrid.RowSpacing = isCompact ? 8 : 12;
            GraphContainer.IsVisible = true;
            GraphContainer.MinimumHeightRequest = 76;
            GraphContainer.HeightRequest = metrics.GraphHeight;
            Card1Wrapper.MinimumHeightRequest = metrics.HeroHeight;
        }

        ApplyButtonMetrics(
            metrics.ButtonSize,
            (int)Math.Max(22, Math.Round(metrics.ButtonSize * 0.165)),
            Math.Max(11, Math.Round(metrics.ButtonSize * 0.058)));

        TrafficGraphCanvas.InvalidateSurface();
    }

    //применение размеров колец кнопки только при реальном изменении габаритов без лишних аллокаций
    private void ApplyButtonMetrics(double buttonSize, int iconSize, double fontSize)
    {
        if (Math.Abs(_lastAppliedButtonSize - buttonSize) < 0.5)
        {
            return;
        }

        _lastAppliedButtonSize = buttonSize;
        ButtonContainerGrid.WidthRequest = buttonSize;
        ButtonContainerGrid.HeightRequest = buttonSize;

        var outerRingSize = Math.Round(buttonSize * 0.93);
        ButtonOuterRing.WidthRequest = outerRingSize;
        ButtonOuterRing.HeightRequest = outerRingSize;
        ButtonOuterRing.StrokeShape = new RoundRectangle
        {
            CornerRadius = new CornerRadius(outerRingSize / 2.0)
        };

        var middleRingSize = Math.Round(buttonSize * 0.80);
        ButtonMiddleRing.WidthRequest = middleRingSize;
        ButtonMiddleRing.HeightRequest = middleRingSize;
        ButtonMiddleRing.StrokeShape = new RoundRectangle
        {
            CornerRadius = new CornerRadius(middleRingSize / 2.0)
        };

        var coreSize = Math.Round(buttonSize * 0.65);
        ConnectButtonCore.WidthRequest = coreSize;
        ConnectButtonCore.HeightRequest = coreSize;
        ConnectButtonCore.StrokeShape = new RoundRectangle
        {
            CornerRadius = new CornerRadius(coreSize / 2.0)
        };

        ConnectIcon.IconSize = iconSize;
        ConnectButtonText.FontSize = fontSize;
    }

    //перерисовка канвасов и обновление стилей при смене темы
    public void OnThemeChanged()
    {
        void ApplyThemeVisuals()
        {
            if (_disposed)
            {
                return;
            }

            _cachedAuraShader?.Dispose();
            _cachedAuraShader = null;
            _cachedGraphUpFillShader?.Dispose();
            _cachedGraphUpFillShader = null;
            _cachedGraphDownFillShader?.Dispose();
            _cachedGraphDownFillShader = null;

            ButtonEffectsCanvas?.InvalidateSurface();
            TrafficGraphCanvas?.InvalidateSurface();

            UpdateCardOpacity();
            PopulateServerList();

            var borderSubtle = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));

            CardIp.Stroke = borderSubtle;
            Card2.Stroke = borderSubtle;
            Card5.Stroke = borderSubtle;
            Card6.Stroke = borderSubtle;
            Card1Wrapper.Stroke = borderSubtle;
            ServerSelectorButton.Stroke = borderMedium;
            ButtonOuterRing.SetDynamicResource(Border.StrokeProperty, "PrimaryGlow");
            ButtonMiddleRing.SetDynamicResource(Border.StrokeProperty, "PrimaryDim");

            var muted = GetAppColor("TextMuted", s_grayText);
            var textPrimary = GetAppColor("TextPrimary", Colors.White);
            var accent = GetAppColor("Accent", s_cyanAccent);
            if (_vpnService.CurrentState == AppVpnState.Connected)
            {
                UpdateIpStatusDisplay(OctopusEngine.Current.AssignedIp, textPrimary, accent);
            }
            else if (_vpnService.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
            {
                UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, muted, muted);
                ResetPingIndicators();
            }

            var currentState = _vpnService.CurrentState;
            switch (currentState)
            {
                case AppVpnState.Disconnected:
                    StatusLabel.Text = GetStatusTextForState(AppVpnState.Disconnected, VpnStatusMessages.Disconnected);
                    ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Disconnected, VpnStatusMessages.StartAction);
                    break;
                case AppVpnState.Connected:
                    StatusLabel.Text = GetStatusTextForState(AppVpnState.Connected, VpnStatusMessages.Protected);
                    ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Connected, VpnStatusMessages.StopAction);
                    break;
                case AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting:
                    StatusLabel.Text = GetStatusTextForState(currentState, StatusLabel.Text);
                    ConnectButtonText.Text = GetButtonTextForState(currentState, ConnectButtonText.Text);
                    break;
                case AppVpnState.Error:
                    StatusLabel.Text = GetStatusTextForState(AppVpnState.Error, StatusLabel.Text);
                    ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Error, VpnStatusMessages.StartAction);
                    break;
            }

            var hasCustom = !string.IsNullOrEmpty(_buttonImageIdlePath) ||
                            !string.IsNullOrEmpty(_buttonImageConnectingPath) ||
                            !string.IsNullOrEmpty(_buttonImageActivePath) ||
                            !string.IsNullOrEmpty(_buttonImageErrorPath) ||
                            _buttonAnimatedCurrent != null;

            if (hasCustom)
            {
                ConnectButtonCore.BackgroundColor = Colors.Transparent;
                ConnectButtonCore.Stroke = Colors.Transparent;
                return;
            }

            switch (currentState)
            {
                case AppVpnState.Disconnected:
                    UiAnimations.SetVpnDisconnectedColors(ConnectIcon, StatusLabel);
                    SetAuraActive(false);
                    ConnectButtonCore.ClearValue(BackgroundColorProperty);
                    ConnectButtonCore.ClearValue(Border.StrokeProperty);
                    ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                    ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
                    break;
                case AppVpnState.Connected:
                    {
                        UiAnimations.SetVpnConnectedColors(ConnectIcon, StatusLabel);
                        SetAuraActive(true);
                        var accentColor = GetAppColor("Accent", s_cyanAccent);
                        var activeOv = ThemeManager.Instance?.ActiveTheme?.Vfx?.ActiveOverrides;
                        if (!string.IsNullOrWhiteSpace(activeOv?.AccentColor) && Color.TryParse(activeOv.AccentColor, out var ovColor))
                        {
                            accentColor = ovColor;
                            StatusLabel.TextColor = ovColor;
                            ConnectIcon.IconColor = ovColor;
                        }

                        ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                        ConnectButtonCore.Stroke = accentColor;
                        break;
                    }
                case AppVpnState.Error:
                    {
                        UiAnimations.SetVpnDisconnectedColors(ConnectIcon, StatusLabel);
                        SetAuraActive(false);
                        var errColor = GetAppColor("Error", s_redDot);
                        var errDim = GetAppColor("ErrorDim", s_redBg);
                        ConnectIcon.IconColor = errColor;
                        StatusLabel.TextColor = errColor;
                        ConnectButtonCore.BackgroundColor = errDim;
                        ConnectButtonCore.Stroke = errColor;
                        break;
                    }
            }
        }

        if (MainThread.IsMainThread)
        {
            ApplyThemeVisuals();
        }
        else
        {
            MainThread.BeginInvokeOnMainThread(ApplyThemeVisuals);
        }
    }

    //получение цвета из ресурсов приложения с фоллбэком
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
            // ignored
        }

        return fallback;
    }
}
