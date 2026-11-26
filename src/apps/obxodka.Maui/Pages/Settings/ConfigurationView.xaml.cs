namespace obxodka.Pages.Settings;

//экран системных настроек, автозапуска, kill switch и замера скорости
public sealed partial class ConfigurationView : IDisposable, IAdaptivePage
{
#if WINDOWS
    private readonly Action? _windowActivatedHandler;
#endif

    private IVpnService? _vpnService;
    private bool _isProtocolSubscribed;
    private bool? _isWideSettingsGrid;
    private bool _isUpdating;

    public ConfigurationView()
    {
        InitializeComponent();

        SyncSwitchesFromPreferences();
#if WINDOWS
        _windowActivatedHandler = () =>
        {
            if (IsVisible)
            {
                _ = SyncWindowsStartupStateAsync();
            }
        };
        App.WindowActivated += _windowActivatedHandler;
        _ = SyncWindowsStartupStateAsync();
#else
        RunOnStartupCard.IsVisible = false;
        RuAutoBypassCard.IsVisible = false;
        SyncTimeZoneCard.IsVisible = false;
#endif
        //карточка голосового помощника временно скрыта в ui до открытия сторонних appfunctions в google gemini
        VoiceAssistantCard.IsVisible = false;

        UpdateSelectionUi();
    }

    //привязка к оркестратору vpn для живой синхронизации протоколов и блокировки сетевых настроек
    public void Initialize(IVpnService vpnService)
    {
        _vpnService?.OnStateChanged -= OnVpnStateChanged;

        _vpnService = vpnService;
        _vpnService.OnStateChanged += OnVpnStateChanged;

        if (!_isProtocolSubscribed)
        {
            OctopusEngine.Current.OnProtocolChanged += OnProtocolChanged;
            _isProtocolSubscribed = true;
        }

        ApplyVpnLockState(_vpnService.CurrentState);
        UpdateSelectionUi();
    }

    private static Color CyanColor => Application.Current?.Resources.TryGetValue("Accent", out var a) == true && a is Color ac
        ? ac
        : Color.FromArgb("#00E5FF");

    private static Color EmeraldColor => Application.Current?.Resources.TryGetValue("Success", out var s) == true && s is Color sc
        ? sc
        : Color.FromArgb("#10B981");

    private static Color ErrorRedColor => Application.Current?.Resources.TryGetValue("Error", out var er) == true && er is Color erc
        ? erc
        : Color.FromArgb("#EF4444");

    //освобождение ресурсов
    public void Dispose()
    {
        _vpnService?.OnStateChanged -= OnVpnStateChanged;
        _vpnService = null;

        if (_isProtocolSubscribed)
        {
            OctopusEngine.Current.OnProtocolChanged -= OnProtocolChanged;
            _isProtocolSubscribed = false;
        }

#if WINDOWS
        if (_windowActivatedHandler is not null)
        {
            App.WindowActivated -= _windowActivatedHandler;
        }
#endif
        DisposeSpeedTest();
        GC.SuppressFinalize(this);
    }

    public event EventHandler? ThemesRequested;

    //единая адаптация экрана конфигурации по ssot-снимку геометрии окна без ручного widthrequest
    public void ApplyViewport(AdaptiveViewport viewport)
    {
        if (RootLayoutGrid is null || MainContentGrid is null)
        {
            return;
        }

        var isCompact = viewport.IsCompact;
        var baseSidePad = isCompact ? 16.0 : viewport.IsExpanded ? 28.0 : 20.0;
        var sidePad = viewport.ComputeHorizontalPadding(baseSidePad, 960.0);
        var topPad = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS
            ? Math.Max(viewport.SafeTop + 14.0, isCompact ? 42.0 : 24.0)
            : isCompact ? 12.0 : 16.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(116.0, viewport.SafeBottom + 98.0)
            : Math.Max(32.0, viewport.SafeBottom + 20.0);

        RootLayoutGrid.Padding = new Thickness(sidePad, 0, sidePad, 0);
        MainContentGrid.HorizontalOptions = LayoutOptions.Fill;
        MainContentGrid.Padding = new Thickness(0, topPad, 0, bottomPad);

        ApplySettingsGridColumns(viewport.UseMultiColumnContent);
    }

