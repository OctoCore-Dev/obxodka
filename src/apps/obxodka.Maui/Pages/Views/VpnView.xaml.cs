using Microsoft.Maui.Controls.Shapes;
using Path = System.IO.Path;

namespace obxodka.Views;

public sealed partial class VpnView : ContentView, IDisposable
{
    private static readonly Color t_greenDot = Color.FromArgb("#10B981");
    private static readonly Color t_greenText = Color.FromArgb("#34D399");
    private static readonly Color t_amberDot = Color.FromArgb("#F59E0B");
    private static readonly Color t_redDot = Color.FromArgb("#EF4444");
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
    private bool _showIpV6;
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
    private readonly SKPaint _customButtonBitmapPaint = new()
    {
        IsAntialias = true
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
    private readonly List<SKPoint> _graphPointsBuffer = [with(256)];
    private SKShader? _cachedAuraShader;
    private float _lastAuraCx, _lastAuraCy, _lastAuraR, _lastAuraGlow;
    private SKColor _lastAuraColor, _lastAuraBloomColor;
    private byte _lastAuraBaseAlpha, _lastAuraMidAlpha;
    private SKShader? _cachedGraphUpFillShader;
    private SKShader? _cachedGraphDownFillShader;
    private float _lastGraphH;
    private SKColor _lastGraphUpColor, _lastGraphDownColor;

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
    private string? _activeCardFramePath;

    private ButtonAnimatedClip? _buttonAnimatedIdle;
    private ButtonAnimatedClip? _buttonAnimatedConnecting;
    private ButtonAnimatedClip? _buttonAnimatedActive;
    private ButtonAnimatedClip? _buttonAnimatedError;
    private ButtonAnimatedClip? _buttonAnimatedCurrent;
    private ButtonAnimatedClip? _buttonAnimatedPrev;
    private float _buttonTransitionProgress = 1f;
    private readonly Stopwatch _buttonAnimStopwatch = new();
    private long _lastButtonAnimTicks;
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
        OctopusEngine.Current.OnPingUpdated -= HandlePingUpdated;

        _vpnService.OnStateChanged += HandleAppVpnStateChanged;
        _vpnService.OnErrorOccurred += HandleVpnError;
        _vpnService.OnLogUpdated += HandleVpnLog;
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
        _customButtonBitmapPaint.Dispose();
        _cachedAuraShader?.Dispose();
        _cachedGraphUpFillShader?.Dispose();
        _cachedGraphDownFillShader?.Dispose();
        _graphStrokeUpPaint.Dispose();
        _graphStrokeDownPaint.Dispose();
        _graphFillPaint.Dispose();
        _graphDotGlowPaint.Dispose();
        _graphDotSolidPaint.Dispose();
        _buttonAnimatedCurrent?.Dispose();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedPrev?.Dispose();
        _buttonAnimatedPrev = null;
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
        !string.IsNullOrWhiteSpace(server.Flag) ? server.Flag : "";

    private static string GetServerProvider(VpnServerDto server) =>
        !string.IsNullOrWhiteSpace(server.Provider) ? server.Provider : "Vultr";

    private static string GetServerRole(VpnServerDto server) =>
        string.Equals(server.Role, "shield", StringComparison.OrdinalIgnoreCase) ? "Shield" :
        (string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase) ? "Main" : "Worker");

    private static string GetStatusTextForState(AppVpnState state, string fallback)
    {
        var st = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        return st is null
            ? fallback
            : state switch
            {
                AppVpnState.Disconnected => !string.IsNullOrWhiteSpace(st.Disconnected) ? st.Disconnected : fallback,
                AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => !string.IsNullOrWhiteSpace(st.Connecting) ? st.Connecting : fallback,
                AppVpnState.Connected => !string.IsNullOrWhiteSpace(st.Connected) ? st.Connected : fallback,
                AppVpnState.Error => !string.IsNullOrWhiteSpace(st.Error) ? st.Error : fallback,
                _ => fallback
            };
    }

    private static string GetButtonTextForState(AppVpnState state, string fallback)
    {
        var st = ThemeManager.Instance?.ActiveTheme?.Vfx?.StatusText;
        return st is null
            ? fallback
            : state switch
            {
                AppVpnState.Disconnected => !string.IsNullOrWhiteSpace(st.ButtonIdle) ? st.ButtonIdle : fallback,
                AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => !string.IsNullOrWhiteSpace(st.ButtonConnecting) ? st.ButtonConnecting : fallback,
                AppVpnState.Connected => !string.IsNullOrWhiteSpace(st.ButtonConnected) ? st.ButtonConnected : fallback,
                AppVpnState.Error => !string.IsNullOrWhiteSpace(st.ButtonIdle) ? st.ButtonIdle : fallback,
                _ => fallback
            };
    }

    private void LoadSavedServerSelection()
    {
        var cachedJson = Preferences.Get("cached_cluster_servers_json", string.Empty);
        if (!string.IsNullOrEmpty(cachedJson))
        {
            try
            {
                var cached = JsonSerializer.Deserialize(cachedJson, AppJsonContext.Default.ListVpnServerDto);
                if (cached is { Count: > 0 })
                {
                    _cachedServers = cached;
                }
            }
            catch { }
        }

        var savedIp = Preferences.Get("selected_server_ip", string.Empty);
        if (!string.IsNullOrEmpty(savedIp) && _cachedServers.Any(s => s.Ip == savedIp))
        {
            _selectedServer = _cachedServers.First(s => s.Ip == savedIp);
        }
        else if (_cachedServers.Count > 0)
        {
            _selectedServer = _cachedServers[0];
        }

        UpdateSelectedServerDisplay();
    }

    private void UpdateSelectedServerDisplay()
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (SelectedServerFlagLabel is null || SelectedServerLocationLabel is null || SelectedServerProviderLabel is null || SelectedServerFlagImage is null)
            {
                return;
            }

            var flagEmoji = _selectedServer != null ? GetServerFlag(_selectedServer) : "";
            var flagUrl = NetworkDefaults.GetFlagImageUrl(flagEmoji);

#if WINDOWS
            if (!string.IsNullOrEmpty(flagUrl))
            {
                SelectedServerFlagImage.Source = new UriImageSource
                {
                    Uri = new Uri(flagUrl),
                    CachingEnabled = true,
                    CacheValidity = TimeSpan.FromDays(30)
                };
                SelectedServerFlagImage.IsVisible = true;
                SelectedServerFlagLabel.IsVisible = false;
            }
            else
            {
                SelectedServerFlagLabel.Text = flagEmoji;
                SelectedServerFlagLabel.IsVisible = true;
                SelectedServerFlagImage.IsVisible = false;
            }
#else
            SelectedServerFlagLabel.Text = flagEmoji;
            SelectedServerFlagLabel.IsVisible = true;
            SelectedServerFlagImage.IsVisible = false;
#endif

