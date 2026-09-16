namespace obxodka.Views;

public sealed partial class ConfigurationView : ContentView, IDisposable
{
    private static Color InactiveStroke => (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bs) == true && bs is Color bsc)
        ? bsc
        : Color.FromArgb("#1AFFFFFF");
    private static Color ActiveStroke => (Application.Current?.Resources.TryGetValue("Primary", out var p) == true && p is Color pc)
        ? pc
        : Color.FromArgb("#0078D4");
    private static Color PurpleStroke => (Application.Current?.Resources.TryGetValue("Purple", out var p) == true && p is Color pc)
        ? pc
        : Color.FromArgb("#A855F7");
    private static Color CyanColor => (Application.Current?.Resources.TryGetValue("Accent", out var a) == true && a is Color ac)
        ? ac
        : Color.FromArgb("#00E5FF");
    private static Color EmeraldColor => (Application.Current?.Resources.TryGetValue("Success", out var s) == true && s is Color sc)
        ? sc
        : Color.FromArgb("#10B981");
    private static Color ErrorRedColor => (Application.Current?.Resources.TryGetValue("Error", out var er) == true && er is Color erc)
        ? erc
        : Color.FromArgb("#EF4444");

    private static readonly string[] t_pingEndpoints =
    [
        "https://ya.ru/favicon.ico",
        "https://1.1.1.1/",
        "https://www.google.com/generate_204"
    ];

    private static readonly string[] t_downloadCandidates =
    [
        "https://speedtest.selectel.ru/100MB",
        "https://mirror.yandex.ru/debian/ls-lR.gz",
        "https://speed.cloudflare.com/__down?bytes=50000000",
        "https://proof.ovh.net/files/100Mb.dat"
    ];

    private int _currentMode = 2;
    private string _protocolMode = "AUTO";
    private bool _isUpdating;
    private bool _isTestingSpeed;
    private CancellationTokenSource? _speedTestCts;
#if WINDOWS
    private readonly Action? _windowActivatedHandler;
#endif

    public ConfigurationView()
    {
        InitializeComponent();

        var defaultRays = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 2 : 8;
        _currentMode = Preferences.Get("BatteryMode", defaultRays);
        _protocolMode = Preferences.Get("ProtocolMode", "AUTO");

        AutoReconnectToggle.IsToggled = Preferences.Get("AutoReconnect", true);
        KillSwitchToggle.IsToggled = Preferences.Get("KillSwitch", false);
        QuickProtocolSwitchToggle.IsToggled = Preferences.Get("QuickProtocolSwitch", true);
#if WINDOWS
        RunOnStartupToggle.IsToggled = Preferences.Get("RunOnStartup", false);
        _windowActivatedHandler = () =>
        {
            if (IsVisible)
            {
                _ = SyncWindowsStartupStateAsync();
            }
        };
        App.WindowActivated += _windowActivatedHandler;
        _ = SyncWindowsStartupStateAsync();
#endif

        UpdateSelectionUI(_currentMode, _protocolMode);
        UpdateLockState();

        ConfigScrollView.SizeChanged += (s, e) =>
        {
            if (ConfigScrollView.Width > 0)
            {
                ApplyCardWidth(ConfigScrollView.Width);
            }
        };
    }

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
        if (width > 0 && RootLayoutGrid is not null)
        {
            var availableWidth = width - RootLayoutGrid.Padding.HorizontalThickness;
            if (availableWidth > 0)
            {
                ApplyCardWidth(availableWidth);
            }
        }
    }

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

    private void OnAutoReconnectToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Set("AutoReconnect", e.Value);

    private void OnKillSwitchToggled(object? sender, ToggledEventArgs e) =>
        Preferences.Set("KillSwitch", e.Value);

    private void OnQuickProtocolSwitchToggled(object? sender, ToggledEventArgs e)
    {
        Preferences.Set("QuickProtocolSwitch", e.Value);
        UpdateLockState();
    }

    private void OnRunOnStartupToggled(object? sender, ToggledEventArgs e)
    {
        if (_isUpdating)
        {
            return;
        }

        Preferences.Set("RunOnStartup", e.Value);
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            _ = SetWindowsStartupTaskAsync(e.Value);
        }