    //динамическая раскладка сетки настроек в 1 или 2 колонки по реальной ширине экрана
    private void ApplySettingsGridColumns(bool isWide)
    {
        if (SecuritySettingsGrid is null || _isWideSettingsGrid == isWide)
        {
            return;
        }

        _isWideSettingsGrid = isWide;
        SecuritySettingsGrid.ColumnDefinitions.Clear();
        if (isWide)
        {
            SecuritySettingsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SecuritySettingsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SecuritySettingsGrid.ColumnSpacing = 12;
            SecuritySettingsGrid.RowSpacing = 10;

            Grid.SetRow(AutoReconnectCard, 0);
            Grid.SetColumn(AutoReconnectCard, 0);
            Grid.SetColumnSpan(AutoReconnectCard, 1);

            Grid.SetRow(KillSwitchCard, 0);
            Grid.SetColumn(KillSwitchCard, 1);
            Grid.SetColumnSpan(KillSwitchCard, 1);

            Grid.SetRow(ForceCloudflareCard, 1);
            Grid.SetColumn(ForceCloudflareCard, 0);
            Grid.SetColumnSpan(ForceCloudflareCard, 1);

            Grid.SetRow(BlockIpv6Card, 1);
            Grid.SetColumn(BlockIpv6Card, 1);
            Grid.SetColumnSpan(BlockIpv6Card, 1);

            Grid.SetRow(RuAutoBypassCard, 2);
            Grid.SetColumn(RuAutoBypassCard, 0);
            Grid.SetColumnSpan(RuAutoBypassCard, 1);

            Grid.SetRow(SyncTimeZoneCard, 2);
            Grid.SetColumn(SyncTimeZoneCard, 1);
            Grid.SetColumnSpan(SyncTimeZoneCard, 1);

            Grid.SetRow(RunOnStartupCard, 3);
            Grid.SetColumn(RunOnStartupCard, 0);
            Grid.SetColumnSpan(RunOnStartupCard, 2);

            Grid.SetRow(VoiceAssistantCard, 4);
            Grid.SetColumn(VoiceAssistantCard, 0);
            Grid.SetColumnSpan(VoiceAssistantCard, 2);

            Grid.SetRow(SpeedTestCard, 5);
            Grid.SetColumn(SpeedTestCard, 0);
            Grid.SetColumnSpan(SpeedTestCard, 2);

            Grid.SetRow(ThemesSettingsCard, 6);
            Grid.SetColumn(ThemesSettingsCard, 0);
            Grid.SetColumnSpan(ThemesSettingsCard, 2);
        }
        else
        {
            SecuritySettingsGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            SecuritySettingsGrid.ColumnSpacing = 0;
            SecuritySettingsGrid.RowSpacing = 10;

            Border[] stacked =
            [
                AutoReconnectCard,
                KillSwitchCard,
                ForceCloudflareCard,
                BlockIpv6Card,
                RuAutoBypassCard,
                SyncTimeZoneCard,
                RunOnStartupCard,
                VoiceAssistantCard,
                SpeedTestCard,
                ThemesSettingsCard
            ];

            for (var i = 0; i < stacked.Length; i++)
            {
                Grid.SetRow(stacked[i], i);
                Grid.SetColumn(stacked[i], 0);
                Grid.SetColumnSpan(stacked[i], 1);
            }
        }
    }

    //синхронизация состояния тумблеров с хранилищем настроек
    private void SyncSwitchesFromPreferences()
    {
        _isUpdating = true;
        try
        {
            AutoReconnectToggle.IsToggled = Preferences.Get(AppPreferenceKeys.AutoReconnect, true);
            KillSwitchToggle.IsToggled = Preferences.Get(AppPreferenceKeys.KillSwitch, false);
            ForceCloudflareToggle.IsToggled = Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
            var blockIpv6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
            BlockIpv6Toggle.IsToggled = blockIpv6;
            OctopusEngine.Current.Pipeline.BlockIpv6Traffic = blockIpv6;
#if WINDOWS
            RunOnStartupToggle.IsToggled = Preferences.Get(AppPreferenceKeys.RunOnStartup, false);
            RuAutoBypassToggle.IsToggled = Preferences.Get(AppPreferenceKeys.BypassRuSites, true);
            SyncTimeZoneToggle.IsToggled = Preferences.Get(AppPreferenceKeys.SyncTimeZoneWithServer, false);
#elif ANDROID
            VoiceAssistantToggle.IsToggled = Preferences.Get(AppPreferenceKeys.VoiceAssistantEnabled, true);
#endif
        }
        finally
        {
            _isUpdating = false;
        }
    }

    //сохранение настройки авто подключения
    private void OnAutoReconnectToggled(object? sender, ToggledEventArgs e)
    {
        if (!_isUpdating)
        {
            Preferences.Set(AppPreferenceKeys.AutoReconnect, e.Value);
        }
    }

