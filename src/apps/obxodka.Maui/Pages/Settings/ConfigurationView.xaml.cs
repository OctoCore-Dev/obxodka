namespace obxodka.Pages.Settings;

//экран системных настроек, автозапуска, kill switch и замера скорости
public sealed partial class ConfigurationView : IDisposable, IAdaptivePage
{
#if WINDOWS
    private readonly Action? _windowActivatedHandler;
#endif

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
#endif

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
            : isCompact ? 14.0 : 20.0;
        var bottomPad = !viewport.UseSidebar
            ? Math.Max(110.0, viewport.SafeBottom + 96.0)
            : Math.Max(28.0, viewport.SafeBottom + 16.0);

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
            Grid.SetColumnSpan(RuAutoBypassCard, 2);

            Grid.SetRow(RunOnStartupCard, 3);
            Grid.SetColumn(RunOnStartupCard, 0);
            Grid.SetColumnSpan(RunOnStartupCard, 2);

            Grid.SetRow(SpeedTestCard, 4);
            Grid.SetColumn(SpeedTestCard, 0);
            Grid.SetColumnSpan(SpeedTestCard, 2);

            Grid.SetRow(ThemesSettingsCard, 5);
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
                RunOnStartupCard,
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

    //актуализация настроек при показе страницы
    public void OnAppearing()
    {
        SyncSwitchesFromPreferences();
#if WINDOWS
        _ = SyncWindowsStartupStateAsync();
#endif
        UpdateSelectionUi();
    }

    //каскадная анимация появления карточек
    public async Task PlayEntranceAnimationAsync()
    {
        OnAppearing();
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(30, 240);
    }

    //отображение активного сетевого протокола и режима стека ip
    private void UpdateSelectionUi()
    {
        var forceWs = Preferences.Get(AppPreferenceKeys.ForceWebSocket, false);
        var blockV6 = Preferences.Get(AppPreferenceKeys.BlockIpv6, false);
        var proto = forceWs
            ? "Cloudflare WebSocket • 443"
            : OctopusEngine.Current.IsConnected
                ? OctopusEngine.Current.ActiveProtocolDisplayName
                : "obxodka Stream • HTTP/2";
        CurrentSelectionLabel.Text = blockV6 ? $"{proto} • IPv4 Only" : proto;
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
            RunOnStartupToggle.IsToggled = isEnabled;
            _isUpdating = false;
        });
#endif
}