#endif
    }

#if WINDOWS
    private static bool HasPackageIdentity()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return false;
        }

        try
        {
            _ = Windows.ApplicationModel.Package.Current.Id;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> CheckWindowsStartupStateAsync()
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && HasPackageIdentity())
            {
                var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("ObxodkaStartup");
                return startupTask.State is Windows.ApplicationModel.StartupTaskState.Enabled
                                       or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
            }
            else if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
                return key?.GetValue("Obxodka") != null;
            }

            return Preferences.Get("RunOnStartup", false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STARTUP] Check failed: {ex.Message}");
            return Preferences.Get("RunOnStartup", false);
        }
    }

    private async Task SyncWindowsStartupStateAsync()
    {
        try
        {
            var isEnabled = await CheckWindowsStartupStateAsync();
            Preferences.Set("RunOnStartup", isEnabled);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                _isUpdating = true;
                RunOnStartupToggle.IsToggled = isEnabled;
                _isUpdating = false;
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STARTUP SYNC ERROR] {ex.Message}");
        }
    }

    private async Task SetWindowsStartupTaskAsync(bool enable)
    {
        try
        {
            _ = Task.Run(() =>
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        using var proc = Process.Start(new ProcessStartInfo("schtasks", "/delete /tn \"ObxodkaVpnStartup\" /f")
                        {
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        });
                        proc?.WaitForExit();
                    }
                }
                catch { }
            });

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && HasPackageIdentity())
            {
                var startupTask = await Windows.ApplicationModel.StartupTask.GetAsync("ObxodkaStartup");
                if (enable)
                {
                    if (startupTask.State == Windows.ApplicationModel.StartupTaskState.DisabledByUser)
                    {
                        _ = await Launcher.OpenAsync(new Uri("ms-settings:startupapps"));

                        await Task.Delay(1000);
                        var recheck = await Windows.ApplicationModel.StartupTask.GetAsync("ObxodkaStartup");
                        var isEnabled = recheck.State is Windows.ApplicationModel.StartupTaskState.Enabled
                                                      or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                        Preferences.Set("RunOnStartup", isEnabled);
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            _isUpdating = true;
                            RunOnStartupToggle.IsToggled = isEnabled;
                            _isUpdating = false;
                        });
                        return;
                    }

                    var state = await startupTask.RequestEnableAsync();
                    var success = state is Windows.ApplicationModel.StartupTaskState.Enabled
                                        or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
                    Preferences.Set("RunOnStartup", success);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _isUpdating = true;
                        RunOnStartupToggle.IsToggled = success;
                        _isUpdating = false;
                    });
                }
                else
                {
                    if (startupTask.State is Windows.ApplicationModel.StartupTaskState.Enabled
                                          or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy)
                    {
                        startupTask.Disable();
                    }
                    Preferences.Set("RunOnStartup", false);
                    MainThread.BeginInvokeOnMainThread(() =>
                    {
                        _isUpdating = true;
                        RunOnStartupToggle.IsToggled = false;
                        _isUpdating = false;
                    });
                }
            }
            else
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    if (enable)
                    {
                        var exePath = Environment.ProcessPath;
                        if (!string.IsNullOrEmpty(exePath))
                        {
                            key.SetValue("Obxodka", $"\"{exePath}\"");
                            Preferences.Set("RunOnStartup", true);
                        }
                    }
                    else
                    {
                        key.DeleteValue("Obxodka", false);
                        Preferences.Set("RunOnStartup", false);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[STARTUP ERROR] {ex.Message}");
        }
    }