            if (_selectedServer is null)
            {
                SelectedServerLocationLabel.Text = "Выберите сервер";
                SelectedServerProviderLabel.Text = "Нажмите для обновления";
                SelectedServerLatencyLabel.Text = "--";
                return;
            }

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
                try
                {
                    Preferences.Set("cached_cluster_servers_json", JsonSerializer.Serialize(servers, AppJsonContext.Default.ListVpnServerDto));
                }
                catch { }
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
            if (_cachedServers.Count > 0)
            {
                _ = Task.Run(PingCachedServersAsync);
            }
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

            var bgSurface = GetAppColor("BgSurface", Color.FromArgb("#161622"));
            var bgElevated = GetAppColor("BgElevated", Color.FromArgb("#202030"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));
            var textPrimary = GetAppColor("TextPrimary", Colors.White);
            var primaryColor = GetAppColor("Primary", Color.FromArgb("#7C3AED"));
            var primaryBright = GetAppColor("PrimaryBright", Color.FromArgb("#9F6FF0"));
            var primaryDim = GetAppColor("PrimaryDim", Color.FromArgb("#267C3AED"));
            var accentColor = GetAppColor("Accent", Color.FromArgb("#00E5FF"));
            var accentDim = GetAppColor("AccentDim", Color.FromArgb("#1A00E5FF"));
            var successColor = GetAppColor("Success", Color.FromArgb("#10B981"));

            ServerItemsContainer.Children.Clear();
            if (_cachedServers.Count == 0)
            {
                var retryCard = new Border
                {
                    Padding = new Thickness(14, 18),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
                    StrokeThickness = 1.0,
                    BackgroundColor = bgSurface,
                    Stroke = borderMedium
                };

                var retryStack = new VerticalStackLayout
                {
                    Spacing = 6,
                    HorizontalOptions = LayoutOptions.Center
                };

                var retryLabel = new Label
                {
                    Text = "Список серверов пуст",
                    FontSize = 13,
                    TextColor = textPrimary,
                    HorizontalTextAlignment = TextAlignment.Center
                };
                retryLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
                retryStack.Children.Add(retryLabel);

                var retrySubLabel = new Label
                {
                    Text = "Нажмите, чтобы обновить",
                    FontSize = 11,
                    TextColor = accentColor,
                    HorizontalTextAlignment = TextAlignment.Center
                };
                retrySubLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                retryStack.Children.Add(retrySubLabel);

                retryCard.Content = retryStack;

                var retryTap = new TapGestureRecognizer();
                retryTap.Tapped += async (s, e) =>
                {
                    ThemeAudioService.PlayClick();
                    _ = retryCard.BounceClickAsync();
                    await RefreshServerListAsync();
                };
                retryCard.GestureRecognizers.Add(retryTap);

                ServerItemsContainer.Children.Add(retryCard);
                return;
            }

            var currentSelectedIp = _selectedServer?.Ip ?? (_cachedServers.Count > 0 ? _cachedServers[0].Ip : string.Empty);

            foreach (var server in _cachedServers)
            {
                var isSelected = string.Equals(server.Ip, currentSelectedIp, StringComparison.OrdinalIgnoreCase);

                var card = new Border
                {
                    Padding = new Thickness(14, 10),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(14) },
                    StrokeThickness = isSelected ? 1.5 : 1.0,
                    BackgroundColor = isSelected ? bgElevated : bgSurface,
                    Stroke = isSelected ? primaryColor : borderMedium
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

                var flagEmoji = GetServerFlag(server);
                var flagUrl = NetworkDefaults.GetFlagImageUrl(flagEmoji);

                var flagBorder = new Border
                {
                    WidthRequest = 28,
                    HeightRequest = 19,
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(3) },
                    StrokeThickness = 0.8,
                    Stroke = borderMedium,
                    VerticalOptions = LayoutOptions.Center
                };

                var flagGrid = new Grid();

#if WINDOWS
                if (!string.IsNullOrEmpty(flagUrl))
                {
                    var flagImage = new Image
                    {
                        Aspect = Aspect.AspectFill,
                        Source = new UriImageSource
                        {
                            Uri = new Uri(flagUrl),
                            CachingEnabled = true,
                            CacheValidity = TimeSpan.FromDays(30)
                        }
                    };
                    flagGrid.Children.Add(flagImage);
                }
                else
                {
                    var flagFallback = new Label
                    {
                        Text = flagEmoji,
                        FontSize = 14,
                        HorizontalOptions = LayoutOptions.Center,
                        VerticalOptions = LayoutOptions.Center
                    };
                    flagGrid.Children.Add(flagFallback);
                }
#else
                var flagLabel = new Label
                {
                    Text = flagEmoji,
                    FontSize = 18,
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center,
                    Margin = new Thickness(0, -2, 0, 0)
                };
                flagGrid.Children.Add(flagLabel);
#endif
                flagBorder.Content = flagGrid;
                grid.Add(flagBorder, 0, 0);

                var infoStack = new VerticalStackLayout
                {
                    Spacing = 3,
                    VerticalOptions = LayoutOptions.Center
                };

