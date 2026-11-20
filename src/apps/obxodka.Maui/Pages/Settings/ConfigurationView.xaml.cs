namespace obxodka.Pages.Settings;

//экран системных настроек, автозапуска, kill switch и замера скорости
public sealed partial class ConfigurationView : IDisposable
{
#if WINDOWS
    private readonly Action? _windowActivatedHandler;
#endif

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

        ConfigScrollView.SizeChanged += (_, _) =>
        {
            if (ConfigScrollView.Width > 0)
            {
                ApplyCardWidth(ConfigScrollView.Width);
            }
        };
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

    //расчет ширины для перестроения интерфейса
    public void ForceLayoutWidth()
    {
        if (ConfigScrollView.Width > 0)
        {
            ApplyCardWidth(ConfigScrollView.Width);
        }
        else if (Width > 0 && RootLayoutGrid is not null)
        {
            var avail = Width - RootLayoutGrid.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (!(width > 0) || RootLayoutGrid is null)
        {
            return;
        }

        var availableWidth = width - RootLayoutGrid.Padding.HorizontalThickness;
        if (availableWidth > 0)
        {
            ApplyCardWidth(availableWidth);
        }
    }

    //подгонка ширины карточек с ограничением максимума
    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || MainContentGrid is null)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        MainContentGrid.WidthRequest = safeWidth;
        MainContentGrid.MaximumWidthRequest = safeWidth;
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
        CurrentSelectionLabel.Text = blockV6 ? $"[ {proto} • IPv4 Only ]" : $"[ {proto} ]";
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

    //отступ под системный статусбар для мобильных устройств
    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Phone || RootLayoutGrid is null)
        {
            return;
        }

        if (DeviceInfo.Platform == DevicePlatform.Android)
        {
            RootLayoutGrid.Padding = new Thickness(16, 0, 16, 0);
            if (MainContentGrid is null)
            {
                return;
            }

            var topAir = Math.Max(top + 16, 50);
            MainContentGrid.Padding = new Thickness(0, topAir, 0, 140);
        }
        else
        {
            RootLayoutGrid.Padding = new Thickness(16, Math.Max(top + 4, 12), 16, 0);
        }
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
