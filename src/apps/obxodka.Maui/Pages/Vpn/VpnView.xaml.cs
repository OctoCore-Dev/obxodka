//using только для сплит тунеля хватит удалять и натыкатся потостоянно на одно и тоже 5ый раз, рома ты тупой, ии не поможет ты сам это сделал
namespace obxodka.Pages;

public sealed partial class VpnView : ContentView, IDisposable
{
    private static readonly Color s_greenDot = Color.FromArgb("#10B981");
    private static readonly Color s_greenText = Color.FromArgb("#34D399");
    private static readonly Color s_amberDot = Color.FromArgb("#F59E0B");
    private static readonly Color s_redDot = Color.FromArgb("#EF4444");
    private static readonly Color s_grayStroke = Color.FromArgb("#4B5563");
    private static readonly Color s_grayText = Color.FromArgb("#9CA3AF");

    private static readonly Color s_cyanAccent = Color.FromArgb("#00E5FF");
    private static readonly Color s_redBg = Color.FromArgb("#1AFF0000");
    private static readonly string[] s_errorSeparators = ["\r\n", "\n", "Status(", "Detail="];

    private static readonly SKColor s_skPurple = SKColor.Parse("#7C3AED");
    private static readonly SKColor s_skCyan = SKColor.Parse("#00E5FF");
    private static readonly SKColor s_skGraphUp = SKColor.Parse("#9F6FF0");
    private static readonly SKColor s_skGraphDown = SKColor.Parse("#00E5FF");
    private static readonly SKColor s_skGridDash = new(255, 255, 255, 14);

    private static readonly SKPaint s_skGridPaint = new()
    {
        Color = s_skGridDash,
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

    private static readonly (float speed, float delay, float ox, float oy, float size, int sides)[] s_polyConfigs =
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

    //принудительный пересчет ширины карточки при изменении размера
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

    //установка адаптивной ширины карточки
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

    //инициализация сервисов и подписка на события туннеля
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

    //отписка от сетевых событий и остановка фоновых таймеров
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

    //освобождение битмапов покадровой анимации
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

    //анимация плавного появления карточек при входе
    public async Task PlayEntranceAnimationAsync()
    {
        UpdateRayIndicator();
        Opacity = 1;
        TranslationY = 0;
        await this.PlayCardsEntranceAsync(35, 240);
    }

    //обновление отображения баланса времени и трафика
    public void UpdateBalanceUI(string timeText, string? trafficText = null)
    {
        TokenAmountLabel.Text = timeText;
        if (trafficText is not null)
        {
            TotalTrafficLabelMain.Text = trafficText;
        }
    }

    //обновление индикатора сетевого луча
    private static void UpdateRayIndicator()
    {
    }
}