    //сохранение настройки аварийного отключения сети
    private void OnKillSwitchToggled(object? sender, ToggledEventArgs e)
    {
        if (!_isUpdating)
        {
            Preferences.Set(AppPreferenceKeys.KillSwitch, e.Value);
        }
    }

    //принудительное включение или отключение режима Cloudflare WebSocket
    private void OnForceCloudflareToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set(AppPreferenceKeys.ForceWebSocket, e.Value);
        UpdateSelectionUi();
    }

    //включение или отключение блокировки ipv6 трафика в конвейере и при следующем подключении
    private void OnBlockIpv6Toggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set(AppPreferenceKeys.BlockIpv6, e.Value);
        OctopusEngine.Current.Pipeline.BlockIpv6Traffic = e.Value;
        UpdateSelectionUi();
    }

    //включение или отключение авто-обхода российских доменов и подсетей на windows
    private void OnRuAutoBypassToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

#if WINDOWS
        Preferences.Set(AppPreferenceKeys.BypassRuSites, e.Value);
        AppManager.SyncPolicyFromPreferences();
#else
        Preferences.Set(AppPreferenceKeys.BypassRuSites, e.Value);
#endif
    }

    //переключение автозагрузки приложения с операционной системой
    private void OnRunOnStartupToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set(AppPreferenceKeys.RunOnStartup, e.Value);
#if WINDOWS
        _ = ApplyWindowsStartupAsync(e.Value);
#endif
    }

    //переключение синхронизации системного часового пояса со страной активного vpn-сервера
    private void OnSyncTimeZoneToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set(AppPreferenceKeys.SyncTimeZoneWithServer, e.Value);
#if WINDOWS
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            return;
        }

        if (e.Value)
        {
            if (_vpnService?.CurrentState == AppVpnState.Connected)
            {
                WindowsTimeZoneService.SyncToServerLocation(
                    OctopusEngine.Current.ActiveServerLocation,
                    OctopusEngine.Current.ActiveServerFlag);
            }
        }
        else
        {
            WindowsTimeZoneService.RestoreOriginalIfNeeded("отключение настройки пользователем");
        }
#endif
    }

    //реакция на смену состояния vpn-сессии
    private void OnVpnStateChanged(AppVpnState state) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            ApplyVpnLockState(state);
            UpdateSelectionUi();
        });

    //реакция на бесшовную смену активного транспортного протокола в ядре
    private void OnProtocolChanged() =>
        MainThread.BeginInvokeOnMainThread(UpdateSelectionUi);

    //блокировка сетевых переключателей, требующих пересоздания маршрутов или туннеля во время работы vpn
    private void ApplyVpnLockState(AppVpnState state)
    {
        var isLocked = state is AppVpnState.Connected or AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting;
        VpnActiveLockBanner.IsVisible = isLocked;
        var lockTip = isLocked ? "Отключите VPN для изменения" : null;

        SetLockedSettingRow(ForceCloudflareCard, ForceCloudflareToggle, isLocked, lockTip);
        SetLockedSettingRow(BlockIpv6Card, BlockIpv6Toggle, isLocked, lockTip);
#if WINDOWS
        SetLockedSettingRow(KillSwitchCard, KillSwitchToggle, isLocked, lockTip);
        SetLockedSettingRow(RuAutoBypassCard, RuAutoBypassToggle, isLocked, lockTip);
#endif
    }

    private static void SetLockedSettingRow(Border card, Microsoft.Maui.Controls.Switch toggle, bool isLocked, string? tooltip)
    {
        toggle.IsEnabled = !isLocked;
        card.Opacity = isLocked ? 0.58 : 1.0;
        if (tooltip is null)
        {
            card.ClearValue(ToolTipProperties.TextProperty);
        }
        else
        {
            ToolTipProperties.SetText(card, tooltip);
        }
    }

    //актуализация настроек при показе страницы
    public void OnAppearing()
    {
        SyncSwitchesFromPreferences();
#if WINDOWS
        _ = SyncWindowsStartupStateAsync();
#elif ANDROID
        RefreshVoiceAssistantStatus();
#endif
        if (_vpnService is not null)
        {
            ApplyVpnLockState(_vpnService.CurrentState);
        }

        UpdateSelectionUi();
    }

    //сохранение настройки управления vpn через голосовых ассистентов android
    private void OnVoiceAssistantToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set(AppPreferenceKeys.VoiceAssistantEnabled, e.Value);
