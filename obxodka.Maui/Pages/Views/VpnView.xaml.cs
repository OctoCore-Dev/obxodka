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
    private static readonly Color t_cyanBg = Color.FromArgb("#1A00E5FF");
    private static readonly Color t_redBg = Color.FromArgb("#1AFF0000");
    private static readonly string[] t_errorSeparators = ["\r\n", "\n", "Status(", "Detail="];

    private static readonly SKColor t_skPurple = SKColor.Parse("#7C3AED");
    private static readonly SKColor t_skCyan = SKColor.Parse("#00E5FF");
    private static readonly SKColor t_skGraphUp = SKColor.Parse("#9F6FF0");
    private static readonly SKColor t_skGraphDown = SKColor.Parse("#00E5FF");
    private static readonly SKColor t_skGraphUpFill = new(159, 111, 240, 55);
    private static readonly SKColor t_skGraphDownFill = new(0, 229, 255, 75);
    private static readonly SKColor t_skGridDash = new(255, 255, 255, 14);

    private static readonly SKPaint t_skGridPaint = new()
    {
        Color = t_skGridDash,
        StrokeWidth = 1f,
        PathEffect = SKPathEffect.CreateDash([4f, 4f], 0),
        IsAntialias = true,
        Style = SKPaintStyle.Stroke
    };

    private static readonly SKPaint t_skStrokeUpPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        Color = t_skGraphUp,
        StrokeWidth = 2.5f,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };

    private static readonly SKPaint t_skStrokeDownPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        Color = t_skGraphDown,
        StrokeWidth = 2.5f,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round
    };

    private static readonly string[] t_suffixes = ["B", "KB", "MB", "GB", "TB"];

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

    public VpnView()
    {
        InitializeComponent();
        Unloaded += (_, _) => UnsubscribeEvents();
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
    }

    public async Task PlayEntranceAnimationAsync()
    {
        UpdateRayIndicator();
        await UIAnimations.PlayEntranceCascadeAsync(80, 450,
            CardIpWrapper, Card2Wrapper, Card5Wrapper, Card1Wrapper);
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
                RayIndicatorLabel.Text = "Режим: FECHSUE (ГигаТуннель • 0% потерь)";
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

    private async void OnPointerEnteredAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.03, 120, Easing.CubicOut);
        }
    }

    private async void OnPointerExitedAsync(object? sender, PointerEventArgs e)
    {
        if (sender is VisualElement ve && ve.IsEnabled)
        {
            _ = await ve.ScaleToAsync(1.0, 120, Easing.CubicIn);
        }
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
            "FECHSUE (ГигаТуннель UDP • 0% потерь)",
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
        else if (action.StartsWith("FECHSUE", StringComparison.OrdinalIgnoreCase))
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
                        await OctopusEngine.Current.ReconnectAsync(servers[0].Ip, servers[0].Port);
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
        var borderInactive = Color.FromArgb("#2D2D3D");

        ModalAutoCard.Stroke = selectedProto == "AUTO" ? Color.FromArgb("#00E5FF") : borderInactive;
        ModalAutoCard.StrokeThickness = selectedProto == "AUTO" ? 1.5 : 1;
        ModalAutoCheck.IsVisible = selectedProto == "AUTO";

        ModalFechsueCard.Stroke = selectedProto == "FECHSUE" ? Color.FromArgb("#A855F7") : borderInactive;
        ModalFechsueCard.StrokeThickness = selectedProto == "FECHSUE" ? 1.5 : 1;
        ModalFechsueCheck.IsVisible = selectedProto == "FECHSUE";

        ModalHttp3Card.Stroke = selectedProto == "HTTP3" ? Color.FromArgb("#00E5FF") : borderInactive;
        ModalHttp3Card.StrokeThickness = selectedProto == "HTTP3" ? 1.5 : 1;
        ModalHttp3Check.IsVisible = selectedProto == "HTTP3";

        ModalHttp2Card.Stroke = selectedProto == "HTTP2" ? Color.FromArgb("#0078D4") : borderInactive;
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

        if (rawError.Contains("50052") || rawError.Contains("50051") || rawError.Contains("Unavailable") ||
            rawError.Contains("SocketException") || rawError.Contains("не получен нужный отклик") ||
            rawError.Contains("от другого компьютера за требуемое время"))
        {
            return "Сервер временно недоступен по выбранному протоколу или порт блокируется сетью.\n\nРекомендуем переключиться на протокол FECHSUE или режим AUTO.";
        }

        if (rawError.Contains("SSL") || rawError.Contains("Certificate") || rawError.Contains("PINNING MISMATCH"))
        {
            return "Ошибка проверки сертификата безопасности сервера. Пожалуйста, обновите список серверов или войдите заново.";
        }

        if (rawError.Contains("No such host is known") || rawError.Contains("NameResolutionFailure"))
        {
            return "Не удалось найти сервер. Проверьте подключение вашего устройства к интернету.";
        }

        if (rawError.Contains("Unauthorized") || rawError.Contains("401") || rawError.Contains("Old certificate"))
        {
            return "Срок действия ключа истёк. Пожалуйста, выполните повторный вход в аккаунт.";
        }

        var firstLine = rawError.Split(t_errorSeparators, StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return firstLine.Length > 120 ? firstLine[..120] + "..." : firstLine;
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
                PingValueLabel.Text = "-- ms";
                PingDot.Color = t_grayDot;
                PingBadge.Stroke = t_grayStroke;
                PingValueLabel.TextColor = t_grayText;
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
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Не в сети", "СТАРТ", false);
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
                    await SetNeonStateAsync("Защищено", "СТОП", true);
                    _parent.NotifyVpnConnected();
                    break;

                case AppVpnState.Error:
                    _isBusy = false;
                    _isErrorState = true;
                    StopLoaderAnimation();
                    StopGraphAnimation();
                    OuterAura.IsVisible = true;
                    IpAddressLabel.Text = "IP: не назначен";
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync("Ошибка", "ПОВТОРИТЬ", false, true);
                    _parent.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    _isErrorState = false;
                    StartLoaderAnimation();
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: получение...";
                    await SetNeonStateAsync("Подключение...", "ЖДИТЕ", false);
                    break;

                case AppVpnState.Disconnecting:
                    StopGraphAnimation();
                    ConnectButtonCore.IsEnabled = false;
                    IpAddressLabel.Text = "IP: отключение...";
                    StatusLabel.Text = "Отключение...";
                    ConnectButtonText.Text = "ЖДИТЕ";
                    _ = LoaderCanvas.ScaleToAsync(1.0, 500, Easing.SpringOut);
                    _ = LoaderCanvas.FadeToAsync(0, 400, Easing.CubicOut);
                    break;

                default:
                    break;
            }
        });
    }

    private async Task SetNeonStateAsync(string status, string btnText, bool connected, bool isError = false)
    {
        StatusLabel.Text = status;
        ConnectButtonText.Text = btnText;

        if (connected)
        {
            await UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectButtonCore.BackgroundColor = t_cyanBg;
            ConnectButtonCore.Stroke = t_cyanAccent;
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.3 : 2.0;
            SafeScaleTo(LoaderCanvas, targetScale, 500, Easing.SpringOut);
        }
        else if (isError)
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectIcon.IconColor = Colors.Red;
            StatusLabel.TextColor = Colors.Red;
            ConnectButtonCore.BackgroundColor = t_redBg;
            ConnectButtonCore.Stroke = Colors.Red;
            SafeScaleTo(LoaderCanvas, 1.0, 500, Easing.SpringOut);
        }
        else
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            ConnectButtonCore.ClearValue(BackgroundColorProperty);
            ConnectButtonCore.ClearValue(Border.StrokeProperty);
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

            if (Connectivity.Current.NetworkAccess != NetworkAccess.Internet)
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

            var (success, servers, errorMsg) = await _apiService.GetServersAsync();
            if (!success || servers is null || servers.Count == 0)
            {
                await _parent.DisplayAlertAsync("Ошибка", errorMsg ?? "Не удалось получить список нод", "OK");
                return;
            }

            var targetServer = servers[0];
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

            await Task.Run(async () => await _vpnService.StartVpnAsync(targetServer.Ip, targetServer.Port));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN CONNECT ERROR] {ex.Message}");
            await _parent.DisplayAlertAsync("Ошибка", "Произошла ошибка при подключении/отключении", "OK");
        }
        finally
        {
            _isBusy = false;
        }
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

        DrawStream(true, t_skStrokeUpPaint, t_skGraphUpFill, t_skGraphUp);
        DrawStream(false, t_skStrokeDownPaint, t_skGraphDownFill, t_skGraphDown);
    }

    public static string FormatBytes(double bytes)
    {
        int i;
        var d = bytes;
        for (i = 0; i < t_suffixes.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            d = bytes / 1024.0;
        }

        return $"{d:0.##} {t_suffixes[i]}";
    }

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

        var colorPurple = _isErrorState ? SKColors.DarkRed : t_skPurple;
        var colorCyan = _isErrorState ? SKColors.Red : t_skCyan;

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
