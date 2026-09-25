using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Views;

public sealed partial class VpnView : ContentView
{
    private static readonly Color t_greenDot = Color.FromArgb("#10B981");
    private static readonly Color t_greenText = Color.FromArgb("#34D399");
    private static readonly Color t_amberDot = Color.FromArgb("#F59E0B");
    private static readonly Color t_amberText = Color.FromArgb("#FBBF24");
    private static readonly Color t_redDot = Color.FromArgb("#EF4444");
    private static readonly Color t_redText = Color.FromArgb("#F87171");
    private static readonly Color t_grayDot = Color.FromArgb("#6B7280");
    private static readonly Color t_grayStroke = Color.FromArgb("#4B5563");
    private static readonly Color t_grayText = Color.FromArgb("#9CA3AF");

    private static readonly Color t_cyanAccent = Color.FromArgb("#00E5FF");
    private static readonly Color t_redBg = Color.FromArgb("#1AFF0000");
    private static readonly string[] t_errorSeparators = ["\r\n", "\n", "Status(", "Detail="];

    private static readonly SKColor t_skPurple = SKColor.Parse("#7C3AED");
    private static readonly SKColor t_skCyan = SKColor.Parse("#00E5FF");
    private static readonly SKColor t_skGraphUp = SKColor.Parse("#9F6FF0");
    private static readonly SKColor t_skGraphDown = SKColor.Parse("#00E5FF");
    private static readonly SKColor t_skGridDash = new(255, 255, 255, 14);

    private static readonly SKPaint t_skGridPaint = new()
    {
        Color = t_skGridDash,
        StrokeWidth = 1f,
        PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke
    };

    private MainPage _parent = null!;
    private IVpnService _vpnService = null!;
    private ApiService _apiService = null!;
    private bool _isBusy;
    private bool _isErrorState;
    private float _loaderAngle;
    private IDispatcherTimer? _loaderTimer;
    private IDispatcherTimer? _graphAnimTimer;
    private double _targetSpeedUp, _targetSpeedDown;
    private double _smoothSpeedUp, _smoothSpeedDown;
    private double _smoothMaxSpeed = 1024.0;
    private float _pulsePhase;

    private readonly struct GraphSample(float time, float speedUp, float speedDown)
    {
        public readonly float Time = time;
        public readonly float SpeedUp = speedUp;
        public readonly float SpeedDown = speedDown;
    }

    private readonly List<GraphSample> _graphSamples = [];
    private readonly Stopwatch _graphStopwatch = new();
    private const float TimeWindowSeconds = 8.0f;
    private long _lastBytesSent, _lastBytesReceived;
    private DateTime _lastTrafficUpdate = DateTime.Now;

    private string? _buttonImageIdlePath;
    private string? _buttonImageConnectingPath;
    private string? _buttonImageActivePath;
    private string? _buttonImageErrorPath;

    private ButtonAnimatedClip? _buttonAnimatedIdle;
    private ButtonAnimatedClip? _buttonAnimatedConnecting;
    private ButtonAnimatedClip? _buttonAnimatedActive;
    private ButtonAnimatedClip? _buttonAnimatedError;
    private ButtonAnimatedClip? _buttonAnimatedCurrent;
    private IDispatcherTimer? _buttonAnimationTimer;
    private CancellationTokenSource? _buttonAnimCts;
    private bool _isWindowFocused = true;

    public VpnView()
    {
        InitializeComponent();
        RootLayoutGrid.SizeChanged += (s, e) =>
        {
            if (RootLayoutGrid.Width > 0)
            {
                var avail = RootLayoutGrid.Width - RootLayoutGrid.Padding.HorizontalThickness;
                if (avail > 0)
                {
                    ApplyCardWidth(avail);
                }
            }
        };

        Unloaded += (_, _) => UnsubscribeEvents();
    }

    public void ForceLayoutWidth()
    {
        if (Width > 0 && RootLayoutGrid is not null)
        {
            var avail = Width - RootLayoutGrid.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
        else if (RootLayoutGrid is { Width: > 0 })
        {
            var avail = RootLayoutGrid.Width - RootLayoutGrid.Padding.HorizontalThickness;
            if (avail > 0)
            {
                ApplyCardWidth(avail);
            }
        }
    }

    private void ApplyCardWidth(double targetWidth)
    {
        if (targetWidth <= 0 || ContentGrid is null)
        {
            return;
        }

        var safeWidth = Math.Min(targetWidth - 6, 950);
        if (safeWidth <= 0)
        {
            return;
        }

        ContentGrid.WidthRequest = safeWidth;
        ContentGrid.MaximumWidthRequest = safeWidth;
    }

    public void Initialize(MainPage parent, IVpnService vpnService, ApiService apiService)
    {
        UpdateRayIndicator();
        _parent = parent;
        _vpnService = vpnService;
        _apiService = apiService;

        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnLogUpdated -= HandleVpnLog;
        OctopusEngine.Current.OnPingUpdated -= HandlePingUpdated;

        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        _vpnService.OnLogUpdated += HandleVpnLog;
        OctopusEngine.Current.OnPingUpdated += HandlePingUpdated;

        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
        OctopusEngine.Current.OnTrafficUpdated += OnTrafficUpdated;
    }

    public void UnsubscribeEvents()
    {
        if (_vpnService is not null)
        {
            _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
            _vpnService.OnErrorOccurred -= HandleVpnError;
            _vpnService.OnLogUpdated -= HandleVpnLog;
        }

        OctopusEngine.Current.OnPingUpdated -= HandlePingUpdated;
        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;

        StopLoaderAnimation();
        StopGraphAnimation();
        DisposeAnimatedButtonResources();
    }

    public async Task PlayEntranceAnimationAsync()
    {
        UpdateRayIndicator();
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    public void UpdateBalanceUI(string timeText, string? trafficText = null)
    {
        TokenAmountLabel.Text = timeText;
        if (trafficText is not null)
        {
            TotalTrafficLabelMain.Text = trafficText;
        }
    }

    private void UpdateRayIndicator()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var protoPref = Preferences.Get("ProtocolMode", "AUTO");
            var activeProto = OctopusEngine.Current is { IsConnected: true }
                ? OctopusEngine.Current.ActiveProtocol
                : protoPref;

            var defaultRays = DeviceInfo.Platform == DevicePlatform.Android || DeviceInfo.Platform == DevicePlatform.iOS ? 2 : 8;
            var rays = Preferences.Get("BatteryMode", defaultRays);
            if (OctopusEngine.Current is { IsConnected: true })
            {
                rays = OctopusEngine.Current.ActiveRays;
            }

            if (activeProto == "FECHSUE")
            {
                RayIndicatorIcon.Icon = FluentIcons.Rocket24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#A855F7");
                RayIndicatorLabel.Text = "Режим: FECHSUE (Мульти-пинговый Watchdog • 0% потерь)";
            }
            else if (activeProto == "AUTO" && OctopusEngine.Current is not { IsConnected: true })
            {
                RayIndicatorIcon.Icon = FluentIcons.Sparkle24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#00E5FF");
                RayIndicatorLabel.Text = "Режим: Авто (Smart Probing & Fallback)";
            }
            else if (rays == 1)
            {
                var protoText = activeProto == "HTTP3" ? "HTTP/3 (QUIC)" : "HTTP/2 (TLS)";
                RayIndicatorIcon.Icon = FluentIcons.LeafOne24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#10B981");
                RayIndicatorLabel.Text = $"Режим: Eco (1 Луч / {protoText})";
            }
            else if (rays == 8)
            {
                var protoText = activeProto == "HTTP3" ? "HTTP/3 (QUIC)" : "HTTP/2 (TLS)";
                RayIndicatorIcon.Icon = FluentIcons.Flash24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#EF4444");
                RayIndicatorLabel.Text = $"Режим: Турбо (8 Лучей / {protoText})";
            }
            else
            {
                var protoText = activeProto == "HTTP3" ? "HTTP/3 (QUIC)" : "HTTP/2 (TLS)";
                RayIndicatorIcon.Icon = FluentIcons.Scales24;
                RayIndicatorIcon.IconColor = Color.FromArgb("#3B82F6");
                RayIndicatorLabel.Text = $"Режим: Баланс (2 Луча / {protoText})";
            }
        });
    }