#if ANDROID
        RefreshVoiceAssistantStatus();
#endif
    }

    //открытие системного экрана настройки голосового помощника android
    private void OnOpenAssistantSettingsTapped(object? sender, TappedEventArgs e)
    {
#if ANDROID
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            AndroidVoiceAssistantDetector.OpenAndroidAssistantSettings(Android.App.Application.Context);
        }
#endif
    }

#if ANDROID
    //автоопределение установленного и системного голосового помощника и обновление карточки подсказок
    private void RefreshVoiceAssistantStatus()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(30) || VoiceAssistantBadgeLabel is null)
        {
            return;
        }

        var detection = AndroidVoiceAssistantDetector.Detect(Android.App.Application.Context);
        var isVoiceEnabled = Preferences.Get(AppPreferenceKeys.VoiceAssistantEnabled, true);

        VoiceAssistantBadgeLabel.Text = detection.BadgeTitle;
        var accentColor = CyanColor;
        var successColor = EmeraldColor;
        var warningColor = Application.Current?.Resources.TryGetValue("Warning", out var w) == true && w is Color wc
            ? wc
            : Color.FromArgb("#F59E0B");

        switch (detection.Kind)
        {
            case DetectedVoiceAssistantKind.GoogleGemini:
            case DetectedVoiceAssistantKind.GoogleAssistant:
                VoiceAssistantBadgeBorder.Stroke = accentColor;
                VoiceAssistantBadgeBorder.BackgroundColor = accentColor.WithAlpha(0.14f);
                VoiceAssistantBadgeLabel.TextColor = accentColor;
                VoiceAssistantStatusLabel.Text = isVoiceEnabled
                    ? "Голосовое управление активно через Google Gemini."
                    : "Голосовое управление выключено в настройках Обходки.";
                VoiceAssistantHintLabel.Text =
                    "Вы можете управлять VPN голосом через Gemini. Скажите «Окей, Гугл» или «Хей, Гугл» и одну из команд:";
                VoiceAssistantCommandsLabel.Text =
                    "• «Включи VPN в Обходке» / «Подключи Обходку»\n" +
                    "• «Выключи VPN в Обходке» / «Отключи Обходку»\n" +
                    "• «Проверь статус Обходки»";
                VoiceAssistantCommandsPanel.IsVisible = true;
                OpenAssistantSettingsButton.IsVisible = false;
                break;

            case DetectedVoiceAssistantKind.SamsungBixby:
                VoiceAssistantBadgeBorder.Stroke = successColor;
                VoiceAssistantBadgeBorder.BackgroundColor = successColor.WithAlpha(0.14f);
                VoiceAssistantBadgeLabel.TextColor = successColor;
                VoiceAssistantStatusLabel.Text = isVoiceEnabled
                    ? "Голосовое управление активно через Samsung Bixby."
                    : "Голосовое управление выключено в настройках Обходки.";
                VoiceAssistantHintLabel.Text = detection.HasBixbyRoutines
                    ? "Скажите «Hi, Bixby» или добавьте действие Обходки в Bixby Routines:"
                    : "Скажите «Hi, Bixby» и одну из команд:";
                VoiceAssistantCommandsLabel.Text =
                    "• «Включи VPN в Обходке» / «Подключи Обходку»\n" +
                    "• «Выключи VPN в Обходке» / «Отключи Обходку»\n" +
                    "• «Проверь статус Обходки»\n" +
                    "• Bixby Routines: быстрый запуск ярлыков «Включить VPN» / «Выключить VPN»";
                VoiceAssistantCommandsPanel.IsVisible = true;
                OpenAssistantSettingsButton.IsVisible = false;
                break;

            default:
                VoiceAssistantBadgeBorder.Stroke = warningColor;
                VoiceAssistantBadgeBorder.BackgroundColor = warningColor.WithAlpha(0.14f);
                VoiceAssistantBadgeLabel.TextColor = warningColor;
                VoiceAssistantStatusLabel.Text = "На вашем устройстве не настроен голосовой помощник.";
                VoiceAssistantCommandsPanel.IsVisible = false;
                OpenAssistantSettingsButton.IsVisible = true;
                break;
        }
    }