                var nameLabel = new Label
                {
                    Text = server.Location,
                    FontSize = 13,
                    TextColor = textPrimary
                };
                nameLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontBold");
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
                    BackgroundColor = accentDim,
                    Stroke = accentColor
                };

                var providerLabel = new Label
                {
                    Text = GetServerProvider(server),
                    FontSize = 9,
                    TextColor = accentColor
                };
                providerLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                providerBadge.Content = providerLabel;
                badgeStack.Children.Add(providerBadge);

                var isShieldRole = string.Equals(server.Role, "shield", StringComparison.OrdinalIgnoreCase);
                var isMainRole = string.Equals(server.Role, "main", StringComparison.OrdinalIgnoreCase);
                var isHighlightRole = isShieldRole || isMainRole;
                var roleBadge = new Border
                {
                    Padding = new Thickness(5, 1),
                    StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(4) },
                    StrokeThickness = 0.6,
                    BackgroundColor = isHighlightRole ? primaryDim : accentDim,
                    Stroke = isHighlightRole ? primaryBright : accentColor
                };

                var roleLabel = new Label
                {
                    Text = GetServerRole(server),
                    FontSize = 9,
                    TextColor = isHighlightRole ? primaryBright : accentColor
                };
                roleLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                roleBadge.Content = roleLabel;
                badgeStack.Children.Add(roleBadge);

                var pingDot = new BoxView
                {
                    WidthRequest = 5,
                    HeightRequest = 5,
                    CornerRadius = 2.5,
                    VerticalOptions = LayoutOptions.Center,
                    Color = successColor
                };
                badgeStack.Children.Add(pingDot);

                var pingText = _serverPings.TryGetValue(server.Ip, out var pingMs) ? $"{pingMs} ms" : "Онлайн";
                var pingLabel = new Label
                {
                    Text = pingText,
                    FontSize = 10,
                    TextColor = successColor,
                    VerticalOptions = LayoutOptions.Center
                };
                pingLabel.SetDynamicResource(Label.FontFamilyProperty, "AppFontMedium");
                badgeStack.Children.Add(pingLabel);

                infoStack.Children.Add(badgeStack);
                grid.Add(infoStack, 1, 0);

                var statusIcon = new MauiIcon
                {
                    Icon = isSelected ? FluentIcons.Checkmark24 : FluentIcons.Circle24,
                    IconColor = isSelected ? successColor : borderMedium,
                    IconSize = isSelected ? 20 : 18,
                    VerticalOptions = LayoutOptions.Center
                };
                grid.Add(statusIcon, 2, 0);

                card.Content = grid;

                var tap = new TapGestureRecognizer();
                tap.Tapped += async (s, e) =>
                {
                    if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
                    {
                        return;
                    }
                    ThemeAudioService.PlayClick();
                    _ = card.BounceClickAsync();
                    var wasConnected = _vpnService?.CurrentState == AppVpnState.Connected;
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

                    if (wasConnected)
                    {
                        await SwitchServerHotSwapAsync(server);
                    }
                };
                card.GestureRecognizers.Add(tap);

                ServerItemsContainer.Children.Add(card);
            }
        });
    }

    private async Task SwitchServerHotSwapAsync(VpnServerDto newServer)
    {
        if (_isBusy || _vpnService is null)
        {
            return;
        }

        _isBusy = true;
        try
        {
            StartLoaderAnimation();
            ConnectButtonCore.IsEnabled = false;
            var accent = GetAppColor("Accent", t_cyanAccent);
            UpdateIpStatusDisplay("Переключение...", accent, accent);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Connecting, "Смена сервера..."),
                GetButtonTextForState(AppVpnState.Connecting, "ЖДИТЕ"),
                AppVpnState.Connecting);

            await _vpnService.StopVpnAsync();

            var orderedCandidates = new List<VpnServerDto> { newServer };
            orderedCandidates.AddRange(_cachedServers.Where(s => s.Ip != newServer.Ip));

            UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
            if (!string.IsNullOrWhiteSpace(newServer.CertHash))
            {
                OctopusEngine.DynamicSslPublicKeyHash = newServer.CertHash;
            }

            Shared.Logging.AppLogger.Log($"[HOT-SWAP] Seamlessly switching to server: {newServer.Location} ({newServer.Ip}:{newServer.Port})");
            await Task.Run(async () => await _vpnService.StartVpnAsync(newServer.Ip, newServer.Port, orderedCandidates));
        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[HOT-SWAP ERROR] {ex.Message}", ex);
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            var muted = GetAppColor("TextMuted", t_grayText);
            UpdateIpStatusDisplay("Не назначен", muted, muted);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Disconnected, "Не в сети"),
                GetButtonTextForState(AppVpnState.Disconnected, "СТАРТ"),
                AppVpnState.Disconnected);
            if (ParentPage is not null)
            {
                await ParentPage.DisplayAlertAsync("Смена сервера", $"Не удалось переключить сервер: {ex.Message}", "OK");
            }
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task OpenServerModalAsync()
    {
        if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
        {
            return;
        }
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

    private void OnServerModalBackdropTapped(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = CloseServerModalAsync();
    }

    private void OnCloseServerModalClicked(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        _ = CloseServerModalAsync();
    }

    private async void OnRefreshServersClickedAsync(object? sender, EventArgs e)
    {
        ThemeAudioService.PlayClick();
        if (sender is VisualElement ve)
        {
            _ = ve.BounceClickAsync();
        }
        await RefreshServerListAsync();
    }

    private async void OnServerSelectorTappedAsync(object? sender, EventArgs e)
    {
        if (_isBusy || _vpnService?.CurrentState is AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting)
        {
            return;
        }

        ThemeAudioService.PlayClick();
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

            if (_vpnService is null || _vpnService.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
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

            _activeConnectedNode = !string.IsNullOrWhiteSpace(host) &&
                !IPAddress.TryParse(host, out _) &&
                !host.Contains("obxodka", StringComparison.OrdinalIgnoreCase) &&
                !host.Contains("octocore", StringComparison.OrdinalIgnoreCase)
                ? host
                : OctopusEngine.Current?.ActiveSni ?? AppSecrets.AllowedSniPool[0];

            var displayHost = _activeConnectedNode;
            if (string.IsNullOrWhiteSpace(displayHost) ||
                IPAddress.TryParse(displayHost, out _) ||
                displayHost.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
                displayHost.Contains("octocore", StringComparison.OrdinalIgnoreCase))
            {
                displayHost = OctopusEngine.Current?.ActiveSni ?? AppSecrets.AllowedSniPool[0];
            }

            NodeHostLabel?.Text = displayHost;
        });
    }

    private void UpdateIpStatusDisplay(string text, Color textColor, Color iconColor)
    {
        if (OctopusEngine.Current is { IsConnected: true })
        {
            var v4 = OctopusEngine.Current.AssignedIp;
            var v6 = OctopusEngine.Current.AssignedIpV6;
            var ip = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
            var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";
            IpAddressLabel.Text = !string.IsNullOrWhiteSpace(ip) ? ip : text;
            IpAddressLabel.TextColor = textColor;
            IpStatusIcon.IconColor = iconColor;
            IpVersionBadge.IsVisible = true;
            IpVersionLabel.Text = tag;
            IpCopyButton.IsVisible = true;
        }
        else
        {
            _showIpV6 = false;
            IpAddressLabel.Text = text;
            IpAddressLabel.TextColor = textColor;
            IpStatusIcon.IconColor = iconColor;
            IpVersionBadge.IsVisible = false;
            IpVersionLabel.Text = "v4";
            IpCopyButton.IsVisible = false;
        }
    }

    private void OnToggleIpVersionClicked(object? sender, EventArgs e)
    {
        if (OctopusEngine.Current is not { IsConnected: true })
        {
            return;
        }

        ThemeAudioService.PlayClick();
        _showIpV6 = !_showIpV6;
        var v4 = OctopusEngine.Current.AssignedIp;
        var v6 = OctopusEngine.Current.AssignedIpV6;
        var currentIp = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
        var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";

        IpAddressLabel.Text = currentIp ?? "Подключен";
        IpAddressLabel.TextColor = GetAppColor("TextPrimary", Colors.White);
        IpVersionBadge.IsVisible = true;
        IpVersionLabel.Text = tag;
        IpCopyButton.IsVisible = true;
    }

    private async void OnCopyIpClickedAsync(object? sender, EventArgs e)
    {
        if (OctopusEngine.Current is not { IsConnected: true })
        {
            return;
        }

        var v4 = OctopusEngine.Current.AssignedIp;
        var v6 = OctopusEngine.Current.AssignedIpV6;
        var currentIp = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? v6 : v4;
        var tag = _showIpV6 && !string.IsNullOrWhiteSpace(v6) ? "v6" : "v4";

        if (!string.IsNullOrWhiteSpace(currentIp) && currentIp != "0.0.0.0")
        {
            ThemeAudioService.PlayClick();
            await Clipboard.Default.SetTextAsync(currentIp);
            var prevText = IpAddressLabel.Text;
            var prevColor = IpAddressLabel.TextColor;
            var successColor = GetAppColor("Success", t_greenText);
            IpAddressLabel.Text = $"{tag.ToUpperInvariant()} скопирован!";
            IpAddressLabel.TextColor = successColor;
            IpCopyIcon.Icon = FluentIcons.Checkmark20;
            IpCopyIcon.IconColor = successColor;
            await Task.Delay(1100);
            if (OctopusEngine.Current.IsConnected)
            {
                var refreshedV4 = OctopusEngine.Current.AssignedIp;
                var refreshedV6 = OctopusEngine.Current.AssignedIpV6;
                var refreshedIp = _showIpV6 && !string.IsNullOrWhiteSpace(refreshedV6) ? refreshedV6 : refreshedV4;
                IpAddressLabel.Text = refreshedIp ?? prevText;
                IpAddressLabel.TextColor = prevColor;
                IpCopyIcon.Icon = FluentIcons.Copy20;
                IpCopyIcon.IconColor = GetAppColor("TextSecondary", t_grayText);
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
            StatusLabel.TextColor = GetAppColor("Error", t_redDot);
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
        var muted = GetAppColor("TextMuted", t_grayText);
        var border = GetAppColor("BorderMedium", t_grayStroke);
        PingValueLabel.Text = "-- ms";
        PingDot.Color = muted;
        PingBadge.Stroke = border;
        PingValueLabel.TextColor = muted;
    }

    private void HandlePingUpdated(long rtt)
    {
        PlatformThrottler.Post("ui:ping", TimeSpan.FromMilliseconds(250), () =>
        {
            if (OctopusEngine.Current is { IsConnected: true })
            {
                UpdateIpStatusDisplay(
                    OctopusEngine.Current.AssignedIp ?? "Подключен",
                    GetAppColor("TextPrimary", Colors.White),
                    GetAppColor("Accent", t_cyanAccent));
                PingValueLabel.Text = $"{rtt} ms";
                if (rtt < 75)
                {
                    var sc = GetAppColor("Success", t_greenDot);
                    PingDot.Color = sc;
                    PingBadge.Stroke = sc;
                    PingValueLabel.TextColor = sc;
                }
                else if (rtt < 150)
                {
                    var wc = GetAppColor("Warning", t_amberDot);
                    PingDot.Color = wc;
                    PingBadge.Stroke = wc;
                    PingValueLabel.TextColor = wc;
                }
                else
                {
                    var ec = GetAppColor("Error", t_redDot);
                    PingDot.Color = ec;
                    PingBadge.Stroke = ec;
                    PingValueLabel.TextColor = ec;
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
            ServerSelectorButton.Opacity = state is AppVpnState.Connected or AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting ? 0.55 : 1.0;
            var muted = GetAppColor("TextMuted", t_grayText);
            var textPrimary = GetAppColor("TextPrimary", Colors.White);
            var accent = GetAppColor("Accent", t_cyanAccent);

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
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, muted, muted);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    await SetNeonStateAsync(
                        GetStatusTextForState(AppVpnState.Disconnected, VpnStatusMessages.Disconnected),
                        GetButtonTextForState(AppVpnState.Disconnected, VpnStatusMessages.StartAction),
                        AppVpnState.Disconnected);
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
                    UpdateIpStatusDisplay(OctopusEngine.Current.AssignedIp ?? VpnStatusMessages.Connected, textPrimary, accent);
                    UpdateActiveNode(_activeConnectedNode);
                    ConnectButtonCore.IsEnabled = true;
                    UpdateRayIndicator();
                    await SetNeonStateAsync(
                        GetStatusTextForState(AppVpnState.Connected, VpnStatusMessages.Protected),
                        GetButtonTextForState(AppVpnState.Connected, VpnStatusMessages.StopAction),
                        AppVpnState.Connected);
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
                    UpdateIpStatusDisplay(VpnStatusMessages.IpNotAssigned, muted, muted);
                    _activeConnectedNode = null;
                    UpdateActiveNode();
                    ConnectButtonCore.IsEnabled = true;
                    PlatformServices.Notification.ShowUnexpectedDisconnectNotification();
                    var errDisplay = !string.IsNullOrWhiteSpace(_lastErrorMessage)
                        ? _lastErrorMessage
                        : GetStatusTextForState(AppVpnState.Error, VpnStatusMessages.DefaultConnectionError);
                    await SetNeonStateAsync(
                        errDisplay,
                        GetButtonTextForState(AppVpnState.Error, VpnStatusMessages.Retry),
                        AppVpnState.Error);
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
                    UpdateIpStatusDisplay(VpnStatusMessages.IpAcquiring, accent, accent);
                    UpdateActiveNode();
                    await SetNeonStateAsync(
                        GetStatusTextForState(state, VpnStatusMessages.Connecting),
                        GetButtonTextForState(state, VpnStatusMessages.PleaseWait),
                        state);
                    break;

                case AppVpnState.Disconnecting:
                    StopGraphAnimation();
                    ResetPingIndicators();
                    ConnectButtonCore.IsEnabled = false;
                    UpdateIpStatusDisplay(VpnStatusMessages.Disconnecting, muted, muted);
                    NodeHostLabel.Text = VpnStatusMessages.Disconnecting;
                    StatusLabel.Text = VpnStatusMessages.Disconnecting;
                    ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Disconnecting, VpnStatusMessages.PleaseWait);
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
            var activeOv = ThemeManager.Instance?.ActiveTheme?.Vfx?.ActiveOverrides;
            var accentColor = GetAppColor("Accent", t_cyanAccent);
            if (!string.IsNullOrWhiteSpace(activeOv?.AccentColor) && Color.TryParse(activeOv.AccentColor, out var ovColor))
            {
                accentColor = ovColor;
                StatusLabel.TextColor = ovColor;
                ConnectIcon.IconColor = ovColor;
            }

            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.BackgroundColor = accentColor.WithAlpha(0.12f);
                ConnectButtonCore.Stroke = accentColor;
            }
            var targetScale = DeviceInfo.Idiom == DeviceIdiom.Phone ? 1.05 : 1.12;
            SafeScaleTo(LoaderCanvas, targetScale, 650, Easing.SpringOut);
        }
        else if (state == AppVpnState.Error)
        {
            await UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
            var errColor = GetAppColor("Error", t_redDot);
            var errDim = GetAppColor("ErrorDim", t_redBg);
            ConnectIcon.IconColor = errColor;
            StatusLabel.TextColor = errColor;
            if (!CustomButtonImage.IsVisible && !CustomButtonAnimatedCanvas.IsVisible)
            {
                ConnectButtonCore.BackgroundColor = errDim;
                ConnectButtonCore.Stroke = errColor;
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
        if (_isBusy || _vpnService is null)
        {
            return;
        }

        ThemeAudioService.PlayClick();
        if (ParentPage is not null)
        {
            float bx;
            float by;
            if (e is TappedEventArgs te && te.GetPosition(ParentPage) is { } pos)
            {
                bx = (float)pos.X;
                by = (float)pos.Y;
            }
            else
            {
                bx = (float)(ParentPage.Width * 0.5);
                by = (float)(ParentPage.Height * 0.5);
            }
            ParentPage.TriggerParticleBurst(bx, by);
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
                    "Обнаружен сертификат перехвата!",
                    $"На вашем устройстве установлен сторонний корневой сертификат:\n«{auditResult.CertificateName}»\n\n" +
                    "ВНИМАНИЕ: Вне VPN этот сертификат позволяет операторам связи и третьим лицам расшифровывать ваш защищённый трафик (HTTPS), видеть переписки и перехватывать пароли.\n\n" +
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
            var accent = GetAppColor("Accent", t_cyanAccent);
            UpdateIpStatusDisplay("Получение...", accent, accent);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Connecting, "Подключение..."),
                GetButtonTextForState(AppVpnState.Connecting, "ЖДИТЕ"),
                AppVpnState.Connecting);

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

            if ((!success || servers is null || servers.Count == 0) && _cachedServers.Count > 0)
            {
                servers = _cachedServers;
            }

            if (servers is null || servers.Count == 0)
            {
                throw new InvalidOperationException("Не удалось получить список серверов. Проверьте интернет и нажмите «СТАРТ» для повтора.");
            }

            _cachedServers = servers;

            VpnServerDto targetServer;
            if (_selectedServer is not null && servers.Any(s => s.Ip == _selectedServer.Ip))
            {
                targetServer = servers.First(s => s.Ip == _selectedServer.Ip);
            }
            else
            {
                targetServer = servers[0];
                _selectedServer = targetServer;
                UpdateSelectedServerDisplay();
            }

            var orderedCandidates = new List<VpnServerDto> { targetServer };
            orderedCandidates.AddRange(servers.Where(s => s.Ip != targetServer.Ip));

            UpdateActiveNode(AppSecrets.AllowedSniPool[0]);
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
            Shared.Logging.AppLogger.LogError($"[VPN CONNECT ERROR] {ex.Message}", ex);
            StopLoaderAnimation();
            ConnectButtonCore.IsEnabled = true;
            var muted = GetAppColor("TextMuted", t_grayText);
            UpdateIpStatusDisplay("Не назначен", muted, muted);
            await SetNeonStateAsync(
                GetStatusTextForState(AppVpnState.Disconnected, "Не в сети"),
                GetButtonTextForState(AppVpnState.Disconnected, "СТАРТ"),
                AppVpnState.Disconnected);
            var friendlyMsg = ex is SocketException or InvalidOperationException
                ? ex.Message
                : "Произошла ошибка при подключении/отключении";
            if (ParentPage is not null)
            {
                var logHint = !string.IsNullOrEmpty(Shared.Logging.AppLogger.LogFilePath)
                    ? $"\n\nПодробный лог сохранен в:\n{Shared.Logging.AppLogger.LogFilePath}"
                    : "";
                await ParentPage.DisplayAlertAsync("Ошибка подключения", $"{friendlyMsg}{logHint}", "OK");
            }
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

        var graphStyle = ThemeManager.Instance?.ActiveTheme?.Vfx?.TrafficGraphStyle?.Trim().ToLowerInvariant() ?? "neon_wave";

        void PopulatePoints(bool isUpStream)
        {
            _graphPointsBuffer.Clear();
            for (var i = 0; i < samplesCopy.Count; i++)
            {
                var s = samplesCopy[i];
                var age = now - s.Time;
                var x = w - (age / TimeWindowSeconds * w);
                var speed = isUpStream ? s.SpeedUp : s.SpeedDown;
                var ratio = Math.Clamp((float)(speed / maxSpeed), 0f, 1f);
                var y = h - paddingY - (ratio * usableH);
                _graphPointsBuffer.Add(new SKPoint(x, y));
            }

            if (_graphPointsBuffer.Count == 0)
            {
                return;
            }

            if (_graphPointsBuffer[0].X > 0)
            {
                _graphPointsBuffer.Insert(0, new SKPoint(0, _graphPointsBuffer[0].Y));
            }

            if (_graphPointsBuffer[^1].X < w)
            {
                _graphPointsBuffer.Add(new SKPoint(w, _graphPointsBuffer[^1].Y));
            }
        }

        void DrawStream(bool isUpStream, SKPaint strokePaint, SKColor startFillColor, SKColor strokeColor)
        {
            PopulatePoints(isUpStream);
            if (_graphPointsBuffer.Count < 2)
            {
                return;
            }

            if (graphStyle == "cyber_bars")
            {
                var barCount = 28;
                var slotW = w / (float)barCount;
                var barW = Math.Max(2f, slotW * 0.56f);
                for (var b = 0; b < barCount; b++)
                {
                    var sampleIdx = (int)Math.Clamp((float)b / barCount * _graphPointsBuffer.Count, 0, _graphPointsBuffer.Count - 1);
                    var ptY = _graphPointsBuffer[sampleIdx].Y;
                    var barX = (b * slotW) + ((slotW - barW) * 0.5f) + (isUpStream ? -1.5f : 1.5f);
                    var barTop = Math.Min(h - paddingY - 2f, ptY);
                    var rect = new SKRect(barX, barTop, barX + barW, h - paddingY);
                    _graphDotSolidPaint.Color = strokeColor.WithAlpha((byte)(isUpStream ? 110 : 165));
                    canvas.DrawRoundRect(rect, 2f, 2f, _graphDotSolidPaint);

                    _graphDotGlowPaint.Color = strokeColor.WithAlpha(235);
                    canvas.DrawRect(barX, barTop, barW, 2.5f, _graphDotGlowPaint);
                }
                return;
            }

            using var strokeBuilder = new SKPathBuilder();
            strokeBuilder.MoveTo(_graphPointsBuffer[0]);
            if (graphStyle == "pulse_line")
            {
                for (var i = 1; i < _graphPointsBuffer.Count; i++)
                {
                    strokeBuilder.LineTo(_graphPointsBuffer[i]);
                }
            }
            else
            {
                for (var i = 0; i < _graphPointsBuffer.Count - 1; i++)
                {
                    var p0 = _graphPointsBuffer[i];
                    var p1 = _graphPointsBuffer[i + 1];
                    var midX = (p0.X + p1.X) / 2f;
                    strokeBuilder.CubicTo(midX, p0.Y, midX, p1.Y, p1.X, p1.Y);
                }
            }
            using var strokePath = strokeBuilder.Detach();

            using var fillBuilder = new SKPathBuilder(strokePath);
            fillBuilder.LineTo(_graphPointsBuffer[^1].X, h);
            fillBuilder.LineTo(_graphPointsBuffer[0].X, h);
            fillBuilder.Close();
            using var fillPath = fillBuilder.Detach();

            SKShader fillShader;
            var effectiveFillColor = graphStyle == "aurora_ribbon"
                ? strokeColor.WithAlpha(115)
                : startFillColor;

            if (isUpStream)
            {
                if (_cachedGraphUpFillShader == null || _lastGraphH != h || _lastGraphUpColor != effectiveFillColor)
                {
                    _cachedGraphUpFillShader?.Dispose();
                    _cachedGraphUpFillShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(0, h),
                        [effectiveFillColor, SKColors.Transparent],
                        [0f, 1f],
                        SKShaderTileMode.Clamp);
                    _lastGraphUpColor = effectiveFillColor;
                }
                fillShader = _cachedGraphUpFillShader;
            }
            else
            {
                if (_cachedGraphDownFillShader == null || _lastGraphH != h || _lastGraphDownColor != effectiveFillColor)
                {
                    _cachedGraphDownFillShader?.Dispose();
                    _cachedGraphDownFillShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, 0),
                        new SKPoint(0, h),
                        [effectiveFillColor, SKColors.Transparent],
                        [0f, 1f],
                        SKShaderTileMode.Clamp);
                    _lastGraphDownColor = effectiveFillColor;
                }
                fillShader = _cachedGraphDownFillShader;
            }
            _lastGraphH = h;

            if (graphStyle != "pulse_line")
            {
                _graphFillPaint.Shader = fillShader;
                canvas.DrawPath(fillPath, _graphFillPaint);
                _graphFillPaint.Shader = null;
            }

            if (graphStyle == "aurora_ribbon")
            {
                var origWidth = strokePaint.StrokeWidth;
                strokePaint.StrokeWidth = 5.5f;
                strokePaint.Color = strokeColor.WithAlpha(90);
                canvas.DrawPath(strokePath, strokePaint);
                strokePaint.StrokeWidth = origWidth;
                strokePaint.Color = strokeColor;
            }

            canvas.DrawPath(strokePath, strokePaint);

            var lastPt = _graphPointsBuffer[^1];
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
                if (CustomButtonRingImage is { IsVisible: true })
                {
                    CustomButtonRingImage.Rotation = _loaderAngle * 0.45f % 360f;
                }
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

        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var loaderStyle = vfx?.ButtonLoaderStyle?.Trim().ToLowerInvariant() ?? string.Empty;
        if (loaderStyle == "none")
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
        var skError = GetSkiaThemeColor("Error", new SKColor(0xEF, 0x44, 0x44));
        if (_vpnService?.CurrentState == AppVpnState.Connected &&
            !string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.AccentColor) &&
            SKColor.TryParse(vfx.ActiveOverrides.AccentColor, out var ovAccent))
        {
            skAccent = ovAccent;
        }

        var colorPrimary = LerpColor(skPrimary, skError, _errorTransitionFactor);
        var colorAccent = LerpColor(skAccent, skError, _errorTransitionFactor);
        var glowMult = (float)ThemeManager.CurrentGlowIntensity;
        if (vfx?.Glow?.Intensity is > 0)
        {
            glowMult *= (float)vfx.Glow.Intensity;
        }

        if (loaderStyle == "dual_arc")
        {
            DrawDualArcLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

        if (loaderStyle == "runes")
        {
            DrawRunesLoader(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

        if (loaderStyle == "reactor")
        {
            DrawLivingReactor(canvas, cx, cy, r, colorPrimary, colorAccent, glowMult, 1f);
            return;
        }

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

    private void DrawDualArcLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        var outerR = r * 0.84f;
        var innerR = r * 0.66f;
        var outerRect = new SKRect(cx - outerR, cy - outerR, cx + outerR, cy + outerR);
        var innerRect = new SKRect(cx - innerR, cy - innerR, cx + innerR, cy + innerR);

        var glowAlpha = (byte)Math.Clamp(110 * glowMult * alphaMultiplier, 0, 255);
        var coreAlpha = (byte)Math.Clamp(220 * alphaMultiplier, 0, 255);

        _polyGlowPaint.Color = colorAccent.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorAccent.WithAlpha(coreAlpha);
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f, 115f, false, _polyGlowPaint);
        canvas.DrawArc(outerRect, _loaderAngle * 1.4f, 115f, false, _polyCorePaint);
        canvas.DrawArc(outerRect, (_loaderAngle * 1.4f) + 180f, 115f, false, _polyGlowPaint);
        canvas.DrawArc(outerRect, (_loaderAngle * 1.4f) + 180f, 115f, false, _polyCorePaint);

        _polyGlowPaint.Color = colorPrimary.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, -_loaderAngle * 1.9f, 85f, false, _polyCorePaint);
        canvas.DrawArc(innerRect, (-_loaderAngle * 1.9f) + 180f, 85f, false, _polyGlowPaint);
        canvas.DrawArc(innerRect, (-_loaderAngle * 1.9f) + 180f, 85f, false, _polyCorePaint);
    }

    private void DrawRunesLoader(SKCanvas canvas, float cx, float cy, float r, SKColor colorPrimary, SKColor colorAccent, float glowMult, float alphaMultiplier)
    {
        var ringR = r * 0.82f;
        var glowAlpha = (byte)Math.Clamp(95 * glowMult * alphaMultiplier, 0, 255);
        var coreAlpha = (byte)Math.Clamp(195 * alphaMultiplier, 0, 255);

        _reactorTrackPaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawCircle(cx, cy, ringR, _reactorTrackPaint);
        canvas.DrawCircle(cx, cy, ringR * 0.86f, _reactorTrackPaint);

        _ = canvas.Save();
        canvas.RotateDegrees(_loaderAngle * 0.65f, cx, cy);
        using var hexPath = MakePolygon(cx, cy, ringR * 0.86f, 6);
        _polyGlowPaint.Color = colorAccent.WithAlpha(glowAlpha);
        _polyCorePaint.Color = colorAccent.WithAlpha(coreAlpha);
        canvas.DrawPath(hexPath, _polyGlowPaint);
        canvas.DrawPath(hexPath, _polyCorePaint);
        canvas.Restore();

        _ = canvas.Save();
        canvas.RotateDegrees(-_loaderAngle * 0.95f, cx, cy);
        using var triPath1 = MakePolygon(cx, cy, ringR * 0.68f, 3);
        _polyCorePaint.Color = colorPrimary.WithAlpha(coreAlpha);
        canvas.DrawPath(triPath1, _polyCorePaint);
        canvas.RotateDegrees(60f, cx, cy);
        canvas.DrawPath(triPath1, _polyCorePaint);
        canvas.Restore();
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

        var vfx = ThemeManager.Instance?.ActiveTheme?.Vfx;
        var isConnected = _vpnService?.CurrentState == AppVpnState.Connected;

        var skAccent = GetSkiaThemeColor("Accent", t_skCyan);
        var skPrimary = GetSkiaThemeColor("Primary", t_skPurple);
        var skError = GetSkiaThemeColor("Error", new SKColor(0xEF, 0x44, 0x44));

        var coreGlowColor = skAccent;
        if (!string.IsNullOrWhiteSpace(vfx?.Glow?.CoreColor) && SKColor.TryParse(vfx.Glow.CoreColor, out var parsedCore))
        {
            coreGlowColor = parsedCore;
        }
        var bloomGlowColor = !string.IsNullOrWhiteSpace(vfx?.Glow?.BloomColor) && SKColor.TryParse(vfx.Glow.BloomColor, out var parsedBloom)
            ? parsedBloom
            : coreGlowColor;

        if (isConnected)
        {
            if (!string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.AccentColor) &&
                SKColor.TryParse(vfx.ActiveOverrides.AccentColor, out var ovAccent))
            {
                coreGlowColor = ovAccent;
            }
            if (!string.IsNullOrWhiteSpace(vfx?.ActiveOverrides?.GlowColor) &&
                SKColor.TryParse(vfx.ActiveOverrides.GlowColor, out var ovGlow))
            {
                bloomGlowColor = ovGlow;
            }
        }

        var targetColor = _isErrorState ? skError : coreGlowColor;
        var color = LerpColor(coreGlowColor, targetColor, Math.Max(_isErrorState ? 1f : 0f, _errorTransitionFactor));
        var bloomColor = LerpColor(bloomGlowColor, targetColor, Math.Max(_isErrorState ? 1f : 0f, _errorTransitionFactor));

        var glowMult = (float)ThemeManager.CurrentGlowIntensity;
        if (vfx?.Glow?.Intensity is > 0)
        {
            glowMult *= (float)vfx.Glow.Intensity;
        }

        var baseAlpha = (byte)(isConnected ? 95 : 65);
        var midAlpha = (byte)(isConnected ? 35 : 20);

        if (_cachedAuraShader == null ||
            Math.Abs(_lastAuraCx - cx) > 0.5f ||
            Math.Abs(_lastAuraCy - cy) > 0.5f ||
            Math.Abs(_lastAuraR - r) > 0.5f ||
            Math.Abs(_lastAuraGlow - glowMult) > 0.01f ||
            _lastAuraColor != color ||
            _lastAuraBloomColor != bloomColor ||
            _lastAuraBaseAlpha != baseAlpha ||
            _lastAuraMidAlpha != midAlpha)
        {
            _cachedAuraShader?.Dispose();
            _lastAuraCx = cx;
            _lastAuraCy = cy;
            _lastAuraR = r;
            _lastAuraGlow = glowMult;
            _lastAuraColor = color;
            _lastAuraBloomColor = bloomColor;
            _lastAuraBaseAlpha = baseAlpha;
            _lastAuraMidAlpha = midAlpha;

            _cachedAuraShader = SKShader.CreateRadialGradient(
                new SKPoint(cx, cy),
                r,
                [
                    color.WithAlpha((byte)Math.Clamp(baseAlpha * glowMult, 0, 255)),
                    bloomColor.WithAlpha((byte)Math.Clamp(midAlpha * glowMult, 0, 255)),
                    SKColors.Transparent
                ],
                [0f, 0.65f, 1f],
                SKShaderTileMode.Clamp);
        }

        _auraPaint.Shader = _cachedAuraShader;
        canvas.DrawCircle(cx, cy, r, _auraPaint);
        _auraPaint.Shader = null;

        if (_shockwaveProgress >= 0f)
        {
            var p = _shockwaveProgress;
            var shockColor = _shockwaveIsConnect ? color : skPrimary;
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
        private const int MaxButtonDimension = 280;
        private readonly List<SKBitmap> _frames = [];
        private readonly List<int> _durations = [];
        private int _currentFrameIndex;
        private int _elapsedMs;

        public int FrameCount => _frames.Count;
        public SKBitmap? CurrentBitmap => _frames.Count > 0 ? _frames[_currentFrameIndex] : null;

        private static SKBitmap DownscaleIfNeeded(SKBitmap bmp)
        {
            if (bmp.Width <= MaxButtonDimension && bmp.Height <= MaxButtonDimension)
            {
                return bmp;
            }

            var scale = Math.Min((float)MaxButtonDimension / bmp.Width, (float)MaxButtonDimension / bmp.Height);
            var targetW = Math.Max(1, (int)Math.Round(bmp.Width * scale));
            var targetH = Math.Max(1, (int)Math.Round(bmp.Height * scale));
            var resizedInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
            var resized = bmp.Resize(resizedInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            if (resized != null)
            {
                bmp.Dispose();
                return resized;
            }
            return bmp;
        }

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
                    staticClip._frames.Add(DownscaleIfNeeded(staticBmp));
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

                    clip._frames.Add(DownscaleIfNeeded(bmp));
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

        var info = e.Info;
        var dst = new SKRect(0, 0, info.Width, info.Height);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        var prev = _buttonAnimatedPrev;
        if (prev?.CurrentBitmap is { } prevBmp && _buttonTransitionProgress < 1f)
        {
            var prevAlpha = (byte)Math.Clamp((1f - _buttonTransitionProgress) * 255f, 0f, 255f);
            _customButtonBitmapPaint.Color = SKColors.White.WithAlpha(prevAlpha);
            canvas.DrawBitmap(prevBmp, new SKRect(0, 0, prevBmp.Width, prevBmp.Height), dst, sampling, _customButtonBitmapPaint);

            var currAlpha = (byte)Math.Clamp(_buttonTransitionProgress * 255f, 0f, 255f);
            _customButtonBitmapPaint.Color = SKColors.White.WithAlpha(currAlpha);
            canvas.DrawBitmap(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), dst, sampling, _customButtonBitmapPaint);
            return;
        }

        _customButtonBitmapPaint.Color = SKColors.White;
        canvas.DrawBitmap(bmp, new SKRect(0, 0, bmp.Width, bmp.Height), dst, sampling, _customButtonBitmapPaint);
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

        _buttonAnimStopwatch.Restart();
        _lastButtonAnimTicks = 0;
        _buttonAnimationTimer = Dispatcher.CreateTimer();
        _buttonAnimationTimer.Interval = TimeSpan.FromMilliseconds(16);
#pragma warning disable IDE0058
        _buttonAnimationTimer.Tick += (_, _) =>
        {
            var nowTicks = _buttonAnimStopwatch.ElapsedTicks;
            var dtMs = (int)Math.Clamp((nowTicks - _lastButtonAnimTicks) * 1000.0 / Stopwatch.Frequency, 4.0, 100.0);
            _lastButtonAnimTicks = nowTicks;

            _buttonAnimatedCurrent?.Advance(dtMs);
            if (_buttonAnimatedPrev is not null)
            {
                _buttonAnimatedPrev.Advance(dtMs);
                _buttonTransitionProgress = Math.Min(1f, _buttonTransitionProgress + (dtMs / 300f));
                if (_buttonTransitionProgress >= 1f)
                {
                    _buttonAnimatedPrev = null;
                }
            }
            CustomButtonAnimatedCanvas.InvalidateSurface();
        };
#pragma warning restore IDE0058
        _buttonAnimationTimer.Start();
    }

    private void StopButtonAnimation()
    {
        _buttonAnimationTimer?.Stop();
        _buttonAnimationTimer = null;
        _buttonAnimStopwatch.Stop();
    }

    private void DisposeAnimatedButtonResources()
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;
        StopButtonAnimation();
        _buttonAnimatedCurrent?.Dispose();
        _buttonAnimatedCurrent = null;
        _buttonAnimatedPrev?.Dispose();
        _buttonAnimatedPrev = null;
        _buttonTransitionProgress = 1f;
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
            AppVpnState.Connected => _buttonAnimatedActive ?? _buttonAnimatedIdle,
            AppVpnState.Connecting or AppVpnState.Reconnecting or AppVpnState.Disconnecting => _buttonAnimatedConnecting ?? _buttonAnimatedIdle,
            AppVpnState.Error => _buttonAnimatedError ?? _buttonAnimatedIdle,
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
        string? videoErrorPath = null,
        string? cardFramePath = null,
        string? buttonRingPath = null)
    {
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;

        _buttonImageIdlePath = imageIdlePath;
        _buttonImageConnectingPath = imageConnectingPath;
        _buttonImageActivePath = imageActivePath;
        _buttonImageErrorPath = imageErrorPath;

        var hasStaticCustom = !string.IsNullOrEmpty(imageIdlePath) ||
                              !string.IsNullOrEmpty(imageConnectingPath) ||
                              !string.IsNullOrEmpty(imageActivePath) ||
                              !string.IsNullOrEmpty(imageErrorPath);

        var idleCandidate = videoIdlePath ?? (IsPotentialAnimatedFile(imageIdlePath) ? imageIdlePath : null);
        var connCandidate = videoConnectingPath ?? (IsPotentialAnimatedFile(imageConnectingPath) ? imageConnectingPath : null);
        var activeCandidate = videoActivePath ?? (IsPotentialAnimatedFile(imageActivePath) ? imageActivePath : null);
        var errorCandidate = videoErrorPath ?? (IsPotentialAnimatedFile(imageErrorPath) ? imageErrorPath : null);

        var hasAnyAnim = !string.IsNullOrEmpty(idleCandidate) ||
                         !string.IsNullOrEmpty(connCandidate) ||
                         !string.IsNullOrEmpty(activeCandidate) ||
                         !string.IsNullOrEmpty(errorCandidate);

        _activeCardFramePath = cardFramePath;
        var framesEnabled = ThemeManager.Instance?.FramesEnabled ?? true;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!string.IsNullOrEmpty(cardFramePath) && File.Exists(cardFramePath))
            {
                CardFrameImage.Source = ImageSource.FromFile(cardFramePath);
                CardFrameImage.IsVisible = framesEnabled;
            }
            else
            {
                CardFrameImage.IsVisible = false;
                CardFrameImage.Source = null;
            }

            if (!string.IsNullOrEmpty(buttonRingPath) && File.Exists(buttonRingPath))
            {
                CustomButtonRingImage.Source = ImageSource.FromFile(buttonRingPath);
                CustomButtonRingImage.IsVisible = true;
            }
            else
            {
                CustomButtonRingImage.IsVisible = false;
                CustomButtonRingImage.Source = null;
            }

            if (hasStaticCustom && !hasAnyAnim)
            {
                DefaultButtonContent.IsVisible = false;
                CustomButtonAnimatedCanvas.IsVisible = false;
                ConnectButtonCore.BackgroundColor = Colors.Transparent;
                ConnectButtonCore.Stroke = Colors.Transparent;

                var currentState = _vpnService?.CurrentState ?? AppVpnState.Disconnected;
                var initialPath = GetButtonImagePathForState(currentState);
                if (!string.IsNullOrEmpty(initialPath) && File.Exists(initialPath))
                {
                    CustomButtonImage.Source = ImageSource.FromFile(initialPath);
                    CustomButtonImage.IsVisible = true;
                    CustomButtonImage.Opacity = 1;
                }
            }
            else if (!hasAnyAnim)
            {
                ResetCustomButtonStateOnly();
            }
        });

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
                    _buttonAnimatedCurrent = null;
                    _buttonAnimatedPrev = null;
                    _buttonTransitionProgress = 1f;
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
        _buttonAnimCts?.Cancel();
        _buttonAnimCts?.Dispose();
        _buttonAnimCts = null;

        _buttonImageIdlePath = null;
        _buttonImageConnectingPath = null;
        _buttonImageActivePath = null;
        _buttonImageErrorPath = null;
        _activeCardFramePath = null;
        DisposeAnimatedButtonResources();

        MainThread.BeginInvokeOnMainThread(() =>
        {
            CardFrameImage.IsVisible = false;
            CardFrameImage.Source = null;
            CustomButtonRingImage.IsVisible = false;
            CustomButtonRingImage.Source = null;
            ResetCustomButtonStateOnly();
            OnThemeChanged();
        });
    }

    public void SetCardFrameVisibility(bool visible)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            var hasFrame = !string.IsNullOrEmpty(_activeCardFramePath) && File.Exists(_activeCardFramePath);
            CardFrameImage.IsVisible = visible && hasFrame;
        });
    }

    private void ResetCustomButtonStateOnly()
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
            if (_buttonAnimatedCurrent != null && _buttonAnimatedCurrent != targetClip)
            {
                _buttonAnimatedPrev = _buttonAnimatedCurrent;
                _buttonTransitionProgress = 0f;
            }
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
        _buttonAnimatedPrev = null;
        _buttonTransitionProgress = 1f;
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
            PopulateServerList();

            var borderSubtle = GetAppColor("BorderSubtle", Color.FromArgb("#282838"));
            var borderMedium = GetAppColor("BorderMedium", Color.FromArgb("#363650"));

            CardIp.Stroke = borderSubtle;
            Card2.Stroke = borderSubtle;
            Card5.Stroke = borderSubtle;
            Card6.Stroke = borderSubtle;
            Card1Wrapper.Stroke = borderSubtle;
            ServerSelectorButton.Stroke = borderMedium;

            var currentState = _vpnService?.CurrentState ?? AppVpnState.Disconnected;
            if (currentState == AppVpnState.Disconnected)
            {
                StatusLabel.Text = GetStatusTextForState(AppVpnState.Disconnected, VpnStatusMessages.Disconnected);
                ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Disconnected, VpnStatusMessages.StartAction);
            }
            else if (currentState == AppVpnState.Connected)
            {
                StatusLabel.Text = GetStatusTextForState(AppVpnState.Connected, VpnStatusMessages.Protected);
                ConnectButtonText.Text = GetButtonTextForState(AppVpnState.Connected, VpnStatusMessages.StopAction);
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

            if (currentState == AppVpnState.Disconnected)
            {
                _ = UIAnimations.SetVpnDisconnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                ConnectButtonCore.ClearValue(BackgroundColorProperty);
                ConnectButtonCore.ClearValue(Border.StrokeProperty);
                ConnectButtonCore.SetDynamicResource(BackgroundColorProperty, "BgElevated");
                ConnectButtonCore.SetDynamicResource(Border.StrokeProperty, "BorderMedium");
            }
            else if (currentState == AppVpnState.Connected)
            {
                _ = UIAnimations.SetVpnConnectedAsync(ConnectIcon, StatusLabel, OuterAura);
                var accentColor = GetAppColor("Accent", t_cyanAccent);
                var activeOv = ThemeManager.Instance?.ActiveTheme?.Vfx?.ActiveOverrides;
                if (!string.IsNullOrWhiteSpace(activeOv?.AccentColor) && Color.TryParse(activeOv.AccentColor, out var ovColor))
                {
                    accentColor = ovColor;
                    StatusLabel.TextColor = ovColor;
                    ConnectIcon.IconColor = ovColor;
                }
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