#pragma warning disable IDE0390
    private async void OnRayIndicatorBadgeTappedAsync(object? sender, EventArgs e)
#pragma warning restore IDE0390
    {
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }

#if WINDOWS
        var action = await _parent.DisplayActionSheetAsync(
            "Протокол связи",
            "Отмена",
            null,
            "AUTO (Умный подбор и Fallback)",
            "FECHSUE (Мульти-пинговый Watchdog • 0% потерь)",
            "HTTP/3 (QUIC • Маскировка под Chrome)",
            "HTTP/2 (Стандартный TLS • Стабильный TCP)");

        if (string.IsNullOrEmpty(action) || action == "Отмена")
        {
            return;
        }

        if (action.StartsWith("AUTO", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProtocolSelection("AUTO");
        }
        else if (action.StartsWith("FECHSUE", StringComparison.OrdinalIgnoreCase) || action.StartsWith("FHARCSUE", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProtocolSelection("FECHSUE");
        }
        else if (action.StartsWith("HTTP/3", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProtocolSelection("HTTP3");
        }
        else if (action.StartsWith("HTTP/2", StringComparison.OrdinalIgnoreCase))
        {
            ApplyProtocolSelection("HTTP2");
        }
#else
        var currentProto = Preferences.Get("ProtocolMode", "AUTO");
        UpdateProtocolModalUI(currentProto);

        QuickProtocolOverlay.IsVisible = true;
        QuickProtocolOverlay.Opacity = 0;
        QuickProtocolModalCard.Scale = 0.92;

        _ = QuickProtocolOverlay.FadeToAsync(1, 180, Easing.CubicOut);
        _ = QuickProtocolModalCard.ScaleToAsync(1.0, 250, Easing.SpringOut);
#endif
    }

    private async void OnCloseProtocolModalTappedAsync(object? sender, EventArgs e)
    {
        _ = QuickProtocolModalCard.ScaleToAsync(0.92, 160, Easing.CubicIn);
        _ = await QuickProtocolOverlay.FadeToAsync(0, 160, Easing.CubicIn);
        QuickProtocolOverlay.IsVisible = false;
    }

    private void OnModalSelectAutoTapped(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconSpringHoverAsync(ModalAutoIcon, 1.25);
        ApplyProtocolSelection("AUTO");
    }

    private void OnModalSelectFechsueTapped(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconSpringHoverAsync(ModalFechsueIcon, 1.25);
        ApplyProtocolSelection("FECHSUE");
    }

    private void OnModalSelectHttp3Tapped(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconPulseAsync(ModalHttp3Icon, 1.25);
        ApplyProtocolSelection("HTTP3");
    }

    private void OnModalSelectHttp2Tapped(object? sender, TappedEventArgs e)
    {
        _ = UIAnimations.PlayIconSpinAsync(ModalHttp2Icon, 180);
        ApplyProtocolSelection("HTTP2");
    }

    private void ApplyProtocolSelection(string newProto)
    {
        Preferences.Set("ProtocolMode", newProto);
        Preferences.Set("UseHttp3", newProto == "HTTP3");
        UpdateProtocolModalUI(newProto);
        UpdateRayIndicator();

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            MainThread.BeginInvokeOnMainThread(() =>
            {
                if (QuickProtocolOverlay.IsVisible)
                {
                    OnCloseProtocolModalTappedAsync(this, EventArgs.Empty);
                }
            });

            if (_vpnService.CurrentState == AppVpnState.Connected)
            {
                try
                {
                    var (success, servers, _) = await _apiService.GetServersAsync();
                    if (success && servers is { Count: > 0 })
                    {
                        var candidateServers = await ProbeBestServerAsync(servers);
                        var targetServer = candidateServers.Count > 0 ? candidateServers[0] : servers[0];
                        await _vpnService.StopVpnAsync();
                        await Task.Delay(250);
                        await _vpnService.StartVpnAsync(targetServer.Ip, targetServer.Port, candidateServers.Count > 0 ? candidateServers : servers);
                        UpdateRayIndicator();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[HOT PROTOCOL SWITCH ERROR] {ex.Message}");
                }
            }
        });
    }

    private void UpdateProtocolModalUI(string selectedProto)
    {
        var borderInactive = (Application.Current?.Resources.TryGetValue("BorderSubtle", out var bi) == true && bi is Color bic)
            ? bic
            : Color.FromArgb("#2D2D3D");
        var activeStroke = (Application.Current?.Resources.TryGetValue("Primary", out var p) == true && p is Color pc)
            ? pc
            : Color.FromArgb("#0078D4");
        var accentColor = (Application.Current?.Resources.TryGetValue("Accent", out var a) == true && a is Color ac)
            ? ac
            : Color.FromArgb("#00E5FF");
        var purpleColor = (Application.Current?.Resources.TryGetValue("Purple", out var pr) == true && pr is Color prc)
            ? prc
            : Color.FromArgb("#A855F7");

        ModalAutoCard.Stroke = selectedProto == "AUTO" ? accentColor : borderInactive;
        ModalAutoCard.StrokeThickness = selectedProto == "AUTO" ? 1.5 : 1;
        ModalAutoCheck.IsVisible = selectedProto == "AUTO";

        ModalFechsueCard.Stroke = selectedProto == "FECHSUE" ? purpleColor : borderInactive;
        ModalFechsueCard.StrokeThickness = selectedProto == "FECHSUE" ? 1.5 : 1;
        ModalFechsueCheck.IsVisible = selectedProto == "FECHSUE";

        ModalHttp3Card.Stroke = selectedProto == "HTTP3" ? accentColor : borderInactive;
        ModalHttp3Card.StrokeThickness = selectedProto == "HTTP3" ? 1.5 : 1;
        ModalHttp3Check.IsVisible = selectedProto == "HTTP3";

        ModalHttp2Card.Stroke = selectedProto == "HTTP2" ? activeStroke : borderInactive;
        ModalHttp2Card.StrokeThickness = selectedProto == "HTTP2" ? 1.5 : 1;
        ModalHttp2Check.IsVisible = selectedProto == "HTTP2";
    }

    private void HandleVpnLog(string logMsg) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = logMsg);

    private void HandleVpnError(string err)
    {
        if (string.IsNullOrWhiteSpace(err) ||
            err.Contains("canceled", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("OperationCanceledException", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("StatusCode=\"Cancelled\"", StringComparison.OrdinalIgnoreCase) ||
            err.Contains("Call canceled by the client", StringComparison.OrdinalIgnoreCase))
        {
            Debug.WriteLine($"[VPN DISCONNECT] Normal cancellation/stop ignored: {err}");
            return;
        }

        var friendlyMessage = FormatUserFriendlyError(err);

        MainThread.BeginInvokeOnMainThread(async () =>
            await _parent.DisplayAlertAsync("Сбой сети", friendlyMessage, "OK"));
    }

    private static string FormatUserFriendlyError(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return "Не удалось установить соединение с сервером.";
        }

        if (rawError.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Unauthenticated", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Device revoked", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Missing certificate", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("401") ||
            rawError.Contains("Old certificate", StringComparison.OrdinalIgnoreCase))
        {
            return "Срок действия ключа истёк или устройство не авторизовано. Пожалуйста, выполните повторный вход в аккаунт.";
        }

        if (rawError.Contains("PINNING MISMATCH", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateNameMismatch", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateChainErrors", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("The remote certificate is invalid", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("RemoteCertificateValidationCallback", StringComparison.OrdinalIgnoreCase))
        {
            return "Ошибка проверки сертификата безопасности сервера. Пожалуйста, обновите список серверов или обратитесь в поддержку.";
        }

        if (rawError.Contains("50052") || rawError.Contains("50051") || rawError.Contains("Unavailable", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("SocketException", StringComparison.OrdinalIgnoreCase) || rawError.Contains("не получен нужный отклик", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("от другого компьютера за требуемое время", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("GOAWAY", StringComparison.OrdinalIgnoreCase) || rawError.Contains("The SSL connection could not be established", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("reset by peer", StringComparison.OrdinalIgnoreCase) || rawError.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase))
        {
            return "Сервер временно недоступен по выбранному протоколу или связь была прервана сетью.\n\nРекомендуем переключиться на протокол FECHSUE или режим AUTO.";
        }

        if (rawError.Contains("No such host is known", StringComparison.OrdinalIgnoreCase) || rawError.Contains("NameResolutionFailure", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось найти сервер. Проверьте подключение вашего устройства к интернету.";
        }

        var firstLine = rawError.Split(t_errorSeparators, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..120] + "..." : firstLine;
    }

    private void ResetPingIndicators()
    {
        PingValueLabel.Text = "-- ms";
        PingDot.Color = t_grayDot;
        PingBadge.Stroke = t_grayStroke;
        PingValueLabel.TextColor = t_grayText;
    }

    private void HandlePingUpdated(long rtt)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (OctopusEngine.Current is { IsConnected: true })
            {
                IpAddressLabel.Text = OctopusEngine.Current.AssignedIp ?? "Подключен";
                PingValueLabel.Text = $"{rtt} ms";
                if (rtt < 75)
                {
                    PingDot.Color = t_greenDot;
                    PingBadge.Stroke = t_greenDot;
                    PingValueLabel.TextColor = t_greenText;
                }
                else if (rtt < 150)
                {
                    PingDot.Color = t_amberDot;
                    PingBadge.Stroke = t_amberDot;
                    PingValueLabel.TextColor = t_amberText;
                }
                else
                {
                    PingDot.Color = t_redDot;
                    PingBadge.Stroke = t_redDot;
                    PingValueLabel.TextColor = t_redText;
                }
            }
            else
            {
                ResetPingIndicators();
            }
        });
    }

    private void HandleAppVpnStateChanged(AppVpnState state)
    {
        UpdateRayIndicator();
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            switch (state)
            {
                case AppVpnState.Disconnected:
                    _isBusy = false;
                    _isErrorState = false;
                    StopLoaderAnimation();
                    StopGraphAnimation();
                    ResetPingIndicators();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Не в сети", "СТАРТ", AppVpnState.Disconnected);
                    _parent.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connected:
                    _isBusy = false;
                    _isErrorState = false;
                    StartGraphAnimation();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = OctopusEngine.Current.AssignedIp ?? "Подключен";
                    ConnectButtonCore.IsEnabled = true;
                    UpdateRayIndicator();
                    await SetNeonStateAsync("Защищено", "СТОП", AppVpnState.Connected);
                    _parent.NotifyVpnConnected();
                    break;

                case AppVpnState.Error:
                    _isBusy = false;
                    _isErrorState = true;
                    StopLoaderAnimation();
                    StopGraphAnimation();
                    ResetPingIndicators();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Ошибка", "ПОВТОРИТЬ", AppVpnState.Error);
                    _parent.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    _isErrorState = false;
                    ResetPingIndicators();
                    StartLoaderAnimation();
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: получение...";
                    await SetNeonStateAsync("Подключение...", "ЖДИТЕ", state);
                    break;

                case AppVpnState.Disconnecting:
                    StopGraphAnimation();
                    ResetPingIndicators();
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: отключение...";
                    StatusLabel.Text = "Отключение...";
                    ConnectButtonText.Text = "ЖДИТЕ";
                    _ = UpdateCustomButtonStateAsync(AppVpnState.Disconnecting);
                    _ = LoaderCanvas.ScaleToAsync(1.0, 500, Easing.SpringOut);
                    _ = LoaderCanvas.FadeToAsync(0, 400, Easing.CubicOut);
                    break;

                default:
                    break;
            }
        });
    }

    private async Task SetNeonStateAsync(string status, string btnText, AppVpnState state)
    {
        StatusLabel.Text = status;
        ConnectButtonText.Text = btnText;

        _ = UpdateCustomButtonStateAsync(state);

        if (state == AppVpnState.Connected)
        {
            await UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                var accentColor = (Application.Current?.Resources.TryGetValue("Accent", out var ac) == true && ac is Color acc)
                    ? acc
                    : t_cyanAccent;
                ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                ConnectButtonCore.Stroke = accentColor;
            }
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.3 : 2.0;
            SafeScaleTo(LoaderCanvas, targetScale, 500, Easing.SpringOut);
        }
        else if (state == AppVpnState.Error)
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectIcon.IconColor = Colors.Red;
            StatusLabel.TextColor = Colors.Red;
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.BackgroundColor = t_redBg;
                ConnectButtonCore.Stroke = Colors.Red;
            }
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
        else if (state is AppVpnState.Connecting or AppVpnState.Reconnecting)
        {
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
        else
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
    }

    private async void OnConnectClickedAsync(object? sender, EventArgs? e)
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;

        if (_vpnService.CurrentState is not (AppVpnState.Disconnected or AppVpnState.Error))
        {
            _ = ConnectButtonCore.BounceClickAsync();
            _ = Task.Run(async () =>
            {
                try
                {
                    _ = _apiService.StopVpnOnServerAsync();
                    await _vpnService.StopVpnAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[VPN STOP ERROR] {ex.Message}");
                }
                finally
                {
                    _isBusy = false;
                }
            });
            return;
        }

        await ConnectButtonCore.BounceClickAsync();
        try
        {
#if ANDROID
            var isDisclosureAccepted = Preferences.Get("VpnDisclosureAccepted", false);
            if (!isDisclosureAccepted)
            {
                var accepted = await _parent.DisplayAlertAsync(
                    "Защита и использование VPN",
                    "Приложение Obxodka использует службу Android VpnService для создания безопасного зашифрованного туннеля и защиты вашего интернет-трафика.\n\n" +
                    "• Мы не сохраняем историю посещений, сетевые логи и личные данные.\n" +
                    "• Все данные передаются в зашифрованном виде.\n\n" +
                    "Вы согласны использовать VPN-соединение для защиты сети?",
                    "Принять",
                    "Отмена");

                if (!accepted)
                {
                    return;
                }
                Preferences.Set("VpnDisclosureAccepted", true);
            }
#endif

            if (Connectivity.Current.NetworkAccess == NetworkAccess.None)
            {
                await _parent.DisplayAlertAsync("Нет интернета", "Отсутствует подключение к интернету. Проверьте сеть и повторите попытку.", "OK");
                return;
            }

            var auditResult = await PlatformServices.CertificateAudit.CheckCertificatesAsync();
            if (auditResult.HasUntrustedRoot)
            {
                var platformInstructions = DeviceInfo.Platform == DevicePlatform.Android
                    ? "Как удалить:\n1. В открывшихся настройках выберите «Надежные сертификаты» (или «Хранилище учетных данных»).\n2. Перейдите во вкладку «Пользователь».\n3. Нажмите на сертификат и выберите «Удалить»."
                    : "Как удалить:\n1. В открывшемся окне «certmgr» раскройте «Доверенные корневые центры сертификации» -> «Сертификаты».\n2. Найдите сертификат, нажмите правой кнопкой мыши -> «Удалить».";

                var openSettings = await _parent.DisplayAlertAsync(
                    "⚠️ Обнаружен сертификат перехвата!",
                    $"На вашем устройстве установлен сторонний корневой сертификат:\n«{auditResult.CertificateName}»\n\n" +
                    "⚠️ ВНИМАНИЕ: Вне VPN этот сертификат позволяет операторам связи и третьим лицам расшифровывать ваш защищённый трафик (HTTPS), видеть переписки и перехватывать пароли.\n\n" +
                    $"{platformInstructions}\n\n" +
                    "Желаете открыть настройки для удаления?",
                    "Открыть настройки",
                    "Отмена");

                if (openSettings)
                {
                    await PlatformServices.CertificateAudit.OpenCertificateSettingsAsync();
                }

                return;
            }

            if (_parent.RemainingSeconds <= 0)
            {
                await _parent.DisplayAlertAsync("Внимание", "Нет доступного времени.", "OK");
                return;
            }

            StartLoaderAnimation();
            ConnectButtonCore.IsEnabled = false;
            IpAddressLabel.Text = "IP: получение...";
            await SetNeonStateAsync("Подключение...", "ЖДИТЕ", AppVpnState.Connecting);

            try
            {
                using var bridgeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var activeBridge = await DiscoveryService.GetActiveBridgeUrlAsync(forceRefresh: true, ct: bridgeCts.Token);
                if (!string.IsNullOrWhiteSpace(activeBridge))
                {
                    AppConfig.ApiBaseUrl = $"https://{activeBridge}/";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VPN CONNECT] Bridge discovery check failed: {ex.Message}");
            }

            var (success, servers, errorMsg) = await _apiService.GetServersAsync();
            if (!success || servers is null || servers.Count == 0)
            {
                if (AppConfig.ApiBaseUrl != AppConfig.DefaultApiBaseUrl)
                {
                    AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
                    (success, servers, errorMsg) = await _apiService.GetServersAsync();
                }
            }

            if (!success || servers is null || servers.Count == 0)
            {
                servers = [
                    new VpnServerDto("api.octocore.dev", 443, "Основной узел (Cloudflare)", true, 10, null),
                    new VpnServerDto("obxodka.one", 443, "Основной узел (Резервный)", true, 15, null),
                    new VpnServerDto("45.63.117.29", 443, "Основной узел (Прямой доступ)", true, 20, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=")
                ];
            }

            var candidateServers = await ProbeBestServerAsync(servers);
            if (candidateServers.Count == 0)
            {
                candidateServers = servers;
            }

            var targetServer = candidateServers[0];
            if (!string.IsNullOrWhiteSpace(targetServer.CertHash))
            {
                OctopusEngine.DynamicSslPublicKeyHash = targetServer.CertHash;
            }
            else
            {
                var (successHash, hashData, _) = await _apiService.GetCertHashAsync();
                if (successHash && hashData is not null && !string.IsNullOrEmpty(hashData.Hash))
                {
                    OctopusEngine.DynamicSslPublicKeyHash = hashData.Hash;
                }
            }

            await Task.Run(async () => await _vpnService.StartVpnAsync(targetServer.Ip, targetServer.Port, candidateServers));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN CONNECT ERROR] {ex.Message}");
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            IpAddressLabel.Text = "IP: не назначен";
            await SetNeonStateAsync("Не в сети", "СТАРТ", AppVpnState.Disconnected);
            var friendlyMsg = ex is SocketException or InvalidOperationException
                ? ex.Message
                : "Произошла ошибка при подключении/отключении";
            await _parent.DisplayAlertAsync("Ошибка", friendlyMsg, "OK");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private static async Task<List<VpnServerDto>> ProbeBestServerAsync(List<VpnServerDto> servers)
    {
        var probeTasks = servers.Select(async server =>
        {
            var sw = Stopwatch.StartNew();
            var reachable = false;
            try
            {
                using var cts = new CancellationTokenSource(3000);
                var host = server.Ip;
                var port = server.Port > 0 ? server.Port : 443;

                if (!IPAddress.TryParse(host, out _))
                {
                    var addrs = await Dns.GetHostAddressesAsync(host, cts.Token);
                    if (!addrs.Any(a => a.AddressFamily == AddressFamily.InterNetwork))
                    {
                        return (server, reachable: false, latencyMs: long.MaxValue);
                    }
                }

                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cts.Token).AsTask();
                sw.Stop();
                reachable = true;
            }
            catch
            {
                sw.Stop();
                reachable = false;
            }

            return (server, reachable, latencyMs: reachable ? sw.ElapsedMilliseconds : long.MaxValue);
        }).ToList();

        var results = await Task.WhenAll(probeTasks);
        var reachableServers = results
            .Where(r => r.reachable)
            .OrderBy(r => r.latencyMs)
            .Select(r => r.server)
            .ToList();

        if (reachableServers.Count > 0)
        {
            return reachableServers;
        }

        var fallbackCandidates = new List<string>();
        try
        {
            var bridge = await DiscoveryService.GetActiveBridgeUrlAsync(forceRefresh: false);
            if (!string.IsNullOrWhiteSpace(bridge))
            {
                var bHost = Uri.TryCreate(bridge, UriKind.Absolute, out var bUri) ? bUri.Host : bridge;
                if (!string.IsNullOrWhiteSpace(bHost))
                {
                    fallbackCandidates.Add(bHost);
                }
            }
        }
        catch { }
        fallbackCandidates.Add("api.octocore.dev");
        fallbackCandidates.Add("obxodka.one");
        fallbackCandidates.Add("45.63.117.29");

        foreach (var fbHost in fallbackCandidates.Distinct())
        {
            try
            {
                using var cts = new CancellationTokenSource(3000);
                var addrs = await Dns.GetHostAddressesAsync(fbHost, cts.Token);
                if (addrs.Any(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(fbHost, 443, cts.Token).AsTask();
                    var certHash = fbHost == "45.63.117.29" ? "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=" : null;
                    reachableServers.Add(new VpnServerDto(fbHost, 443, "Auto", true, 50, certHash));
                    break;
                }
            }
            catch { }
        }

        return reachableServers.Count > 0
            ? reachableServers
            : servers.Count > 0
                ? servers
                : [new VpnServerDto("45.63.117.29", 443, "Основной узел (Прямой доступ)", true, 10, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=")];
    }

    private void StartGraphAnimation()
    {
        if (_graphAnimTimer is not null)
        {
            return;
        }

        _graphStopwatch.Restart();
        lock (_graphSamples)
        {
            _graphSamples.Clear();
            _graphSamples.Add(new GraphSample(0f, 0f, 0f));
        }

        _smoothSpeedUp = 0;
        _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;

        _graphAnimTimer = Dispatcher.CreateTimer();
        _graphAnimTimer.Interval = TimeSpan.FromMilliseconds(16);
        _graphAnimTimer.Tick += (_, _) =>
        {
            if (!IsVisible || TrafficGraphCanvas is null || !TrafficGraphCanvas.IsVisible)
            {
                return;
            }

            var now = (float)_graphStopwatch.Elapsed.TotalSeconds;

            _pulsePhase += 0.09f;
            if (_pulsePhase > MathF.PI * 2)
            {
                _pulsePhase -= MathF.PI * 2;
            }

            _smoothSpeedUp += (_targetSpeedUp - _smoothSpeedUp) * 0.16;
            _smoothSpeedDown += (_targetSpeedDown - _smoothSpeedDown) * 0.16;

            lock (_graphSamples)
            {
                _graphSamples.Add(new GraphSample(now, (float)_smoothSpeedUp, (float)_smoothSpeedDown));

                var cutoff = now - (TimeWindowSeconds + 1.0f);
                while (_graphSamples.Count > 0 && _graphSamples[0].Time < cutoff)
                {
                    _graphSamples.RemoveAt(0);
                }

                var peak = 1024.0;
                for (var i = 0; i < _graphSamples.Count; i++)
                {
                    var s = _graphSamples[i];
                    if (s.SpeedUp > peak)
                    {
                        peak = s.SpeedUp;
                    }

                    if (s.SpeedDown > peak)
                    {
                        peak = s.SpeedDown;
                    }
                }

                _smoothMaxSpeed += (peak - _smoothMaxSpeed) * 0.08;
            }

            TrafficGraphCanvas.InvalidateSurface();
        };
        _graphAnimTimer.Start();
    }

    private void StopGraphAnimation()
    {
        _graphAnimTimer?.Stop();
        _graphAnimTimer = null;
        _graphStopwatch.Reset();
        _targetSpeedUp = 0;
        _targetSpeedDown = 0;
        _smoothSpeedUp = 0;
        _smoothSpeedDown = 0;
        _smoothMaxSpeed = 1024.0;

        lock (_graphSamples)
        {
            _graphSamples.Clear();
        }

        TrafficGraphCanvas?.InvalidateSurface();
    }

    private void OnTrafficUpdated(long bytesSent, long bytesReceived)
    {
        var now = DateTime.Now;
        var elapsed = (now - _lastTrafficUpdate).TotalSeconds;
        if (elapsed <= 0.05)
        {
            return;
        }

        var sentSpeed = Math.Max(0, (bytesSent - _lastBytesSent) / elapsed);
        var recvSpeed = Math.Max(0, (bytesReceived - _lastBytesReceived) / elapsed);
        _lastBytesSent = bytesSent;
        _lastBytesReceived = bytesReceived;
        _lastTrafficUpdate = now;

        _targetSpeedUp = sentSpeed;
        _targetSpeedDown = recvSpeed;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            TrafficUpLabel.Text = $"{FormatBytes(sentSpeed)}/s";
            TrafficDownLabel.Text = $"{FormatBytes(recvSpeed)}/s";
        });
    }

    private void OnPaintGraphSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var w = e.Info.Width;
        var h = e.Info.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var paddingY = 6f;
        var usableH = h - (paddingY * 2f);

        for (var line = 1; line <= 3; line++)
        {
            var y = paddingY + (usableH * (line / 4f));
            canvas.DrawLine(0, y, w, y, t_skGridPaint);
        }

        List<GraphSample> samplesCopy;
        float now;
        double maxSpeed;

        lock (_graphSamples)
        {
            if (_graphSamples.Count == 0)
            {
                return;
            }

            samplesCopy = [with(_graphSamples)];
            now = (float)_graphStopwatch.Elapsed.TotalSeconds;
            maxSpeed = Math.Max(_smoothMaxSpeed, 1024.0);
        }

        if (samplesCopy.Count == 0)
        {
            return;
        }

        void DrawStream(bool isUpStream, SKPaint strokePaint, SKColor startFillColor, SKColor strokeColor)
        {
            var points = new List<SKPoint>(samplesCopy.Count + 2);

            for (var i = 0; i < samplesCopy.Count; i++)
            {
                var s = samplesCopy[i];
                var age = now - s.Time;
                var x = w - (age / TimeWindowSeconds * w);
                var speed = isUpStream ? s.SpeedUp : s.SpeedDown;
                var ratio = Math.Clamp((float)(speed / maxSpeed), 0f, 1f);
                var y = h - paddingY - (ratio * usableH);

                points.Add(new SKPoint(x, y));
            }

            if (points.Count == 0)
            {
                return;
            }

            if (points[0].X > 0)
            {
                points.Insert(0, new SKPoint(0, points[0].Y));
            }

            if (points[^1].X < w)
            {
                points.Add(new SKPoint(w, points[^1].Y));
            }

            if (points.Count < 2)
            {
                return;
            }

            using var strokeBuilder = new SKPathBuilder();
            strokeBuilder.MoveTo(points[0]);
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = points[i];
                var p1 = points[i + 1];
                var midX = (p0.X + p1.X) / 2f;
                strokeBuilder.CubicTo(midX, p0.Y, midX, p1.Y, p1.X, p1.Y);
            }
            using var strokePath = strokeBuilder.Detach();

            using var fillBuilder = new SKPathBuilder(strokePath);
            fillBuilder.LineTo(points[^1].X, h);
            fillBuilder.LineTo(points[0].X, h);
            fillBuilder.Close();
            using var fillPath = fillBuilder.Detach();

            using var fillPaint = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, 0),
                    new SKPoint(0, h),
                    [startFillColor, SKColors.Transparent],
                    [0f, 1f],
                    SKShaderTileMode.Clamp)
            };
            canvas.DrawPath(fillPath, fillPaint);
            canvas.DrawPath(strokePath, strokePaint);

            var lastPt = points[^1];
            var pulseRadius = 4.5f + (1.8f * MathF.Sin(_pulsePhase));
            var alphaGlow = (byte)Math.Clamp(50 + (35 * MathF.Sin(_pulsePhase)), 0, 255);

            using var dotGlowPaint = new SKPaint
            {
                IsAntialias = true,
                Color = strokeColor.WithAlpha(alphaGlow),
                Style = SKPaintStyle.Fill
            };
            canvas.DrawCircle(lastPt.X, lastPt.Y, pulseRadius, dotGlowPaint);

            using var dotSolidPaint = new SKPaint
            {
                IsAntialias = true,
                Color = strokeColor,
                Style = SKPaintStyle.Fill
            };
            canvas.DrawCircle(lastPt.X, lastPt.Y, 2.5f, dotSolidPaint);
        }

        var skPrimaryBright = GetSkiaThemeColor("PrimaryBright", t_skGraphUp);
        var skAccent = GetSkiaThemeColor("Accent", t_skGraphDown);
        var skGraphUpFill = skPrimaryBright.WithAlpha(55);
        var skGraphDownFill = skAccent.WithAlpha(75);

        using var strokeUpPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = skPrimaryBright,
            StrokeWidth = 2.5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };
        using var strokeDownPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = skAccent,
            StrokeWidth = 2.5f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        DrawStream(true, strokeUpPaint, skGraphUpFill, skPrimaryBright);
        DrawStream(false, strokeDownPaint, skGraphDownFill, skAccent);
    }

    public static string FormatBytes(double bytes) => FormatHelper.FormatBytes(bytes);

    private void StartLoaderAnimation()
    {
        if (_loaderTimer is not null)
        {
            return;
        }

        _loaderTimer = Dispatcher.CreateTimer();
        _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
        _loaderTimer.Tick += (_, _) =>
        {
            _loaderAngle += 1.5f;
            LoaderCanvas?.InvalidateSurface();
        };
        LoaderCanvas.Opacity = 1;
        LoaderCanvas.IsVisible = true;
        _loaderTimer.Start();
    }

    private void StopLoaderAnimation()
    {
        _loaderTimer?.Stop();
        _loaderTimer = null;

        if (LoaderCanvas is not null)
        {
            try
            {
                _ = LoaderCanvas.AbortAnimation("ScaleTo");
                _ = LoaderCanvas.AbortAnimation("FadeTo");
            }
            catch
            {
            }

            SafeScaleTo(LoaderCanvas, 1.0, 250, Easing.SpringOut);
            SafeFadeTo(LoaderCanvas, 0, 250, Easing.CubicOut, () => LoaderCanvas.IsVisible = false);
        }
    }

    private static void SafeScaleTo(VisualElement? element, double scale, uint length = 250, Easing? easing = null)
    {
        if (element is null)
        {
            return;
        }

        try
        {
            if (element.Handler is not null && element.IsLoaded && element.Window is not null)
            {
                _ = element.ScaleToAsync(scale, length, easing ?? Easing.Linear);
            }
            else
            {
                element.Scale = scale;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeScaleTo] Handled disposed UI animation: {ex.Message}");
        }
    }

    private static void SafeFadeTo(VisualElement? element, double opacity, uint length = 250, Easing? easing = null, Action? onCompleted = null)
    {
        if (element is null)
        {
            onCompleted?.Invoke();
            return;
        }

        try
        {
            if (element.Handler is not null && element.IsLoaded && element.Window is not null)
            {
                _ = element.FadeToAsync(opacity, length, easing ?? Easing.Linear).ContinueWith(_ =>
                {
                    if (onCompleted is not null)
                    {
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            try
                            {
                                onCompleted();
                            }
                            catch
                            {
                            }
                        });
                    }
                });
            }
            else
            {
                element.Opacity = opacity;
                onCompleted?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeFadeTo] Handled disposed UI animation: {ex.Message}");
            onCompleted?.Invoke();
        }
    }

    private void OnPaintLoaderSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_loaderTimer is null)
        {
            return;
        }

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 4f;

        var skPrimary = GetSkiaThemeColor("Primary", t_skPurple);
        var skAccent = GetSkiaThemeColor("Accent", t_skCyan);
        var colorPurple = _isErrorState ? SKColors.DarkRed : skPrimary;
        var colorCyan = _isErrorState ? SKColors.Red : skAccent;

        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 22f),
            Color = colorPurple.WithAlpha(45)
        };
        canvas.DrawCircle(cx, cy, r - 30f, glowPaint);

        var configs = new (float speed, float delay, float ox, float oy, float size, int sides)[]
        {
            ( 1.0f,   0f, 0.5f, 0.5f, 0.74f, 5),
            (-1.0f,   0f, 0.5f, 0.5f, 0.65f, 6),
            ( 1.5f,  60f, 0.5f, 0.6f, 0.54f, 5),
            (-1.5f, -60f, 0.4f, 0.4f, 0.45f, 4),
            ( 2.0f, 120f, 0.6f, 0.4f, 0.38f, 6),
        };

        using var polyPaint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < configs.Length; i++)
        {
            var (speed, delay, ox, oy, size, sides) = configs[i];
            var rot = (_loaderAngle * speed) + delay;
            var alpha = (byte)(90 + (i * 15));
            polyPaint.Color = (i % 2 == 0 ? colorPurple : colorCyan).WithAlpha(alpha);
            polyPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f);

            var pivotX = cx + ((ox - 0.5f) * r);
            var pivotY = cy + ((oy - 0.5f) * r);
            _ = canvas.Save();
            canvas.RotateDegrees(rot, pivotX, pivotY);
            using var path = MakePolygon(pivotX, pivotY, r * size, sides);
            canvas.DrawPath(path, polyPaint);
            canvas.Restore();
        }
    }

    private static SKColor GetSkiaThemeColor(string key, SKColor fallback) =>
        Application.Current?.Resources.TryGetValue(key, out var val) == true && val is Color c
            ? new SKColor((byte)(c.Red * 255), (byte)(c.Green * 255), (byte)(c.Blue * 255), (byte)(c.Alpha * 255))
            : fallback;

    private sealed partial class ButtonAnimatedClip : IDisposable
    {
        private readonly List<SKBitmap> _frames = [];
        private readonly List<int> _durations = [];
        private int _currentFrameIndex;
        private int _elapsedMs;

        public int FrameCount => _frames.Count;
        public SKBitmap? CurrentBitmap => _frames.Count > 0 ? _frames[_currentFrameIndex] : null;

        public static ButtonAnimatedClip? Load(string path, int maxFrames = 180, CancellationToken ct = default)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return null;
                }

                using var stream = File.OpenRead(path);
                using var codec = SKCodec.Create(stream);
                if (codec == null)
                {
                    var staticBmp = SKBitmap.Decode(path);
                    if (staticBmp == null)
                    {
                        return null;
                    }

                    var staticClip = new ButtonAnimatedClip();
                    staticClip._frames.Add(staticBmp);
                    staticClip._durations.Add(1000);
                    return staticClip;
                }

                var count = codec.FrameCount;
                var clip = new ButtonAnimatedClip();
                var frameInfos = codec.FrameInfo;

                var step = count > maxFrames ? (int)Math.Ceiling((double)count / maxFrames) : 1;

                for (var i = 0; i < count; i += step)
                {
                    if (ct.IsCancellationRequested)
                    {
                        clip.Dispose();
                        return null;
                    }

                    var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                    var bmp = new SKBitmap(info);
                    var opts = new SKCodecOptions(i);
                    var res = codec.GetPixels(info, bmp.GetPixels(), opts);
                    if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        bmp.Dispose();
                        continue;
                    }

                    var dur = frameInfos != null && frameInfos.Length > i ? frameInfos[i].Duration : 16;
                    if (dur <= 0)
                    {
                        dur = 16;
                    }

                    dur *= step;

                    clip._frames.Add(bmp);
                    clip._durations.Add(dur);
                }

                if (clip._frames.Count == 0)
                {
                    clip.Dispose();
                    return null;
                }

                return clip;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[AnimButton] Load failed for {path}: {ex.Message}");
                return null;
            }
        }

        public void Advance(int stepMs)
        {
            if (_frames.Count <= 1)
            {
                return;
            }

            _elapsedMs += stepMs;
            while (_durations.Count > _currentFrameIndex && _elapsedMs >= _durations[_currentFrameIndex])
            {
                _elapsedMs -= _durations[_currentFrameIndex];
                _currentFrameIndex = (_currentFrameIndex + 1) % _frames.Count;
            }
        }

        public void Dispose()
        {
            foreach (var f in _frames)
            {
                f.Dispose();
            }
            _frames.Clear();
            _durations.Clear();
        }
    }

    private void OnCustomButtonAnimatedCanvasPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var current = _buttonAnimatedCurrent;
        var bmp = current?.CurrentBitmap;
        if (bmp is null)
        {
            return;
        }

        using var paint = new SKPaint
        {
            IsAntialias = true,
        };

        var info = e.Info;
        var src = new SKRect(0, 0, bmp.Width, bmp.Height);
        var dst = new SKRect(0, 0, info.Width, info.Height);
        canvas.DrawBitmap(bmp, src, dst, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), paint);
    }

    public void OnWindowFocusChanged(bool isFocused)
    {
        _isWindowFocused = isFocused;
        if (!isFocused)
        {
            StopButtonAnimation();
        }
        else
        {
            if (CustomButtonAnimatedCanvas.IsVisible && _buttonAnimatedCurrent is not null)
            {
                StartButtonAnimation();
            }
        }
    }

    private void StartButtonAnimation()
    {
        if (!_isWindowFocused)
        {
            return;
        }

        if (_buttonAnimationTimer is not null)
        {
            return;
        }

        _buttonAnimationTimer = Dispatcher.CreateTimer();
        _buttonAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
#pragma warning disable IDE0058
        _buttonAnimationTimer.Tick += (_, _) =>
        {
            _buttonAnimatedCurrent?.Advance(16);
            CustomButtonAnimatedCanvas.InvalidateSurface();
        };
#pragma warning restore IDE0058
        _buttonAnimationTimer.Start();
    }

    private void StopButtonAnimation()
    {
        _buttonAnimationTimer?.Stop();
        _buttonAnimationTimer = null;
    }

    private void DisposeAnimatedButtonResources()
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;
        StopButtonAnimation();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedIdle?.Dispose();
        _buttonAnimatedIdle = null;
        _buttonAnimatedConnecting?.Dispose();
        _buttonAnimatedConnecting = null;
        _buttonAnimatedActive?.Dispose();
        _buttonAnimatedActive = null;
        _buttonAnimatedError?.Dispose();
        _buttonAnimatedError = null;
    }

    private string? GetButtonImagePathForState(AppVpnState state)
    {
        return state switch
        {
            AppVpnState.Connected => _buttonImageActivePath ?? _buttonImageIdlePath,
            AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => _buttonImageConnectingPath ?? _buttonImageIdlePath,
            AppVpnState.Error => _buttonImageErrorPath ?? _buttonImageIdlePath,
            AppVpnState.Disconnected => _buttonImageIdlePath,
            _ => _buttonImageIdlePath
        };
    }

    private ButtonAnimatedClip? GetButtonAnimatedClipForState(AppVpnState state)
    {
        return state switch
        {
            AppVpnState.Connected => _buttonAnimatedActive,
            AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => _buttonAnimatedConnecting,
            AppVpnState.Error => _buttonAnimatedError,
            AppVpnState.Disconnected => _buttonAnimatedIdle,
            _ => null
        };
    }

    public void ApplyThemeButton(
        string? imageIdlePath,
        string? imageConnectingPath = null,
        string? imageActivePath = null,
        string? imageErrorPath = null,
        string? videoIdlePath = null,
        string? videoConnectingPath = null,
        string? videoActivePath = null,
        string? videoErrorPath = null)
    {
        _buttonImageIdlePath = imageIdlePath;
        _buttonImageConnectingPath = imageConnectingPath;
        _buttonImageActivePath = imageActivePath;
        _buttonImageErrorPath = imageErrorPath;

        var hasCustom = !string.IsNullOrEmpty(imageIdlePath) ||
                        !string.IsNullOrEmpty(imageConnectingPath) ||
                        !string.IsNullOrEmpty(imageActivePath) ||
                        !string.IsNullOrEmpty(imageErrorPath);

        MainThread.BeginInvokeOnMainThread(() =>
        {
            DefaultButtonContent.IsVisible = !hasCustom;
            CustomButtonImage.IsVisible = hasCustom;

            if (hasCustom)
            {
                ConnectButtonCore.BackgroundColor = Colors.Transparent;
                ConnectButtonCore.Stroke = Colors.Transparent;

                var currentState = _vpnService?.CurrentState ?? AppVpnState.Disconnected;
                var initialPath = GetButtonImagePathForState(currentState);
                if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                {
                    CustomButtonImage.Source = ImageSource.FromFile(initialPath);
                }
            }
            else
            {
                ResetThemeButtonInternal();
            }
        });

        var idleCandidate = videoIdlePath ?? (IsPotentialAnimatedFile(imageIdlePath) ? imageIdlePath : null);
        var connCandidate = videoConnectingPath ?? (IsPotentialAnimatedFile(imageConnectingPath) ? imageConnectingPath : null);
        var activeCandidate = videoActivePath ?? (IsPotentialAnimatedFile(imageActivePath) ? imageActivePath : null);
        var errorCandidate = videoErrorPath ?? (IsPotentialAnimatedFile(imageErrorPath) ? imageErrorPath : null);

        var hasAnyAnim = !string.IsNullOrEmpty(idleCandidate) ||
                         !string.IsNullOrEmpty(connCandidate) ||
                         !string.IsNullOrEmpty(activeCandidate) ||
                         !string.IsNullOrEmpty(errorCandidate);

        if (!hasAnyAnim)
        {
            DisposeAnimatedButtonResources();
            MainThread.BeginInvokeOnMainThread(() => CustomButtonAnimatedCanvas.IsVisible = false);
            return;
        }

        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        var cts = new CancellationTokenSource();
        _buttonAnimCts = cts;
        var token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                var idleClip = !string.IsNullOrEmpty(idleCandidate) && File.Exists(idleCandidate)
                    ? ButtonAnimatedClip.Load(idleCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    return;
                }

                var connClip = !string.IsNullOrEmpty(connCandidate) && File.Exists(connCandidate)
                    ? ButtonAnimatedClip.Load(connCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    return;
                }

                var activeClip = !string.IsNullOrEmpty(activeCandidate) && File.Exists(activeCandidate)
                    ? ButtonAnimatedClip.Load(activeCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    return;
                }

                var errorClip = !string.IsNullOrEmpty(errorCandidate) && File.Exists(errorCandidate)
                    ? ButtonAnimatedClip.Load(errorCandidate, 180, token)
                    : null;
                if (token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    errorClip?.Dispose();
                    return;
                }

                var hasLoadedAnim = idleClip != null || connClip != null || activeClip != null || errorClip != null;
                if (!hasLoadedAnim || token.IsCancellationRequested)
                {
                    idleClip?.Dispose();
                    connClip?.Dispose();
                    activeClip?.Dispose();
                    errorClip?.Dispose();
                    return;
                }

                await MainThread.InvokeOnMainThreadAsync(async () =>
                {
                    if (token.IsCancellationRequested)
                    {
                        idleClip?.Dispose();
                        connClip?.Dispose();
                        activeClip?.Dispose();
                        errorClip?.Dispose();
                        return;
                    }

                    StopButtonAnimation();
                    _buttonAnimatedIdle?.Dispose();
                    _buttonAnimatedConnecting?.Dispose();
                    _buttonAnimatedActive?.Dispose();
                    _buttonAnimatedError?.Dispose();

                    _buttonAnimatedIdle = idleClip;
                    _buttonAnimatedConnecting = connClip;
                    _buttonAnimatedActive = activeClip;
                    _buttonAnimatedError = errorClip;

                    var currentState = _vpnService?.CurrentState ?? AppVpnState.Disconnected;
                    await UpdateCustomButtonStateAsync(currentState);
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[VpnView] Async button animation load error: {ex.Message}");
            }
        }, token);
    }

    private static bool IsPotentialAnimatedFile(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var ext = Path.GetExtension(path);
        return ext.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".apng", StringComparison.OrdinalIgnoreCase);
    }

    public void ResetThemeButton()
    {
        _buttonImageIdlePath = null;
        _buttonImageConnectingPath = null;
        _buttonImageActivePath = null;
        _buttonImageErrorPath = null;
        DisposeAnimatedButtonResources();
        MainThread.BeginInvokeOnMainThread(ResetThemeButtonInternal);
    }

    private void ResetThemeButtonInternal()
    {
        CustomButtonImage.IsVisible = false;
        CustomButtonImage.Source = null;
        CustomButtonAnimatedCanvas.IsVisible = false;
        DefaultButtonContent.IsVisible = true;
        ConnectButtonCore.ClearValue(BackgroundColorProperty);
        ConnectButtonCore.ClearValue(Border.StrokeProperty);
        ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
        ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
    }

    private async Task UpdateCustomButtonStateAsync(AppVpnState state)
    {
        var targetClip = GetButtonAnimatedClipForState(state);
        var targetImagePath = GetButtonImagePathForState(state);

        if (targetClip != null)
        {
            _buttonAnimatedCurrent = targetClip;
            CustomButtonImage.IsVisible = false;
            CustomButtonAnimatedCanvas.IsVisible = true;
            DefaultButtonContent.IsVisible = false;
            ConnectButtonCore.BackgroundColor = Colors.Transparent;
            ConnectButtonCore.Stroke = Colors.Transparent;
            StartButtonAnimation();
            CustomButtonAnimatedCanvas.InvalidateSurface();
            return;
        }

        StopButtonAnimation();
        _buttonAnimatedCurrent = null;
        CustomButtonAnimatedCanvas.IsVisible = false;

        if (!string.IsNullOrEmpty(targetImagePath) && File.Exists(targetImagePath))
        {
            DefaultButtonContent.IsVisible = false;
            ConnectButtonCore.BackgroundColor = Colors.Transparent;
            ConnectButtonCore.Stroke = Colors.Transparent;

            if (CustomButtonImage.IsVisible)
            {
#pragma warning disable IDE0058
                await CustomButtonImage.FadeToAsync(0, 90, Easing.CubicOut);
                CustomButtonImage.Source = ImageSource.FromFile(targetImagePath);
                CustomButtonImage.Scale = 0.92;
                var fadeIn = CustomButtonImage.FadeToAsync(1, 130, Easing.CubicIn);
                var scaleIn = CustomButtonImage.ScaleToAsync(1.0, 200, Easing.SpringOut);
                await Task.WhenAll(fadeIn, scaleIn);
#pragma warning restore IDE0058
            }
            else
            {
                CustomButtonImage.Opacity = 1;
                CustomButtonImage.Scale = 1.0;
                CustomButtonImage.Source = ImageSource.FromFile(targetImagePath);
                CustomButtonImage.IsVisible = true;
            }
            return;
        }

        CustomButtonImage.IsVisible = false;
        CustomButtonImage.Source = null;
        DefaultButtonContent.IsVisible = true;
    }

    public void UpdateCardOpacity()
    {
        var bgSurface = GetAppColor("BgSurface", Color.FromArgb("#161622"));
        var bgElevated = GetAppColor("BgElevated", Color.FromArgb("#202030"));

        CardIp.BackgroundColor = bgSurface;
        Card2.BackgroundColor = bgSurface;
        Card5.BackgroundColor = bgSurface;
        Card6.BackgroundColor = bgSurface;
        Card1Wrapper.BackgroundColor = bgSurface;
        RayIndicatorBadge.BackgroundColor = bgElevated;
    }

    private double _lastAllocatedW = -1;
    private double _lastAllocatedH = -1;
    private double _currentTopInset;

    public void SetHeaderTopInset(double top)
    {
        _currentTopInset = top;
        if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayoutGrid != null)
        {
            RootLayoutGrid.Padding = new Thickness(8, Math.Max(top + 4, 8), 8, 0);
        }

        if (_lastAllocatedW > 0 && _lastAllocatedH > 0)
        {
            ApplyDynamicLayout(_lastAllocatedW, _lastAllocatedH);
        }
    }

    protected override void OnSizeAllocated(double width, double height)
    {
        base.OnSizeAllocated(width, height);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        if (Math.Abs(_lastAllocatedW - width) > 1 || Math.Abs(_lastAllocatedH - height) > 1)
        {
            _lastAllocatedW = width;
            _lastAllocatedH = height;
            ApplyDynamicLayout(width, height);
        }
    }

    private void ApplyDynamicLayout(double width, double height)
    {
        var availWidth = width - (RootLayoutGrid?.Padding.HorizontalThickness ?? 0);
        if (availWidth > 0)
        {
            ApplyCardWidth(availWidth);
        }

        var isDesktopOrTablet = DeviceInfo.Idiom == DeviceIdiom.Desktop || DeviceInfo.Idiom == DeviceIdiom.Tablet;
        var isWide = AdaptiveLayoutHelper.IsWideLayout(width, isDesktopOrTablet);

        if (ContentGrid is not null)
        {
            if (isWide)
            {
                if (ContentGrid.ColumnDefinitions.Count != 2)
                {
                    ContentGrid.ColumnDefinitions.Clear();
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(5, GridUnitType.Star)));
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(4, GridUnitType.Star)));
                    ContentGrid.RowDefinitions.Clear();
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    ContentGrid.ColumnSpacing = 28;
                    ContentGrid.RowSpacing = 0;
                    Grid.SetColumn(Card1Wrapper, 0);
                    Grid.SetRow(Card1Wrapper, 0);
                    Grid.SetColumn(TopCardsGrid, 1);
                    Grid.SetRow(TopCardsGrid, 0);
                    TopCardsGrid.RowDefinitions.Clear();
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(new GridLength(2, GridUnitType.Star)));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                }

                Card1Wrapper.MinimumHeightRequest = -1;
                Card1ContentGrid.Padding = new Thickness(28, 32);
                GraphContainer.IsVisible = true;
                GraphContainer.HeightRequest = -1;
                Card5.HeightRequest = -1;
                Card6.HeightRequest = -1;
                ButtonContainerGrid.WidthRequest = 280;
                ButtonContainerGrid.HeightRequest = 280;
                ButtonOuterRing.WidthRequest = 260;
                ButtonOuterRing.HeightRequest = 260;
                ButtonOuterRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(130) };
                ButtonMiddleRing.WidthRequest = 224;
                ButtonMiddleRing.HeightRequest = 224;
                ButtonMiddleRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(112) };
                ConnectButtonCore.WidthRequest = 180;
                ConnectButtonCore.HeightRequest = 180;
                ConnectButtonCore.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(90) };
                ConnectIcon.IconSize = 44;
                ConnectButtonText.FontSize = 15;
                TrafficGraphCanvas.InvalidateSurface();
                return;
            }
            else
            {
                if (ContentGrid.ColumnDefinitions.Count != 1)
                {
                    ContentGrid.ColumnDefinitions.Clear();
                    ContentGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
                    ContentGrid.RowDefinitions.Clear();
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    ContentGrid.RowDefinitions.Add(new RowDefinition(GridLength.Star));
                    ContentGrid.ColumnSpacing = 0;
                    ContentGrid.RowSpacing = 10;
                    Grid.SetColumn(TopCardsGrid, 0);
                    Grid.SetRow(TopCardsGrid, 0);
                    Grid.SetColumn(Card1Wrapper, 0);
                    Grid.SetRow(Card1Wrapper, 1);
                    TopCardsGrid.RowDefinitions.Clear();
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    TopCardsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                }
            }
        }

        var (buttonSize, graphHeight, _) = AdaptiveLayoutHelper.CalculateVpnViewDimensions(
            width,
            height,
            _currentTopInset,
            isDesktopOrTablet);

        var isShortScreen = AdaptiveLayoutHelper.IsShortScreen(height);
        if (isShortScreen)
        {
            Card1Wrapper.MinimumHeightRequest = -1;
            Card1ContentGrid.Padding = new Thickness(12, 6, 12, 16);
            TopCardsGrid.RowSpacing = 6;
            if (ContentGrid is { } cgShort)
            {
                cgShort.RowSpacing = 6;
            }
            Card5.HeightRequest = 48;
            Card6.HeightRequest = 48;
            GraphContainer.IsVisible = false;
            GraphContainer.HeightRequest = 0;
        }
        else
        {
            Card1ContentGrid.Padding = new Thickness(16, 14, 16, 26);
            TopCardsGrid.RowSpacing = 10;
            if (ContentGrid is { } cgNormal)
            {
                cgNormal.RowSpacing = 10;
            }
            Card5.HeightRequest = 64;
            Card6.HeightRequest = 64;
            GraphContainer.IsVisible = true;
            GraphContainer.HeightRequest = graphHeight;

            var topCardsH = TopCardsGrid.Height > 0 ? TopCardsGrid.Height : (48.0 + 48.0 + 64.0 + graphHeight + 30.0);
            var estimatedAvailableForCard1 = height - _currentTopInset - 100.0 - topCardsH;
            var targetCard1MinHeight = Math.Clamp(Math.Round(estimatedAvailableForCard1), 340.0, 540.0);
            Card1Wrapper.MinimumHeightRequest = targetCard1MinHeight;
        }

        ButtonContainerGrid.WidthRequest = buttonSize;
        ButtonContainerGrid.HeightRequest = buttonSize;

        var outerRingSize = Math.Round(buttonSize * 0.935);
        ButtonOuterRing.WidthRequest = outerRingSize;
        ButtonOuterRing.HeightRequest = outerRingSize;
        ButtonOuterRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(outerRingSize / 2.0) };

        var middleRingSize = Math.Round(buttonSize * 0.828);
        ButtonMiddleRing.WidthRequest = middleRingSize;
        ButtonMiddleRing.HeightRequest = middleRingSize;
        ButtonMiddleRing.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(middleRingSize / 2.0) };

        var coreSize = Math.Round(buttonSize * 0.707);
        ConnectButtonCore.WidthRequest = coreSize;
        ConnectButtonCore.HeightRequest = coreSize;
        ConnectButtonCore.StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(coreSize / 2.0) };

        ConnectIcon.IconSize = (int)Math.Max(22, Math.Round(buttonSize * 0.165));
        ConnectButtonText.FontSize = Math.Max(10, Math.Round(buttonSize * 0.058));

        GraphContainer.HeightRequest = graphHeight;
        TrafficGraphCanvas.InvalidateSurface();
    }

    public void OnThemeChanged()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoaderCanvas?.InvalidateSurface();
            TrafficGraphCanvas?.InvalidateSurface();
            UpdateRayIndicator();

            UpdateCardOpacity();

            var borderSubtle = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));

            CardIp.Stroke = borderSubtle;
            Card2.Stroke = borderSubtle;
            Card5.Stroke = borderSubtle;
            Card6.Stroke = borderSubtle;
            Card1Wrapper.Stroke = borderSubtle;
            RayIndicatorBadge.Stroke = borderMedium;

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

            if (_vpnService.CurrentState == AppVpnState.Disconnected)
            {
                _ = UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            else if (_vpnService.CurrentState == AppVpnState.Connected)
            {
                _ = UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                var accentColor = GetAppColor("Accent", t_cyanAccent);
                ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                ConnectButtonCore.Stroke = accentColor;
            }
        });
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
        catch { }
        return fallback;
    }

    private static SKPath MakePolygon(float cx, float cy, float r, int sides)
    {
        using var builder = new SKPathBuilder();
        for (var i = 0; i < sides; i++)
        {
            var a = (float)((i * 2 * Math.PI / sides) - (Math.PI / 2));
            var x = cx + (r * MathF.Cos(a));
            var y = cy + (r * MathF.Sin(a));
            if (i == 0)
            {
                builder.MoveTo(x, y);
            }
            else
            {
                builder.LineTo(x, y);
            }
        }
        builder.Close();
        return builder.Detach();
    }
}