#endif

    //каскадная анимация появления карточек
    public async Task PlayEntranceAnimationAsync()
    {
        OnAppearing();
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(30, 240);
    }

    //динамическая синхронизация карточки и чипов протоколов с реестром ядра ProtocolPipelineRegistry
    private void UpdateSelectionUi()
    {
        var factories = ProtocolPipelineRegistry.GetRegisteredFactories();
        var forceWs = Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
        var blockV6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
        var engine = OctopusEngine.Current;
        var vpnState = _vpnService?.CurrentState ?? (engine.IsConnected ? AppVpnState.Connected : AppVpnState.Disconnected);
        var isConnected = vpnState == AppVpnState.Connected || engine.IsConnected;
        var isNegotiating = vpnState is AppVpnState.Connecting or AppVpnState.Reconnecting;

        ITransportProtocolFactory? highlightedFactory = null;
        if ((isConnected || isNegotiating) &&
            ProtocolPipelineRegistry.TryFindFactoryByProtocolName(engine.ActiveProtocol, out var liveFactory))
        {
            highlightedFactory = liveFactory;
        }
        else if (forceWs && ProtocolPipelineRegistry.TryGetFactory(TransportProtocolKind.ObxodkaWebSocket, out var wsFactory))
        {
            highlightedFactory = wsFactory;
        }
        else if (factories.Count > 0)
        {
            highlightedFactory = factories[0];
        }

        var liveBadge = isConnected
            ? ProtocolPipelineRegistry.FormatLiveStatusBadge(engine.ActiveProtocol, engine.ActiveRays)
            : highlightedFactory is not null
                ? $"{highlightedFactory.FriendlyName} • {highlightedFactory.TransportSummary}"
                : "Obxodka Stream • HTTP/2";

        CurrentSelectionLabel.Text = blockV6 ? $"{liveBadge} • IPv4 Only" : liveBadge;

        var chainNames = string.Join(" → ", factories.Select(f => f.ShortTag));
        ProtocolSubtitleLabel.Text = forceWs
            ? $"Принудительный облачный режим через WebSocket CDN. Доступные транспорты ядра: {chainNames}."
            : $"Автоматический круговой конвейер транспортов: {chainNames}.";

        if (highlightedFactory is not null)
        {
            ActiveProtocolTitleLabel.Text = isConnected && highlightedFactory.Kind == TransportProtocolKind.ObxodkaStream
                ? $"{highlightedFactory.FriendlyName} ({highlightedFactory.TransportSummary} • {Math.Max(1, engine.ActiveRays)}L)"
                : $"{highlightedFactory.FriendlyName} ({highlightedFactory.TransportSummary})";
            ActiveProtocolDescLabel.Text = highlightedFactory.Description;
        }

        var successColor = EmeraldColor;
        var accentColor = CyanColor;
        var successDim = Application.Current?.Resources.TryGetValue("SuccessDim", out var sd) == true && sd is Color sdc
            ? sdc
            : successColor.WithAlpha(0.14f);
        var accentDim = Application.Current?.Resources.TryGetValue("AccentDim", out var ad) == true && ad is Color adc
            ? adc
            : accentColor.WithAlpha(0.14f);

        if (isConnected)
        {
            ActiveProtocolBadgeLabel.Text = "АКТИВЕН";
            ActiveProtocolBadgeLabel.TextColor = successColor;
            ActiveProtocolBadgeBorder.BackgroundColor = successDim;
            ActiveProtocolBadgeBorder.Stroke = successColor;
        }
        else if (isNegotiating)
        {
            ActiveProtocolBadgeLabel.Text = "ПОДБОР";
            ActiveProtocolBadgeLabel.TextColor = accentColor;
            ActiveProtocolBadgeBorder.BackgroundColor = accentDim;
            ActiveProtocolBadgeBorder.Stroke = accentColor;
        }
        else
        {
            ActiveProtocolBadgeLabel.Text = forceWs ? "ПРИНУДИТ." : "АВТО";
            ActiveProtocolBadgeLabel.TextColor = accentColor;
            ActiveProtocolBadgeBorder.BackgroundColor = accentDim;
            ActiveProtocolBadgeBorder.Stroke = accentColor;
        }

        RenderRegisteredProtocolPills(factories, highlightedFactory, isConnected);
    }

    //динамическая отрисовка списка всех зарегистрированных в ядре транспортов без фиксированной высоты (winui 3 safe)
    private void RenderRegisteredProtocolPills(
        IReadOnlyList<ITransportProtocolFactory> factories,
        ITransportProtocolFactory? highlightedFactory,
        bool isConnected)
    {
        if (RegisteredProtocolsLayout is null)
        {
            return;
        }

        RegisteredProtocolsLayout.Children.Clear();
        var bgElevated = Application.Current?.Resources.TryGetValue("BgElevated", out var bg) == true && bg is Color bgc
            ? bgc
            : Color.FromArgb("#202030");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
            ? bsc
            : Color.FromArgb("#282838");
        var textMuted = Application.Current?.Resources.TryGetValue("TextMuted", out var tm) == true && tm is Color tmc
            ? tmc
            : Color.FromArgb("#9CA3AF");
        var accent = isConnected ? EmeraldColor : CyanColor;
        var accentBg = accent.WithAlpha(0.16f);

        foreach (var factory in factories)
        {
            var isSelected = highlightedFactory is not null && factory.Kind == highlightedFactory.Kind;
            var labelText = isSelected && isConnected && factory.Kind == TransportProtocolKind.ObxodkaStream
                ? $"{factory.ShortTag} • {Math.Max(1, OctopusEngine.Current.ActiveRays)}L"
                : factory.ShortTag;

            var pillLabel = new Label
            {
                Text = labelText,
                FontSize = 11,
                TextColor = isSelected ? accent : textMuted,
                VerticalOptions = LayoutOptions.Center
            };
            pillLabel.SetDynamicResource(Label.FontFamilyProperty, isSelected ? "AppFontBold" : "AppFontMedium");

            var pill = new Border
            {
                Margin = new Thickness(0, 0, 6, 4),
                Padding = new Thickness(9, 4),
                BackgroundColor = isSelected ? accentBg : bgElevated,
                Stroke = isSelected ? accent : borderSubtle,
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(8) },
                VerticalOptions = LayoutOptions.Center,
                Content = pillLabel
            };
            ToolTipProperties.SetText(pill, $"{factory.FriendlyName} ({factory.TransportSummary}) — {factory.Description}");
            RegisteredProtocolsLayout.Children.Add(pill);
        }
    }

    //применение цветов фона и границ карточек настроек
    public void UpdateCardOpacity()
    {
        var bgSurface = Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor
            ? bgColor
            : Color.FromArgb("#161622");
        var borderSubtle = Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc
            ? bsc
            : Color.FromArgb("#282838");

        ProtocolInfoCard.BackgroundColor = bgSurface;
        ProtocolInfoCard.Stroke = borderSubtle;
        AutoReconnectCard.BackgroundColor = bgSurface;
        AutoReconnectCard.Stroke = borderSubtle;
        KillSwitchCard.BackgroundColor = bgSurface;
        KillSwitchCard.Stroke = borderSubtle;
        ForceCloudflareCard.BackgroundColor = bgSurface;
        ForceCloudflareCard.Stroke = borderSubtle;
        BlockIpv6Card.BackgroundColor = bgSurface;
        BlockIpv6Card.Stroke = borderSubtle;
        RuAutoBypassCard.BackgroundColor = bgSurface;
        RuAutoBypassCard.Stroke = borderSubtle;
        SyncTimeZoneCard.BackgroundColor = bgSurface;
        SyncTimeZoneCard.Stroke = borderSubtle;
        RunOnStartupCard.BackgroundColor = bgSurface;
        RunOnStartupCard.Stroke = borderSubtle;
        SpeedTestCard.BackgroundColor = bgSurface;
        SpeedTestCard.Stroke = borderSubtle;
        ThemesSettingsCard.BackgroundColor = bgSurface;
        ThemesSettingsCard.Stroke = borderSubtle;
    }

    //реакция на смену темы оформления
    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateSelectionUi();
        });

    //переход в магазин тем оформления
    private void OnThemesSettingsTapped(object? sender, EventArgs e)
    {
        _ = ThemesSettingsCard?.BounceClickAsync();
        ThemesRequested?.Invoke(this, EventArgs.Empty);
    }

#if WINDOWS
    //синхронизация состояния тумблера автозапуска
    private async Task SyncWindowsStartupStateAsync()
    {
        var isEnabled = await WindowsStartupManager.CheckEnabledAsync();
        ApplyStartupToggleState(isEnabled);
    }

    //применение и отображение фактического состояния автозапуска
    private async Task ApplyWindowsStartupAsync(bool enable)
    {
        var actualState = await WindowsStartupManager.SetEnabledAsync(enable);
        ApplyStartupToggleState(actualState);
    }

    //обновление состояния тумблера автозапуска в ui-потоке
    private void ApplyStartupToggleState(bool isEnabled) =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            _isUpdating = true;
            try
            {
                RunOnStartupToggle.IsToggled = isEnabled;
            }
            finally
            {
                _isUpdating = false;
            }
        });
#endif
}
