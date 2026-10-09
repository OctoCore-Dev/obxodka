using Microsoft.Maui.Controls.Shapes;
using obxodka.Pages;
using Path = System.IO.Path;

namespace obxodka.Views;

public sealed partial class VpnView : ContentView, IDisposable
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

    private WeakReference<MainPage>? _parentRef;
    private MainPage? ParentPage => _parentRef is not null && _parentRef.TryGetTarget(out var target) ? target : null;
    private IVpnService _vpnService = null!;
    private ApiService _apiService = null!;
    private bool _isBusy;
    private bool _isErrorState;
    private string? _activeConnectedNode;
    private string? _lastErrorMessage;
    private VpnServerDto? _selectedServer;
    private List<VpnServerDto> _cachedServers = [];
    private readonly Dictionary<string, long> _serverPings = [];
    private readonly Stopwatch _loaderStopwatch = new();
    private IDispatcherTimer? _loaderTimer;
    private IDispatcherTimer? _graphAnimTimer;
    private double _targetSpeedUp, _targetSpeedDown;
    private double _smoothSpeedUp, _smoothSpeedDown;
    private double _smoothMaxSpeed = 1024.0;
    private float _pulsePhase;
    private float _lastGraphTickTime;
    private float _lastSampleTime;

    private readonly SKPaint _polyGlowPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 3.5f,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8f)
    };
    private readonly SKPaint _polyCorePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1.6f
    };
    private readonly SKPaint _crystalAmbientPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 26f)
    };
    private readonly SKPaint _reactorSparkPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };
    private readonly SKPaint _reactorSparkGlowPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f)
    };
    private readonly SKPaint _reactorTrackPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1.5f
    };
    private readonly SKPaint _shockwavePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke
    };
    private readonly SKPaint _auraPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };
    private float _shockwaveProgress = -1f;
    private bool _shockwaveIsConnect;
    private float _loaderAngle;
    private float _reactorTransitionFactor;
    private float _errorTransitionFactor;

    private static readonly (float speed, float delay, float ox, float oy, float size, int sides)[] t_polyConfigs =
    [
        ( 1.0f,   0f, 0.5f, 0.5f, 0.76f, 5),
        (-1.0f,   0f, 0.5f, 0.5f, 0.66f, 6),
        ( 1.5f,  60f, 0.5f, 0.6f, 0.56f, 5),
        (-1.5f, -60f, 0.4f, 0.4f, 0.46f, 4),
        ( 2.0f, 120f, 0.6f, 0.4f, 0.38f, 6),
    ];

    private readonly SKPaint _graphStrokeUpPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _graphStrokeDownPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2.5f, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _graphFillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _graphDotGlowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _graphDotSolidPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

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

        Unloaded += (_, _) => Dispose();
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
        _parentRef = new WeakReference<MainPage>(parent);
        _vpnService = vpnService;
        _apiService = apiService;

        UpdateRayIndicator();
        UpdateActiveNode();
        LoadSavedServerSelection();
        _ = RefreshServerListAsync();

        _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred -= HandleVpnError;
        _vpnService.OnLogUpdated -= HandleVpnLog;
        OctopusEngine.Current.OnStatusMessage -= HandleVpnLog;
        OctopusEngine.Current.OnPingUpdated -= HandlePingUpdated;

        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        _vpnService.OnLogUpdated += HandleVpnLog;
        OctopusEngine.Current.OnStatusMessage += HandleVpnLog;
        OctopusEngine.Current.OnPingUpdated += HandlePingUpdated;

        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
        OctopusEngine.Current.OnTrafficUpdated += OnTrafficUpdated;

        PlatformServices.Notification.ReconnectRequested -= HandleNotificationReconnect;
        PlatformServices.Notification.ReconnectRequested += HandleNotificationReconnect;
    }

    public void UnsubscribeEvents()
    {
        if (_vpnService is not null)
        {
            _vpnService.OnStateChanged -= HandleAppVpnStateChanged;
            _vpnService.OnErrorOccurred -= HandleVpnError;
            _vpnService.OnLogUpdated -= HandleVpnLog;
        }

        OctopusEngine.Current.OnStatusMessage -= HandleVpnLog;
        OctopusEngine.Current.OnPingUpdated -= HandlePingUpdated;
        OctopusEngine.Current.OnTrafficUpdated -= OnTrafficUpdated;
        PlatformServices.Notification.ReconnectRequested -= HandleNotificationReconnect;

        StopLoaderAnimation(animateExit: false);
        StopGraphAnimation();
        DisposeAnimatedButtonResources();
        _ = this.AbortAnimation("ReactorMorph");
        _ = this.AbortAnimation("ErrorMorph");
        _ = this.AbortAnimation("ButtonShockwave");
    }

    public void Dispose()
    {
        UnsubscribeEvents();
        _polyGlowPaint.MaskFilter?.Dispose();
        _polyGlowPaint.Dispose();
        _polyCorePaint.Dispose();
        _crystalAmbientPaint.MaskFilter?.Dispose();
        _crystalAmbientPaint.Dispose();
        _reactorSparkPaint.Dispose();
        _reactorSparkGlowPaint.MaskFilter?.Dispose();
        _reactorSparkGlowPaint.Dispose();
        _reactorTrackPaint.Dispose();
        _shockwavePaint.Dispose();
        _auraPaint.Dispose();
        _graphStrokeUpPaint.Dispose();
        _graphStrokeDownPaint.Dispose();
        _graphFillPaint.Dispose();
        _graphDotGlowPaint.Dispose();
        _graphDotSolidPaint.Dispose();
        _buttonAnimatedCurrent?.Dispose();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedIdle?.Dispose();
        _buttonAnimatedIdle = null;
        _buttonAnimatedConnecting?.Dispose();
        _buttonAnimatedConnecting = null;
        _buttonAnimatedActive?.Dispose();
        _buttonAnimatedActive = null;
        _buttonAnimatedError?.Dispose();
        _buttonAnimatedError = null;
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;
        _parentRef = null;
        GC.SuppressFinalize(this);
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

    private static void UpdateRayIndicator()
    {
    }

    private static string GetServerFlag(VpnServerDto server) =>
        !string.IsNullOrWhiteSpace(server.Flag) ? server.Flag : "🌐";

    private static string GetServerProvider(VpnServerDto server) =>
        !string.IsNullOrWhiteSpace(server.Provider) ? server.Provider : "Vultr";

    private static string GetServerRole(VpnServerDto server) =>
        string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase) ? "Main" : "Worker";

    private void LoadSavedServerSelection()
    {
        var savedIp = Preferences.Get("selected_server_ip", string.Empty);
        if (!string.IsNullOrEmpty(savedIp))
        {
            var savedLocation = Preferences.Get("selected_server_location", "Швеция, Стокгольм");
            var savedProvider = Preferences.Get("selected_server_provider", "Vultr");
            var savedFlag = Preferences.Get("selected_server_flag", "🇸🇪");
            var savedRole = Preferences.Get("selected_server_role", savedIp == "70.34.201.253" ? "main" : "worker");
            var savedCertHash = Preferences.Get("selected_server_cert_hash", "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=");
            _selectedServer = new VpnServerDto(savedIp, 443, savedLocation, true, 10, savedCertHash, savedProvider, savedFlag, savedRole);
        }
        UpdateSelectedServerDisplay();
    }

    private void UpdateSelectedServerDisplay()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedServerFlagLabel is null || SelectedServerLocationLabel is null || SelectedServerProviderLabel is null)
            {
                return;
            }

            if (_selectedServer is null)
            {
                SelectedServerFlagLabel.Text = "🇸🇪";
                SelectedServerLocationLabel.Text = "Швеция, Стокгольм";
                SelectedServerProviderLabel.Text = "Vultr • Main";
                SelectedServerLatencyLabel.Text = "Online";
                return;
            }

            SelectedServerFlagLabel.Text = GetServerFlag(_selectedServer);
            SelectedServerLocationLabel.Text = _selectedServer.Location;
            SelectedServerProviderLabel.Text = $"{GetServerProvider(_selectedServer)} • {GetServerRole(_selectedServer)}";
            SelectedServerLatencyLabel.Text = _serverPings.TryGetValue(_selectedServer.Ip, out var pingMs)
                ? $"{pingMs} ms"
                : "Online";
        });
    }

    private async Task RefreshServerListAsync()
    {
        try
        {
            var (success, servers, _) = await _apiService.GetServersAsync();
            if (success && servers is { Count: > 0 })
            {
                _cachedServers = servers;
            }
            else if (_cachedServers.Count == 0)
            {
                _cachedServers =
                [
                    new VpnServerDto("70.34.201.253", 443, "Швеция, Стокгольм", true, 10, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=", "Vultr", "🇸🇪", "main"),
                    new VpnServerDto("70.34.246.53", 443, "Польша, Варшава", true, 8, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=", "Vultr", "🇵🇱", "worker")
                ];
            }

            if (_selectedServer is null && _cachedServers.Count > 0)
            {
                _selectedServer = _cachedServers[0];
            }
            else if (_selectedServer is not null && _cachedServers.Any(s => s.Ip == _selectedServer.Ip))
            {
                _selectedServer = _cachedServers.First(s => s.Ip == _selectedServer.Ip);
            }

            UpdateSelectedServerDisplay();
            PopulateServerList();
            _ = Task.Run(PingCachedServersAsync);
        }
        catch
        {
        }
    }

    private async Task PingCachedServersAsync()
    {
        var tasks = _cachedServers.Select(async s =>
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using var cts = new CancellationTokenSource(2500);
                using var client = new TcpClient();
                await client.ConnectAsync(s.Ip, s.Port > 0 ? s.Port : 443, cts.Token).AsTask();
                sw.Stop();
                _serverPings[s.Ip] = sw.ElapsedMilliseconds;
            }
            catch
            {
                sw.Stop();
            }
        });

        await Task.WhenAll(tasks);
        UpdateSelectedServerDisplay();
        PopulateServerList();
    }

    private void PopulateServerList()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (ServerItemsContainer is null)
            {
                return;
            }

            ServerItemsContainer.Children.Clear();
            var currentSelectedIp = _selectedServer?.Ip ?? (_cachedServers.Count > 0 ? _cachedServers[0].Ip : string.Empty);

            foreach (var server in _cachedServers)
            {
                var isSelected = string.Equals(server.Ip, currentSelectedIp, StringComparison.OrdinalIgnoreCase);

                var card = new Border
                {
                    Padding = new Thickness(14, 10),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
                    StrokeThickness = isSelected ? 1.5 : 1.0,
                    BackgroundColor = isSelected
                        ? (Application.Current?.Resources.TryGetValue("BgElevated", out var bgEl) == true ? (Color)bgEl : Color.FromArgb("#1E293B"))
                        : (Application.Current?.Resources.TryGetValue("BgCard", out var bgCd) == true ? (Color)bgCd : Color.FromArgb("#0F172A")),
                    Stroke = isSelected
                        ? (Application.Current?.Resources.TryGetValue("Primary", out var pColor) == true ? (Color)pColor : Color.FromArgb("#7C3AED"))
                        : (Application.Current?.Resources.TryGetValue("BorderMedium", out var bColor) == true ? (Color)bColor : Color.FromArgb("#334155"))
                };

                var grid = new Grid
                {
                    ColumnDefinitions =
                    [
                        new ColumnDefinition(GridLength.Auto),
                        new ColumnDefinition(GridLength.Star),
                        new ColumnDefinition(GridLength.Auto)
                    ],
                    ColumnSpacing = 12,
                    VerticalOptions = LayoutOptions.Center
                };

                var flagLabel = new Label
                {
                    Text = GetServerFlag(server),
                    FontSize = 22,
                    VerticalOptions = LayoutOptions.Center
                };
                grid.Add(flagLabel, 0, 0);

                var infoStack = new VerticalStackLayout
                {
                    Spacing = 3,
                    VerticalOptions = LayoutOptions.Center
                };

                var nameLabel = new Label
                {
                    Text = server.Location,
                    FontFamily = "AppFontBold",
                    FontSize = 13,
                    TextColor = Application.Current?.Resources.TryGetValue("TextPrimary", out var tp) == true ? (Color)tp : Colors.White
                };
                infoStack.Children.Add(nameLabel);

                var badgeStack = new HorizontalStackLayout
                {
                    Spacing = 6,
                    VerticalOptions = LayoutOptions.Center
                };

                var providerBadge = new Border
                {
                    Padding = new Thickness(5, 1),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                    StrokeThickness = 0.6,
                    BackgroundColor = Application.Current?.Resources.TryGetValue("AccentDim", out var ad) == true ? (Color)ad : Color.FromArgb("#1A00E5FF"),
                    Stroke = Application.Current?.Resources.TryGetValue("Accent", out var ac) == true ? (Color)ac : Color.FromArgb("#00E5FF")
                };

                var providerLabel = new Label
                {
                    Text = GetServerProvider(server),
                    FontFamily = "AppFontMedium",
                    FontSize = 9,
                    TextColor = Application.Current?.Resources.TryGetValue("Accent", out var act) == true ? (Color)act : Color.FromArgb("#00E5FF")
                };
                providerBadge.Content = providerLabel;
                badgeStack.Children.Add(providerBadge);

                var isMainRole = string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase);
                var roleBadge = new Border
                {
                    Padding = new Thickness(5, 1),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                    StrokeThickness = 0.6,
                    BackgroundColor = isMainRole ? Color.FromArgb("#267C3AED") : Color.FromArgb("#1A38BDF8"),
                    Stroke = isMainRole ? Color.FromArgb("#A78BFA") : Color.FromArgb("#38BDF8")
                };

                var roleLabel = new Label
                {
                    Text = GetServerRole(server),
                    FontFamily = "AppFontMedium",
                    FontSize = 9,
                    TextColor = isMainRole ? Color.FromArgb("#C4B5FD") : Color.FromArgb("#7DD3FC")
                };
                roleBadge.Content = roleLabel;
                badgeStack.Children.Add(roleBadge);

                var pingDot = new BoxView
                {
                    WidthRequest = 5,
                    HeightRequest = 5,
                    CornerRadius = 2.5,
                    VerticalOptions = LayoutOptions.Center,
                    Color = Application.Current?.Resources.TryGetValue("Success", out var sc) == true ? (Color)sc : Color.FromArgb("#10B981")
                };
                badgeStack.Children.Add(pingDot);

                var pingText = _serverPings.TryGetValue(server.Ip, out var pingMs) ? $"{pingMs} ms" : "Онлайн";
                var pingLabel = new Label
                {
                    Text = pingText,
                    FontFamily = "AppFontMedium",
                    FontSize = 10,
                    TextColor = Application.Current?.Resources.TryGetValue("Success", out var sct) == true ? (Color)sct : Color.FromArgb("#10B981"),
                    VerticalOptions = LayoutOptions.Center
                };
                badgeStack.Children.Add(pingLabel);

                infoStack.Children.Add(badgeStack);
                grid.Add(infoStack, 1, 0);

                var statusIcon = new MauiIcon
                {
                    Icon = isSelected ? FluentIcons.Checkmark24 : FluentIcons.Circle24,
                    IconColor = isSelected
                        ? (Application.Current?.Resources.TryGetValue("Success", out var sic) == true ? (Color)sic : Color.FromArgb("#10B981"))
                        : (Application.Current?.Resources.TryGetValue("BorderMedium", out var bic) == true ? (Color)bic : Color.FromArgb("#475569")),
                    IconSize = isSelected ? 20 : 18,
                    VerticalOptions = LayoutOptions.Center
                };
                grid.Add(statusIcon, 2, 0);

                card.Content = grid;

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (s, e) =>
                {
                    _ = card.BounceClickAsync();
                    _selectedServer = server;
                    Preferences.Set("selected_server_ip", server.Ip);
                    Preferences.Set("selected_server_location", server.Location);
                    Preferences.Set("selected_server_provider", GetServerProvider(server));
                    Preferences.Set("selected_server_flag", GetServerFlag(server));
                    if (!string.IsNullOrEmpty(server.Role))
                    {
                        Preferences.Set("selected_server_role", server.Role);
                    }
                    if (!string.IsNullOrEmpty(server.CertHash))
                    {
                        Preferences.Set("selected_server_cert_hash", server.CertHash);
                    }
                    UpdateSelectedServerDisplay();
                    await CloseServerModalAsync();
                };
                card.GestureRecognizers.Add(tap);

                ServerItemsContainer.Children.Add(card);
            }
        });
    }

    private async Task OpenServerModalAsync()
    {
        if (_cachedServers.Count == 0)
        {
            await RefreshServerListAsync();
        }
        else
        {
            PopulateServerList();
            _ = Task.Run(PingCachedServersAsync);
        }

        ServerModalOverlay.IsVisible = true;
        ServerModalOverlay.InputTransparent = false;
        ServerModalCard.Scale = 0.95;
        ServerModalCard.Opacity = 0.0;

        _ = ServerModalOverlay.FadeToAsync(1.0, 200, Easing.CubicOut);
        _ = ServerModalCard.FadeToAsync(1.0, 200, Easing.CubicOut);
        _ = await ServerModalCard.ScaleToAsync(1.0, 250, Easing.CubicOut);
    }

    private async Task CloseServerModalAsync()
    {
        ServerModalOverlay.InputTransparent = true;
        _ = ServerModalCard.ScaleToAsync(0.95, 180, Easing.CubicIn);
        _ = ServerModalCard.FadeToAsync(0.0, 180, Easing.CubicIn);
        _ = await ServerModalOverlay.FadeToAsync(0.0, 180, Easing.CubicIn);
        ServerModalOverlay.IsVisible = false;
    }

    private void OnServerModalBackdropTapped(object? sender, EventArgs e) =>
        _ = CloseServerModalAsync();

    private void OnCloseServerModalClicked(object? sender, EventArgs e) =>
        _ = CloseServerModalAsync();

    private async void OnRefreshServersClickedAsync(object? sender, EventArgs e)
    {
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }
        await RefreshServerListAsync();
    }

    private async void OnServerSelectorTappedAsync(object? sender, EventArgs e)
    {
        if (ServerSelectorButton is not null)
        {
            _ = ServerSelectorButton.BounceClickAsync();
        }
        await OpenServerModalAsync();
    }

    private void HandleVpnLog(string logMsg) =>
        MainThread.BeginInvokeOnMainThread(() => StatusLabel.Text = logMsg);

    private void HandleNotificationReconnect()
    {
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (ParentPage is not null)
            {
                await ParentPage.SwitchTabAsync("vpn");
            }

            if (_vpnService.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
            {
                OnConnectClickedAsync(null, EventArgs.Empty);
            }
        });
    }

    public void UpdateActiveNode(string? host = null)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_vpnService == null || _vpnService.CurrentState == AppVpnState.Disconnected)
            {
                NodeHostLabel?.Text = "Нет подключения";
                return;
            }

            if (!string.IsNullOrWhiteSpace(host))
            {
                _activeConnectedNode = host;
            }

            var displayHost = _activeConnectedNode;

            if (string.IsNullOrWhiteSpace(displayHost))
            {
                displayHost = Uri.TryCreate(AppConfig.ApiBaseUrl, UriKind.Absolute, out var uri)
                    ? uri.Host
                    : AppConfig.ApiBaseUrl;
            }

            if (string.IsNullOrWhiteSpace(displayHost))
            {
                displayHost = "obxodka.one";
            }

            NodeHostLabel?.Text = displayHost;
        });
    }

    private void UpdateIpStatusDisplay(string text, Color textColor, Color iconColor)
    {
        IpAddressLabel.Text = text;
        IpAddressLabel.TextColor = textColor;
        IpStatusIcon.IconColor = iconColor;
    }

    private async void OnCardIpTappedAsync(object? sender, EventArgs e)
    {
        var ip = OctopusEngine.Current.AssignedIp;
        if (!string.IsNullOrWhiteSpace(ip) && ip != "0.0.0.0")
        {
            await Clipboard.Default.SetTextAsync(ip);
            var prevText = IpAddressLabel.Text;
            var prevColor = IpAddressLabel.TextColor;
            IpAddressLabel.Text = "Скопировано!";
            IpAddressLabel.TextColor = t_greenText;
            await Task.Delay(1200);
            if (OctopusEngine.Current.IsConnected)
            {
                IpAddressLabel.Text = OctopusEngine.Current.AssignedIp ?? prevText;
                IpAddressLabel.TextColor = prevColor;
            }
        }
    }

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

        PlatformServices.Notification.ShowUnexpectedDisconnectNotification();

        var friendlyMessage = FormatUserFriendlyError(err);
        _lastErrorMessage = friendlyMessage;

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            StatusLabel.Text = friendlyMessage;
            StatusLabel.TextColor = Colors.Red;
            try
            {
                if (ParentPage is not null)
                {
                    await ParentPage.DisplayAlertAsync("Сбой сети", friendlyMessage, "OK");
                }
            }
            catch { }
        });
    }

    private static string FormatUserFriendlyError(string rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return "Не удалось установить соединение с сервером.";
        }

        if (rawError.Contains("Таймаут", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("timed out", StringComparison.OrdinalIgnoreCase))
        {
            return "Не удалось подключиться к серверу (таймаут ответа).\n\nВозможные причины:\n• Блокировка TLS/протокола провайдером\n• Нестабильный интернет\n\nПопробуйте повторить попытку.";
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
            return "Сервер временно недоступен или связь была прервана сетью.\n\nПожалуйста, проверьте интернет-соединение или повторите попытку.";
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
        PlatformThrottler.Post("ui:ping", TimeSpan.FromMilliseconds(250), () =>
        {
            if (OctopusEngine.Current is { IsConnected: true })
            {
                UpdateIpStatusDisplay(OctopusEngine.Current.AssignedIp ?? "Подключен", Colors.White, t_cyanAccent);
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
                    _errorTransitionFactor = 0f;
                    StopLoaderAnimation(animateExit: true);
                    StopGraphAnimation();
                    ResetPingIndicators();
                    TriggerShockwave(isConnect: false);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, t_grayText, t_grayText);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync(VpnStatusMessages.Disconnected, VpnStatusMessages.StartAction, AppVpnState.Disconnected);
                    ParentPage?.NotifyVpnDisconnected();
                    break;

                case AppVpnState.Connected:
                    _isBusy = false;
                    _isErrorState = false;
                    _errorTransitionFactor = 0f;
                    StartLoaderAnimation(animateEntrance: false);
                    StartGraphAnimation();
                    AnimateReactorMorph(1.0f, 650);
                    TriggerShockwave(isConnect: true);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(OctopusEngine.Current.AssignedIp ?? VpnStatusMessages.Connected, Colors.White, t_cyanAccent);
                    UpdateActiveNode(_activeConnectedNode);
                    ConnectButtonCore.IsEnabled = true;
                    UpdateRayIndicator();
                    await SetNeonStateAsync(VpnStatusMessages.Protected, VpnStatusMessages.StopAction, AppVpnState.Connected);
                    ParentPage?.NotifyVpnConnected();
                    break;

                case AppVpnState.Error:
                    _isBusy = false;
                    _isErrorState = true;
                    StopGraphAnimation();
                    ResetPingIndicators();
                    AnimateErrorMorph(1.0f, 350);
                    TriggerShockwave(isConnect: false);
                    OuterAura.IsVisible = true;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, t_grayText, t_grayText);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    PlatformServices.Notification.ShowUnexpectedDisconnectNotification();
                    var errDisplay = !string.IsNullOrWhiteSpace(_lastErrorMessage) ? _lastErrorMessage : VpnStatusMessages.DefaultConnectionError;
                    await SetNeonStateAsync(errDisplay, VpnStatusMessages.Retry, AppVpnState.Error);
                    ParentPage?.NotifyVpnDisconnected();
                    StopLoaderAnimation(animateExit: true);
                    break;

                case AppVpnState.Connecting:
                case AppVpnState.Reconnecting:
                    _isErrorState = false;
                    _errorTransitionFactor = 0f;
                    _reactorTransitionFactor = 0f;
                    ResetPingIndicators();
                    StartLoaderAnimation(animateEntrance: true);
                    ConnectButtonCore.IsEnabled = false;
                    UpdateIpStatusDisplay(VpnStatusMessages.IpAcquiring, t_cyanAccent, t_cyanAccent);
                    UpdateActiveNode();
                    await SetNeonStateAsync(VpnStatusMessages.Connecting, VpnStatusMessages.PleaseWait, state);
                    break;

                case AppVpnState.Disconnecting:
                    StopGraphAnimation();
                    ResetPingIndicators();
                    ConnectButtonCore.IsEnabled = false;
                    UpdateIpStatusDisplay(VpnStatusMessages.Disconnecting, t_grayText, t_grayText);
                    NodeHostLabel.Text = VpnStatusMessages.Disconnecting;
                    StatusLabel.Text = VpnStatusMessages.Disconnecting;
                    ConnectButtonText.Text = VpnStatusMessages.PleaseWait;
                    _ = UpdateCustomButtonStateAsync(AppVpnState.Disconnecting);
                    AnimateReactorMorph(0.0f, 350);
                    SafeScaleTo(LoaderCanvas, 0.35, 400, Easing.CubicIn);
                    SafeFadeTo(LoaderCanvas, 0, 350, Easing.CubicIn);
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
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.05 : 1.12;
            SafeScaleTo(LoaderCanvas, targetScale, 650, Easing.SpringOut);
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

        _lastErrorMessage = null;
        await ConnectButtonCore.BounceClickAsync();
        try
        {
#if ANDROID
            var isDisclosureAccepted = Preferences.Get("VpnDisclosureAccepted", false);
            if (!isDisclosureAccepted)
            {
                var accepted = ParentPage is not null && await ParentPage.DisplayAlertAsync(
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
                if (ParentPage is not null)
                {
                    await ParentPage.DisplayAlertAsync("Нет интернета", "Отсутствует подключение к интернету. Проверьте сеть и повторите попытку.", "OK");
                }
                return;
            }

            var auditResult = await PlatformServices.CertificateAudit.CheckCertificatesAsync();
            if (auditResult.HasUntrustedRoot)
            {
                var platformInstructions = DeviceInfo.Platform == DevicePlatform.Android
                    ? "Как удалить:\n1. В открывшихся настройках выберите «Надежные сертификаты» (или «Хранилище учетных данных»).\n2. Перейдите во вкладку «Пользователь».\n3. Нажмите на сертификат и выберите «Удалить»."
                    : "Как удалить:\n1. В открывшемся окне «certmgr» раскройте «Доверенные корневые центры сертификации» -> «Сертификаты».\n2. Найдите сертификат, нажмите правой кнопкой мыши -> «Удалить».";

                var openSettings = ParentPage is not null && await ParentPage.DisplayAlertAsync(
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

            if ((ParentPage?.RemainingSeconds ?? 0) <= 0)
            {
                if (ParentPage is not null)
                {
                    await ParentPage.DisplayAlertAsync("Внимание", "Нет доступного времени.", "OK");
                }
                return;
            }

            var conflictingVpn = _vpnService.DetectConflictingVpn();
            if (!string.IsNullOrEmpty(conflictingVpn))
            {
                if (ParentPage is not null)
                {
                    var proceed = await ParentPage.DisplayAlertAsync(
                        "Сторонний VPN",
                        $"Обнаружен сторонний VPN ({conflictingVpn}).\n\nЕсли вы уже отключили тот VPN, Обходка автоматически нейтрализует оставшийся адаптер и подключится.\n\nПродолжить?",
                        "Продолжить",
                        "Отмена");

                    if (!proceed)
                    {
                        return;
                    }
                }
            }

            StartLoaderAnimation();
            ConnectButtonCore.IsEnabled = false;
            UpdateIpStatusDisplay("Получение...", t_cyanAccent, t_cyanAccent);
            await SetNeonStateAsync("Подключение...", "ЖДИТЕ", AppVpnState.Connecting);

            var preflightError = await Task.Run(_vpnService.RunNetworkPreflightAsync);
            if (!string.IsNullOrEmpty(preflightError))
            {
                throw new InvalidOperationException(preflightError);
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
                    new VpnServerDto("70.34.201.253", 443, "Швеция, Стокгольм", true, 10, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=", "Vultr", "🇸🇪", "main"),
                    new VpnServerDto("70.34.246.53", 443, "Польша, Варшава", true, 8, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=", "Vultr", "🇵🇱", "worker")
                ];
            }

            _cachedServers = servers;

            VpnServerDto targetServer;
            if (_selectedServer is not null && servers.Any(s => s.Ip == _selectedServer.Ip))
            {
                targetServer = servers.First(s => s.Ip == _selectedServer.Ip);
            }
            else
            {
                var candidateServers = await ProbeBestServerAsync(servers);
                targetServer = candidateServers.Count > 0 ? candidateServers[0] : servers[0];
                _selectedServer = targetServer;
                UpdateSelectedServerDisplay();
            }

            var orderedCandidates = new List<VpnServerDto> { targetServer };
            orderedCandidates.AddRange(servers.Where(s => s.Ip != targetServer.Ip));

            UpdateActiveNode(targetServer.Ip);
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

            Shared.Logging.AppLogger.Log($"[VPN-TARGET] User initiated connect to: {targetServer.Location} ({targetServer.Ip}:{targetServer.Port}) | Role: {targetServer.Role ?? "worker"} | Provider: {targetServer.Provider} | Flag: {targetServer.Flag}");
            await Task.Run(async () => await _vpnService.StartVpnAsync(targetServer.Ip, targetServer.Port, orderedCandidates));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VPN CONNECT ERROR] {ex.Message}");
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            UpdateIpStatusDisplay("Не назначен", t_grayText, t_grayText);
            await SetNeonStateAsync("Не в сети", "СТАРТ", AppVpnState.Disconnected);
            var friendlyMsg = ex is SocketException or InvalidOperationException
                ? ex.Message
                : "Произошла ошибка при подключении/отключении";
            if (ParentPage is not null)
            {
                await ParentPage.DisplayAlertAsync("Ошибка", friendlyMsg, "OK");
            }
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

        string[] fallbackCandidates = [AppConfig.DirectServerIp];

        foreach (var fbHost in fallbackCandidates)
        {
            try
            {
                using var cts = new CancellationTokenSource(3000);
                var addrs = await Dns.GetHostAddressesAsync(fbHost, cts.Token);
                if (addrs.Any(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync(fbHost, 443, cts.Token).AsTask();
                    var certHash = "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=";
                    reachableServers.Add(new VpnServerDto(fbHost, 443, "Основной узел (Прямой доступ)", true, 10, certHash));
                    break;
                }
            }
            catch { }
        }

        return reachableServers.Count > 0
            ? reachableServers
            : servers.Count > 0
                ? servers
                : [new VpnServerDto(AppConfig.DirectServerIp, 443, "Основной узел (Прямой доступ)", true, 10, "xZIbvT6/B+lfJmN4F7NEnEF4uZQYdP5sXDKZqsLQS1U=")];
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

        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
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
            var dt = Math.Clamp(now - _lastGraphTickTime, 0.001f, 0.1f);
            _lastGraphTickTime = now;

            _pulsePhase += dt * 5.5f;
            if (_pulsePhase > MathF.PI * 2)
            {
                _pulsePhase -= MathF.PI * 2;
            }

            var speedLerp = 1.0 - Math.Exp(-8.0 * dt);
            _smoothSpeedUp += (_targetSpeedUp - _smoothSpeedUp) * speedLerp;
            _smoothSpeedDown += (_targetSpeedDown - _smoothSpeedDown) * speedLerp;

            lock (_graphSamples)
            {
                if (now - _lastSampleTime >= 0.05f || _graphSamples.Count == 0)
                {
                    _graphSamples.Add(new GraphSample(now, (float)_smoothSpeedUp, (float)_smoothSpeedDown));
                    _lastSampleTime = now;

                    var cutoff = now - (TimeWindowSeconds + 1.0f);
                    var removeCount = 0;
                    while (removeCount < _graphSamples.Count && _graphSamples[removeCount].Time < cutoff)
                    {
                        removeCount++;
                    }

                    if (removeCount > 0)
                    {
                        _graphSamples.RemoveRange(0, removeCount);
                    }
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

                var peakLerp = 1.0 - Math.Exp(-4.0 * dt);
                _smoothMaxSpeed += (peak - _smoothMaxSpeed) * peakLerp;
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
        _lastGraphTickTime = 0;
        _lastSampleTime = 0;
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

        PlatformThrottler.Post("ui:traffic", TimeSpan.FromMilliseconds(200), () =>
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

            using var fillShader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, h),
                [startFillColor, SKColors.Transparent],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _graphFillPaint.Shader = fillShader;
            canvas.DrawPath(fillPath, _graphFillPaint);
            _graphFillPaint.Shader = null;

            canvas.DrawPath(strokePath, strokePaint);

            var lastPt = points[^1];
            var pulseRadius = 4.5f + (1.8f * MathF.Sin(_pulsePhase));
            var alphaGlow = (byte)Math.Clamp(50 + (35 * MathF.Sin(_pulsePhase)), 0, 255);

            _graphDotGlowPaint.Color = strokeColor.WithAlpha(alphaGlow);
            canvas.DrawCircle(lastPt.X, lastPt.Y, pulseRadius, _graphDotGlowPaint);

            _graphDotSolidPaint.Color = strokeColor;
            canvas.DrawCircle(lastPt.X, lastPt.Y, 2.5f, _graphDotSolidPaint);
        }

        var skPrimaryBright = GetSkiaThemeColor("PrimaryBright", t_skGraphUp);
        var skAccent = GetSkiaThemeColor("Accent", t_skGraphDown);
        var skGraphUpFill = skPrimaryBright.WithAlpha(55);
        var skGraphDownFill = skAccent.WithAlpha(75);

        _graphStrokeUpPaint.Color = skPrimaryBright;
        _graphStrokeDownPaint.Color = skAccent;

        DrawStream(true, _graphStrokeUpPaint, skGraphUpFill, skPrimaryBright);
        DrawStream(false, _graphStrokeDownPaint, skGraphDownFill, skAccent);
    }

    public static string FormatBytes(double bytes) => FormatHelper.FormatBytes(bytes);

    private void StartLoaderAnimation(bool animateEntrance = true)
    {
        _loaderStopwatch.Restart();
        if (_loaderTimer is null)
        {
            _loaderTimer = Dispatcher.CreateTimer();
            _loaderTimer.Interval = TimeSpan.FromMilliseconds(16);
            _loaderTimer.Tick += (_, _) =>
            {
                _loaderAngle += 1.6f;
                LoaderCanvas?.InvalidateSurface();
            };
            _loaderTimer.Start();
        }

        LoaderCanvas.IsVisible = true;

        if (animateEntrance)
        {
            try
            {
                _ = LoaderCanvas.AbortAnimation("ScaleTo");
                _ = LoaderCanvas.AbortAnimation("FadeTo");
            }
            catch
            {
            }

            LoaderCanvas.Scale = 0.25;
            LoaderCanvas.Opacity = 0.0;
            SafeScaleTo(LoaderCanvas, 1.0, 450, Easing.CubicOut);
            SafeFadeTo(LoaderCanvas, 1.0, 350, Easing.CubicOut);
        }
        else
        {
            LoaderCanvas.Opacity = 1;
        }
    }

    private void StopLoaderAnimation(bool animateExit = true)
    {
        if (LoaderCanvas is null)
        {
            _loaderTimer?.Stop();
            _loaderTimer = null;
            _loaderStopwatch.Reset();
            return;
        }

        if (animateExit && LoaderCanvas.IsVisible && LoaderCanvas.Opacity > 0.05)
        {
            try
            {
                _ = LoaderCanvas.AbortAnimation("ScaleTo");
                _ = LoaderCanvas.AbortAnimation("FadeTo");
            }
            catch
            {
            }

            SafeScaleTo(LoaderCanvas, 0.25, 350, Easing.CubicIn);
            SafeFadeTo(LoaderCanvas, 0.0, 300, Easing.CubicIn, () =>
            {
                LoaderCanvas.IsVisible = false;
                _loaderTimer?.Stop();
                _loaderTimer = null;
                _loaderStopwatch.Reset();
            });
        }
        else
        {
            _loaderTimer?.Stop();
            _loaderTimer = null;
            _loaderStopwatch.Reset();
            LoaderCanvas.IsVisible = false;
            LoaderCanvas.Opacity = 0;
            LoaderCanvas.Scale = 1.0;
        }
    }

    private void AnimateReactorMorph(float target, uint duration = 500)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ReactorMorph");
                var start = _reactorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _reactorTransitionFactor = (float)v;
                    LoaderCanvas?.InvalidateSurface();
                }, start, target, Easing.CubicInOut);
                anim.Commit(this, "ReactorMorph", 16, duration, Easing.CubicInOut);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ReactorMorph] Animation error: {ex.Message}");
                _reactorTransitionFactor = target;
            }
        });
    }

    private void AnimateErrorMorph(float target, uint duration = 400)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ErrorMorph");
                var start = _errorTransitionFactor;
                var anim = new Animation(v =>
                {
                    _errorTransitionFactor = (float)v;
                    LoaderCanvas?.InvalidateSurface();
                    OuterAura?.InvalidateSurface();
                }, start, target, Easing.CubicOut);
                anim.Commit(this, "ErrorMorph", 16, duration, Easing.CubicOut);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ErrorMorph] Animation error: {ex.Message}");
                _errorTransitionFactor = target;
            }
        });
    }

    private static SKColor LerpColor(SKColor from, SKColor to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        var r = (byte)(from.Red + ((to.Red - from.Red) * t));
        var g = (byte)(from.Green + ((to.Green - from.Green) * t));
        var b = (byte)(from.Blue + ((to.Blue - from.Blue) * t));
        var a = (byte)(from.Alpha + ((to.Alpha - from.Alpha) * t));
        return new SKColor(r, g, b, a);
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

    private void TriggerShockwave(bool isConnect)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _ = this.AbortAnimation("ButtonShockwave");
                _shockwaveIsConnect = isConnect;
                _shockwaveProgress = 0f;
                var anim = new Animation(v =>
                {
                    _shockwaveProgress = (float)v;
                    OuterAura?.InvalidateSurface();
                }, 0f, 1f, Easing.CubicOut);
                anim.Commit(this, "ButtonShockwave", 16, 650, Easing.CubicOut, (v, c) =>
                {
                    _shockwaveProgress = -1f;
                    OuterAura?.InvalidateSurface();
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Shockwave] Animation error: {ex.Message}");
                _shockwaveProgress = -1f;
            }
        });
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

    private void OnPaintLoaderSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_loaderTimer is null && _vpnService?.CurrentState != AppVpnState.Connected)
        {
            return;
        }

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 6f;
        if (r <= 12f)
        {
            return;
        }

        var skPrimary = GetSkiaThemeColor("Primary", t_skPurple);
        var skAccent = GetSkiaThemeColor("Accent", t_skCyan);
        var colorPrimary = LerpColor(skPrimary, SKColors.DarkRed, _errorTransitionFactor);
        var colorAccent = LerpColor(skAccent, SKColors.Red, _errorTransitionFactor);
        var glowMult = (float)ThemeManager.CurrentGlowIntensity;

        var factor = _reactorTransitionFactor;

        if (factor <= 0.001f)
        {
            DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
        }
        else if (factor >= 0.999f)
        {
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
        }
        else
        {
            DrawQuantumHyperCrystal(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f - factor);
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, factor);
        }
    }

    private void DrawQuantumHyperCrystal(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        var ambientAlpha = (byte)Math.Clamp(55 * glowMult * alphaMultiplier, 0, 255);
        if (ambientAlpha > 0)
        {
            _crystalAmbientPaint.Color = colorPrimary.WithAlpha(ambientAlpha);
            canvas.DrawCircle(cx, cy, Math.Max(4f, r - 26f), _crystalAmbientPaint);
        }

        for (var i = 0; i < t_polyConfigs.Length; i++)
        {
            var (speed, delay, ox, oy, size, sides) = t_polyConfigs[i];
            var rot = (_loaderAngle * speed) + delay;
            var baseAlpha = 80 + (i * 20);
            var glowAlpha = (byte)Math.Clamp(baseAlpha * glowMult * alphaMultiplier, 0, 255);
            var coreAlpha = (byte)Math.Clamp((baseAlpha + 50) * Math.Min(glowMult, 1.2f) * alphaMultiplier, 0, 255);

            var polyColor = i % 2 == 0 ? colorPrimary : colorAccent;

            var pivotX = cx + ((ox - 0.5f) * r);
            var pivotY = cy + ((oy - 0.5f) * r);

            _ = canvas.Save();
            canvas.RotateDegrees(rot, pivotX, pivotY);

            using var path = MakePolygon(pivotX, pivotY, r * size, sides);

            if (glowAlpha > 0)
            {
                _polyGlowPaint.Color = polyColor.WithAlpha(glowAlpha);
                canvas.DrawPath(path, _polyGlowPaint);
            }

            _polyCorePaint.Color = polyColor.WithAlpha(coreAlpha);
            canvas.DrawPath(path, _polyCorePaint);

            canvas.Restore();
        }
    }

    private void DrawLivingReactor(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        if (alphaMultiplier <= 0.001f)
        {
            return;
        }

        var t = (float)_loaderStopwatch.Elapsed.TotalSeconds;

        var trafficBps = _smoothSpeedDown + _smoothSpeedUp;
        var trafficNormalized = Math.Clamp((float)(trafficBps / (1024.0 * 1024.0 * 2.0)), 0f, 3.5f);
        var dynamicSpeed = 1.2f + (trafficNormalized * 2.0f);

        var pulse = (MathF.Sin(t * 3f) * 0.12f) + 0.88f;
        var coreR = Math.Max(6f, r * 0.42f * pulse);

        var coreAlpha = (byte)Math.Clamp(50 * glowMult * pulse * alphaMultiplier, 0, 255);
        if (coreAlpha > 0)
        {
            _crystalAmbientPaint.Color = colorAccent.WithAlpha(coreAlpha);
            canvas.DrawCircle(cx, cy, coreR, _crystalAmbientPaint);
        }

        var trackR1 = r * 0.82f;
        var trackR2 = r * 0.58f;

        _reactorTrackPaint.Color = colorPrimary.WithAlpha((byte)Math.Clamp(35 * glowMult * alphaMultiplier, 0, 255));
        canvas.DrawCircle(cx, cy, trackR1, _reactorTrackPaint);

        _reactorTrackPaint.Color = colorAccent.WithAlpha((byte)Math.Clamp(25 * glowMult * alphaMultiplier, 0, 255));
        canvas.DrawCircle(cx, cy, trackR2, _reactorTrackPaint);

        for (var i = 0; i < 5; i++)
        {
            var baseAngle = i * (360f / 5f);
            var isOuter = i % 2 == 0;
            var trackR = isOuter ? trackR1 : trackR2;
            var dir = isOuter ? 1f : -1.25f;
            var angleDeg = baseAngle + (_loaderAngle * dynamicSpeed * dir);
            var angleRad = angleDeg * (MathF.PI / 180f);

            var sparkX = cx + (trackR * MathF.Cos(angleRad));
            var sparkY = cy + (trackR * MathF.Sin(angleRad));

            var sparkColor = isOuter ? colorAccent : colorPrimary;
            var sparkAlpha = (byte)Math.Clamp(200 * glowMult * alphaMultiplier, 0, 255);

            if (sparkAlpha > 0)
            {
                _reactorSparkGlowPaint.Color = sparkColor.WithAlpha((byte)Math.Clamp(sparkAlpha * 0.7f, 0, 255));
                canvas.DrawCircle(sparkX, sparkY, 6f, _reactorSparkGlowPaint);
            }

            _reactorSparkPaint.Color = SKColors.White.WithAlpha(sparkAlpha);
            canvas.DrawCircle(sparkX, sparkY, 2.6f, _reactorSparkPaint);
        }
    }

    private void OnPaintOuterAuraSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        var cx = e.Info.Width / 2f;
        var cy = e.Info.Height / 2f;
        var r = Math.Min(cx, cy) - 2f;
        if (r <= 5f)
        {
            return;
        }

        var skAccent = GetSkiaThemeColor("Accent", t_skCyan);
        var targetColor = _isErrorState ? SKColors.Red : skAccent;
        var color = LerpColor(skAccent, targetColor, Math.Max(_isErrorState ? 1f : 0f, _errorTransitionFactor));
        var glowMult = (float)ThemeManager.CurrentGlowIntensity;

        var baseAlpha = _vpnService?.CurrentState == AppVpnState.Connected ? 95 : 65;
        var midAlpha = _vpnService?.CurrentState == AppVpnState.Connected ? 35 : 20;

        using var auraShader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy),
            r,
            [
                color.WithAlpha((byte)Math.Clamp(baseAlpha * glowMult, 0, 255)),
                color.WithAlpha((byte)Math.Clamp(midAlpha * glowMult, 0, 255)),
                SKColors.Transparent
            ],
            [0f, 0.65f, 1f],
            SKShaderTileMode.Clamp);

        _auraPaint.Shader = auraShader;
        canvas.DrawCircle(cx, cy, r, _auraPaint);
        _auraPaint.Shader = null;

        if (_shockwaveProgress >= 0f)
        {
            var p = _shockwaveProgress;
            var shockColor = _shockwaveIsConnect ? color : SKColors.DeepPink;
            var waveAlpha = (byte)Math.Clamp((int)(240f * (1f - p) * glowMult), 0, 255);
            if (waveAlpha > 0)
            {
                var waveR1 = r * (0.28f + (0.72f * p));
                _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha);
                _shockwavePaint.StrokeWidth = Math.Max(1f, 5f * (1f - p));
                canvas.DrawCircle(cx, cy, waveR1, _shockwavePaint);

                if (p > 0.15f)
                {
                    var p2 = (p - 0.15f) / 0.85f;
                    var waveR2 = r * (0.20f + (0.80f * p2));
                    var waveAlpha2 = (byte)Math.Clamp((int)(180f * (1f - p2) * glowMult), 0, 255);
                    _shockwavePaint.Color = shockColor.WithAlpha(waveAlpha2);
                    _shockwavePaint.StrokeWidth = Math.Max(1f, 3.5f * (1f - p2));
                    canvas.DrawCircle(cx, cy, waveR2, _shockwavePaint);
                }
            }
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
        _buttonAnimatedCurrent?.Dispose();
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
        ServerSelectorButton.BackgroundColor = bgElevated;
    }

    private double _lastAllocatedW = -1;
    private double _lastAllocatedH = -1;
    private double _currentTopInset;

    public void SetHeaderTopInset(double top)
    {
        _currentTopInset = top;
        if (DeviceInfo.Platform == DevicePlatform.Android && DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            RootLayoutGrid?.Padding = new Thickness(8, 0, 8, 0);

            if (ContentGrid != null)
            {
                var topAir = Math.Max(top + 16, 50);
                ContentGrid.Padding = new Thickness(0, topAir, 0, 96);
            }
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone && RootLayoutGrid != null)
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
            OuterAura?.InvalidateSurface();
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
            ServerSelectorButton.Stroke = borderMedium;

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
}
