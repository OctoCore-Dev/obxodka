namespace obxodka.Views;

public sealed partial class ConfigurationView : ContentView, IDisposable
{
    private static readonly Color t_inactiveStroke = Color.FromArgb("#1AFFFFFF");
    private static readonly Color t_activeStroke = Color.FromArgb("#0078D4");
    private static readonly Color t_purpleStroke = Color.FromArgb("#A855F7");
    private static readonly Color t_cyanColor = Color.FromArgb("#00E5FF");
    private static readonly Color t_emeraldColor = Color.FromArgb("#10B981");
    private static readonly Color t_errorRedColor = Color.FromArgb("#EF4444");

    private static readonly string[] t_pingEndpoints =
    [
        "https://ya.ru/favicon.ico",
        "https://1.1.1.1/",
        "https://www.google.com/generate_204",
        "https://obxodka.one/favicon.ico"
    ];

    private static readonly string[] t_downloadCandidates =
    [
        "https://speedtest.selectel.ru/100MB",
        "https://mirror.yandex.ru/debian/ls-lR.gz",
        "https://speed.cloudflare.com/__down?bytes=50000000",
        "https://proof.ovh.net/files/100Mb.dat"
    ];

    private int _currentMode = 2;
    private string _protocolMode = "FECHSUE";
    private bool _isUpdating;
    private bool _isTestingSpeed;
    private CancellationTokenSource? _speedTestCts;

    public ConfigurationView()
    {
        InitializeComponent();

        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            var content = Content;
            Content = new ScrollView
            {
                Orientation = ScrollOrientation.Vertical,
                VerticalScrollBarVisibility = ScrollBarVisibility.Never,
                Content = content
            };
        }

        _currentMode = Preferences.Get("BatteryMode", 2);
        _protocolMode = Preferences.Get("ProtocolMode", "AUTO");

        AutoReconnectToggle.IsToggled = Preferences.Get("AutoReconnect", true);
        KillSwitchToggle.IsToggled = Preferences.Get("KillSwitch", false);
        QuickProtocolSwitchToggle.IsToggled = Preferences.Get("QuickProtocolSwitch", true);
#if WINDOWS
        RunOnStartupToggle.IsToggled = Preferences.Get("RunOnStartup", false);
#endif

        UpdateSelectionUI(_currentMode, _protocolMode);
        UpdateLockState();
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
        Preferences.Set("RunOnStartup", e.Value);
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            SetWindowsStartupTask(e.Value);
        }
#endif
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static void SetWindowsStartupTask(bool enable)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var args = enable
                ? $"/create /tn \"ObxodkaVpnStartup\" /tr \"\\\"{exePath}\\\" --hidden\" /sc onlogon /rl highest /f"
                : "/delete /tn \"ObxodkaVpnStartup\" /f";

            var psi = new ProcessStartInfo("schtasks", args)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                Verb = "runas"
            };

            Process.Start(psi)?.WaitForExit();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to change scheduled task: {ex.Message}");
        }
    }
#endif

    public void OnAppearing()
    {
        _currentMode = Preferences.Get("BatteryMode", 2);
        _protocolMode = Preferences.Get("ProtocolMode", "AUTO");

        AutoReconnectToggle.IsToggled = Preferences.Get("AutoReconnect", true);
        KillSwitchToggle.IsToggled = Preferences.Get("KillSwitch", false);
        QuickProtocolSwitchToggle.IsToggled = Preferences.Get("QuickProtocolSwitch", true);
#if WINDOWS
        RunOnStartupToggle.IsToggled = Preferences.Get("RunOnStartup", false);
#endif

        UpdateSelectionUI(_currentMode, _protocolMode);
        UpdateLockState();
    }

    public async Task PlayEntranceAnimationAsync()
    {
        OnAppearing();
        Opacity = 1;
        TranslationY = 0;
        await UIAnimations.PlayEntranceCascadeAsync(
            60,
            400,
            RaysHeaderGrid,
            EcoButton,
            BalancedButton,
            TurboButton,
            ProtocolHeaderLabel,
            AutoButton,
            FechsueButton,
            Http3Button,
            Http2Button,
            SecurityHeaderGrid,
            AutoReconnectCard,
            KillSwitchCard,
            QuickProtocolSwitchCard,
#if WINDOWS
            RunOnStartupCard,
#endif
            SpeedTestCard);
    }

    private async void OnPointerEnteredAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.02, 120, Easing.CubicOut);
        }
    }

    private async void OnPointerExitedAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.0, 120, Easing.CubicIn);
        }
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
            LockWarningLabel.TextColor = t_errorRedColor;
        }
        else if (isVpnRunning && isHotSwap)
        {
            LockWarningLabel.IsVisible = true;
            LockWarningLabel.Text = "Горячая смена (Hot-Swap) активна";
            LockWarningLabel.TextColor = t_cyanColor;
        }
        else if (isFechsue)
        {
            LockWarningLabel.IsVisible = true;
            LockWarningLabel.Text = "FECHSUE работает на 1 супер-потоке";
            LockWarningLabel.TextColor = t_purpleStroke;
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
        AutoButton.Stroke = t_inactiveStroke;
        EcoButton.Stroke = t_inactiveStroke;
        BalancedButton.Stroke = t_inactiveStroke;
        TurboButton.Stroke = t_inactiveStroke;
        Http2Button.Stroke = t_inactiveStroke;
        Http3Button.Stroke = t_inactiveStroke;
        FechsueButton.Stroke = t_inactiveStroke;

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
                EcoButton.Stroke = t_activeStroke;
            }
            else if (mode == 2)
            {
                BalancedButton.Stroke = t_activeStroke;
            }
            else if (mode == 8)
            {
                TurboButton.Stroke = t_activeStroke;
            }
        }

        var protocolText = protocol switch
        {
            "AUTO" => "AUTO (Умный)",
            "FECHSUE" => "FECHSUE (ГигаТуннель)",
            "HTTP3" => "HTTP/3 QUIC",
            _ => "HTTP/2 TCP"
        };

        if (protocol == "AUTO")
        {
            AutoButton.Stroke = t_cyanColor;
        }
        else if (protocol == "FECHSUE")
        {
            FechsueButton.Stroke = t_purpleStroke;
        }
        else if (protocol == "HTTP3")
        {
            Http3Button.Stroke = t_activeStroke;
        }
        else
        {
            Http2Button.Stroke = t_activeStroke;
        }

        CurrentSelectionLabel.Text = $"[ {raysText} / {protocolText} ]";
    }

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
        SpeedTestResultLabel.TextColor = t_cyanColor;

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
                            SpeedTestResultLabel.TextColor = t_cyanColor;
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
                    SpeedTestResultLabel.TextColor = t_emeraldColor;
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
                SpeedTestResultLabel.TextColor = t_errorRedColor;
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

    public void Dispose()
    {
        _speedTestCts?.Cancel();
        _speedTestCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