#endif

    public void OnAppearing()
    {
        var defaultRays = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 2 : 8;
        _currentMode = Preferences.Get("BatteryMode", defaultRays);
        _protocolMode = Preferences.Get("ProtocolMode", "AUTO");

        AutoReconnectToggle.IsToggled = Preferences.Get("AutoReconnect", true);
        KillSwitchToggle.IsToggled = Preferences.Get("KillSwitch", false);
        QuickProtocolSwitchToggle.IsToggled = Preferences.Get("QuickProtocolSwitch", true);
#if WINDOWS
        _ = SyncWindowsStartupStateAsync();
#endif

        UpdateSelectionUI(_currentMode, _protocolMode);
        UpdateLockState();
    }

    public async Task PlayEntranceAnimationAsync()
    {
        OnAppearing();
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(30, 240);
    }

    private void UpdateLockState()
    {
        var isVpnRunning = OctopusEngine.Current is { IsConnected: true };
        var isFechsue = _protocolMode == "FECHSUE";
        var isHotSwap = Preferences.Get("QuickProtocolSwitch", true);

        if (isVpnRunning && !isHotSwap)
        {
            LockWarningLabel.IsVisible = true;
            LockWarningLabel.Text = "Отключите VPN, чтобы изменить";
            LockWarningLabel.TextColor = ErrorRedColor;
        }
        else if (isVpnRunning && isHotSwap)
        {
            LockWarningLabel.IsVisible = true;
            LockWarningLabel.Text = "Горячая смена (Hot-Swap) активна";
            LockWarningLabel.TextColor = CyanColor;
        }
        else if (isFechsue)
        {
            LockWarningLabel.IsVisible = true;
            LockWarningLabel.Text = "FECHSUE работает на 1 супер-потоке";
            LockWarningLabel.TextColor = PurpleStroke;
        }
        else
        {
            LockWarningLabel.IsVisible = false;
        }

        var raysEnabled = !isVpnRunning && !isFechsue;
        EcoButton.IsEnabled = raysEnabled;
        BalancedButton.IsEnabled = raysEnabled;
        TurboButton.IsEnabled = raysEnabled;

        var protoEnabled = !isVpnRunning || isHotSwap;
        AutoButton.IsEnabled = protoEnabled;
        Http2Button.IsEnabled = protoEnabled;
        Http3Button.IsEnabled = protoEnabled;
        FechsueButton.IsEnabled = protoEnabled;

        var rayOpacity = isVpnRunning ? 0.5 : (isFechsue ? 0.35 : 1.0);
        EcoButton.Opacity = rayOpacity;
        BalancedButton.Opacity = rayOpacity;
        TurboButton.Opacity = rayOpacity;

        var protoOpacity = protoEnabled ? 1.0 : 0.5;
        AutoButton.Opacity = protoOpacity;
        Http2Button.Opacity = protoOpacity;
        Http3Button.Opacity = protoOpacity;
        FechsueButton.Opacity = protoOpacity;
    }

    private void OnEcoTapped(object? sender, TappedEventArgs e)
    {
        if (EcoButton.IsEnabled)
        {
            _ = UIAnimations.PlayIconSpringHoverAsync(EcoIcon, 1.25);
            SetMode(1);
        }
    }

    private void OnBalancedTapped(object? sender, TappedEventArgs e)
    {
        if (BalancedButton.IsEnabled)
        {
            _ = UIAnimations.PlayIconWiggleAsync(BalancedIcon, 12);
            SetMode(2);
        }
    }

    private void OnTurboTapped(object? sender, TappedEventArgs e)
    {
        if (TurboButton.IsEnabled)
        {
            _ = UIAnimations.PlayIconSpringHoverAsync(TurboIcon, 1.3);
            SetMode(8);
        }
    }

    private void OnAutoTapped(object? sender, TappedEventArgs e)
    {
        if (AutoButton.IsEnabled)
        {
            _ = UIAnimations.PlayIconSpringHoverAsync(AutoIcon, 1.25);
            SetProtocol("AUTO");
        }
    }

    private void OnHttp2Tapped(object? sender, TappedEventArgs e)
    {
        if (Http2Button.IsEnabled)
        {
            _ = UIAnimations.PlayIconSpinAsync(Http2Icon, 180);
            SetProtocol("HTTP2");
        }
    }

    private void OnHttp3Tapped(object? sender, TappedEventArgs e)
    {
        if (Http3Button.IsEnabled)
        {
            _ = UIAnimations.PlayIconPulseAsync(Http3Icon, 1.25);
            SetProtocol("HTTP3");
        }
    }

    private void OnFechsueTapped(object? sender, TappedEventArgs e)
    {
        if (FechsueButton.IsEnabled)
        {
            _ = UIAnimations.PlayIconSpringHoverAsync(FechsueIcon, 1.25);
            SetProtocol("FECHSUE");
        }
    }

    private void SetMode(int rays)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        _currentMode = rays;
        Preferences.Set("BatteryMode", rays);
        UpdateSelectionUI(_currentMode, _protocolMode);
        _isUpdating = false;
    }

    private void SetProtocol(string protocol)
    {
        if (_isUpdating)
        {
            return;
        }

        _isUpdating = true;
        _protocolMode = protocol;
        Preferences.Set("ProtocolMode", protocol);
        Preferences.Set("UseHttp3", protocol == "HTTP3");
        UpdateSelectionUI(_currentMode, _protocolMode);
        UpdateLockState();

        if (OctopusEngine.Current is { IsConnected: true } && Preferences.Get("QuickProtocolSwitch", true))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var (success, servers, _) = await new ApiService(new HttpClient()).GetServersAsync();
                    if (success && servers is { Count: > 0 })
                    {
                        await OctopusEngine.Current.ReconnectAsync(servers[0].Ip, servers[0].Port);
                        MainThread.BeginInvokeOnMainThread(UpdateLockState);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HOT SWAP ERROR] {ex.Message}");
                }
            });
        }

        _isUpdating = false;
    }

    private void UpdateSelectionUI(int mode, string protocol)
    {
        AutoButton.Stroke = InactiveStroke;
        EcoButton.Stroke = InactiveStroke;
        BalancedButton.Stroke = InactiveStroke;
        TurboButton.Stroke = InactiveStroke;
        Http2Button.Stroke = InactiveStroke;
        Http3Button.Stroke = InactiveStroke;
        FechsueButton.Stroke = InactiveStroke;

        var raysText = protocol == "FECHSUE"
            ? "1 Супер-Луч"
            : mode switch
            {
                1 => "1 Луч",
                2 => "2 Луча",
                8 => "8 Лучей",
                _ => $"{mode} Луч."
            };

        if (protocol != "FECHSUE")
        {
            if (mode == 1)
            {
                EcoButton.Stroke = ActiveStroke;
            }
            else if (mode == 2)
            {
                BalancedButton.Stroke = ActiveStroke;
            }
            else if (mode == 8)
            {
                TurboButton.Stroke = ActiveStroke;
            }
        }

        var protocolText = protocol switch
        {
            "AUTO" => "AUTO (Умный)",
            "FECHSUE" => "FHARCSUE (Мульти-пинговый Watchdog)",
            "HTTP3" => "HTTP/3 QUIC",
            _ => "HTTP/2 TCP"
        };

        if (protocol == "AUTO")
        {
            AutoButton.Stroke = CyanColor;
        }
        else if (protocol == "FECHSUE")
        {
            FechsueButton.Stroke = PurpleStroke;
        }
        else if (protocol == "HTTP3")
        {
            Http3Button.Stroke = ActiveStroke;
        }
        else
        {
            Http2Button.Stroke = ActiveStroke;
        }

        CurrentSelectionLabel.Text = $"[ {raysText} / {protocolText} ]";
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = (Application.Current?.Resources.TryGetValue("BgSurface", out var bg) == true && bg is Color bgColor)
            ? bgColor
            : Color.FromArgb("#161622");

        EcoButton.BackgroundColor = bgSurface;
        BalancedButton.BackgroundColor = bgSurface;
        TurboButton.BackgroundColor = bgSurface;
        AutoButton.BackgroundColor = bgSurface;
        Http2Button.BackgroundColor = bgSurface;
        Http3Button.BackgroundColor = bgSurface;
        FechsueButton.BackgroundColor = bgSurface;
        AutoReconnectCard.BackgroundColor = bgSurface;
        KillSwitchCard.BackgroundColor = bgSurface;
        QuickProtocolSwitchCard.BackgroundColor = bgSurface;
        RunOnStartupCard.BackgroundColor = bgSurface;
        SpeedTestCard.BackgroundColor = bgSurface;
        ThemesSettingsCard.BackgroundColor = bgSurface;
    }

    public void SetHeaderTopInset(double top)
    {
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayoutGrid != null)
        {
            RootLayoutGrid.Padding = new Thickness(16, Math.Max(top + 4, 12), 16, 0);
        }
    }

    public void OnThemeChanged() =>
        MainThread.BeginInvokeOnMainThread(() =>
        {
            UpdateCardOpacity();
            UpdateSelectionUI(_currentMode, _protocolMode);
        });

    private async void OnRunSpeedTestClickedAsync(object? sender, EventArgs e)
    {
        if (sender is VisualElement btn)
        {
            _ = btn.BounceClickAsync();
        }

        if (_isTestingSpeed)
        {
            _speedTestCts?.Cancel();
            return;
        }

        _isTestingSpeed = true;
        _speedTestCts = new CancellationTokenSource();
        var token = _speedTestCts.Token;

        SpeedTestBtnText.Text = "СТОП";
        SpeedTestResultLabel.Text = "Подключение и замер пинга...";
        SpeedTestResultLabel.TextColor = CyanColor;

        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested && _isTestingSpeed)
            {
                MainThread.BeginInvokeOnMainThread(() => SpeedGaugeIcon?.Rotation = (SpeedGaugeIcon.Rotation + 16) % 360);
                try
                {
                    await Task.Delay(25, token);
                }
                catch
                {
                    break;
                }
            }

            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (SpeedGaugeIcon is not null)
                {
                    _ = SpeedGaugeIcon.RotateToAsync(0, 250, Easing.CubicOut);
                }
            });
        }, token);

        try
        {
            await Task.Run(async () =>
            {
                using var handler = new SocketsHttpHandler
                {
                    PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                    EnableMultipleHttp2Connections = true
                };
                using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");

                long pingMs = 0;
                foreach (var endpoint in t_pingEndpoints)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        var pingSw = Stopwatch.StartNew();
                        using var req = new HttpRequestMessage(HttpMethod.Head, endpoint);
                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
                        pingSw.Stop();
                        if (resp.IsSuccessStatusCode || (int)resp.StatusCode < 500)
                        {
                            pingMs = pingMs == 0 ? pingSw.ElapsedMilliseconds : Math.Min(pingMs, pingSw.ElapsedMilliseconds);
                        }
                    }
                    catch when (!token.IsCancellationRequested) { }
                }

                if (token.IsCancellationRequested)
                {
                    return;
                }

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var pingText = pingMs > 0 ? $"{pingMs} ms • " : "";
                    SpeedTestResultLabel.Text = $"{pingText}Запуск тестирования скорости...";
                });

                string? activeUrl = null;
                foreach (var candidate in t_downloadCandidates)
                {
                    if (token.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        using var probeReq = new HttpRequestMessage(HttpMethod.Head, candidate);
                        using var probeResp = await client.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, token);
                        if (probeResp.IsSuccessStatusCode)
                        {
                            activeUrl = candidate;
                            break;
                        }
                    }
                    catch when (!token.IsCancellationRequested) { }
                }

                activeUrl ??= "https://speedtest.selectel.ru/100MB";

                const double targetDurationSeconds = 10.0;
                var overallSw = Stopwatch.StartNew();
                var windowSw = Stopwatch.StartNew();

                long totalBytes = 0;
                long windowBytes = 0;
                double peakSpeedMBs = 0;
                double avgSpeedMBs = 0;

                const int workerCount = 2;
                List<Task> workerTasks = [];

                for (var i = 0; i < workerCount; i++)
                {
                    workerTasks.Add(Task.Run(async () =>
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent(131072);
                        try
                        {
                            while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
                            {
                                try
                                {
                                    using var streamReq = new HttpRequestMessage(HttpMethod.Get, activeUrl);
                                    using var resp = await client.SendAsync(streamReq, HttpCompletionOption.ResponseHeadersRead, token);
                                    if (!resp.IsSuccessStatusCode)
                                    {
                                        break;
                                    }

                                    using var stream = await resp.Content.ReadAsStreamAsync(token);
                                    while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
                                    {
                                        var read = await stream.ReadAsync(buffer.AsMemory(0, 131072), token);
                                        if (read <= 0)
                                        {
                                            break;
                                        }

                                        _ = Interlocked.Add(ref totalBytes, read);
                                        _ = Interlocked.Add(ref windowBytes, read);
                                    }
                                }
                                catch when (!token.IsCancellationRequested)
                                {
                                    break;
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }, token));
                }

                while (!token.IsCancellationRequested && overallSw.Elapsed.TotalSeconds < targetDurationSeconds)
                {
                    await Task.Delay(200, token);

                    if (windowSw.ElapsedMilliseconds >= 300)
                    {
                        var windowSec = Math.Max(windowSw.Elapsed.TotalSeconds, 0.01);
                        var currentWBytes = Interlocked.Exchange(ref windowBytes, 0);
                        var currentInstantMBs = currentWBytes / (1024.0 * 1024.0) / windowSec;
                        var currentMbps = currentInstantMBs * 8.0;

                        peakSpeedMBs = Math.Max(peakSpeedMBs, currentInstantMBs);
                        var elapsedSec = Math.Max(overallSw.Elapsed.TotalSeconds, 0.05);
                        avgSpeedMBs = Interlocked.Read(ref totalBytes) / (1024.0 * 1024.0) / elapsedSec;

                        var remaining = (int)Math.Max(targetDurationSeconds - overallSw.Elapsed.TotalSeconds, 0);
                        windowSw.Restart();

                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            SpeedTestResultLabel.Text = $"{currentMbps:F1} Мбит/с ({currentInstantMBs:F1} МБ/с) [{remaining}с]";
                            SpeedTestResultLabel.TextColor = CyanColor;
                        });
                    }
                }

                await Task.WhenAll(workerTasks);

                overallSw.Stop();
                var totalSec = Math.Max(overallSw.Elapsed.TotalSeconds, 0.1);
                avgSpeedMBs = Interlocked.Read(ref totalBytes) / (1024.0 * 1024.0) / totalSec;
                if (peakSpeedMBs < avgSpeedMBs)
                {
                    peakSpeedMBs = avgSpeedMBs;
                }

                var avgMbps = avgSpeedMBs * 8.0;
                var peakMbps = peakSpeedMBs * 8.0;

                MainThread.BeginInvokeOnMainThread(() =>
                {
                    var pingText = pingMs > 0 ? $" • {pingMs} ms" : "";
                    SpeedTestResultLabel.Text = $"{avgMbps:F1} Мбит/с ({avgSpeedMBs:F1} МБ/с){pingText} (Пик: {peakMbps:F1} Мбит/с)";
                    SpeedTestResultLabel.TextColor = EmeraldColor;
                });
            }, token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SPEED TEST] {ex.Message}");
            MainThread.BeginInvokeOnMainThread(() =>
            {
                SpeedTestResultLabel.Text = "Сбой замера (проверьте сеть)";
                SpeedTestResultLabel.TextColor = ErrorRedColor;
            });
        }
        finally
        {
            _isTestingSpeed = false;
            _speedTestCts?.Dispose();
            _speedTestCts = null;

            MainThread.BeginInvokeOnMainThread(() => SpeedTestBtnText.Text = "ТЕСТ");
        }
    }

    public event EventHandler? ThemesRequested;

    private void OnThemesSettingsTapped(object? sender, EventArgs e)
    {
        if (ThemesSettingsCard is not null)
        {
            _ = ThemesSettingsCard.BounceClickAsync();
        }

        ThemesRequested?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
#if WINDOWS
        if (_windowActivatedHandler != null)
        {
            App.WindowActivated -= _windowActivatedHandler;
        }
#endif
        _speedTestCts?.Cancel();
        _speedTestCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
