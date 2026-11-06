namespace obxodka.Maui.Theming.Controls;

//наложение визуальных эффектов темы оформления частицы шейдеры и интерактивный отклик на трафик
[SuppressMessage("Security", "CA5394:Do not use insecure randomness", Justification = "visual particle physics and rendering")]
public sealed partial class ThemeParticlesOverlay : SKCanvasView, IDisposable
{
    private readonly List<Particle> _particles = [];
    private readonly List<BurstParticle> _burstParticles = [];
    private readonly List<SKBitmap> _spriteBitmaps = [];
    private readonly Random _random = new(1337);
    private readonly Stopwatch _stopwatch = new();
    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true };
    private readonly SKPaint _fallbackPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _shaderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke };
    private readonly SKPaint _glowPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill, BlendMode = SKBlendMode.Plus };
    private readonly SKPath _starPath = CreateStarPath();
    private readonly SKPath _petalPath = CreatePetalPath();
    private readonly SKPath _diamondPath = CreateDiamondPath();
    private readonly SKPathBuilder _wavePathBuilder = new();
    private static readonly SKSamplingOptions s_samplingOptions = new(SKFilterMode.Linear);

    private IDispatcherTimer? _timer;
    private MovementType _movementType = MovementType.Falling;
    private string _particleTypeName = "none";
    private string _screenOverlay = "none";
    private string? _activeOverrideOverlay;
    private SKColor _particleColor = new(255, 255, 255, 180);
    private SKColor _primarySkColor = new(0, 120, 212, 220);
    private SKColor _accentSkColor = new(0, 229, 255, 220);
    private bool _hasCustomTint;
    private SKColor _customTintColor = SKColors.White;
    private float _speedMultiplier = 1.0f;
    private float _sizeMultiplier = 1.0f;
    private float _activeSpeedOverride = 1.0f;
    private bool _interactiveParticles = true;
    private bool _trafficReactive = true;
    private float _targetTrafficMultiplier = 1.0f;
    private float _currentTrafficMultiplier = 1.0f;
    private float _pointerX;
    private float _pointerY;
    private bool _pointerActive;
    private long _lastPointerMoveTicks;
    private AppVpnState _vpnState = AppVpnState.Disconnected;
    private bool _shockwaveActive;
    private float _shockwaveProgress;
    private float _shaderTime;
    private double _lastFrameSeconds;
    private float _lastWidth;
    private float _lastHeight;
    private bool _initialized;
    private float? _customMinSize;
    private float? _customMaxSize;
    private float? _customSpeedX;
    private float? _customSpeedY;
    private float? _customRotationSpeed;
    private float? _customSwaySpeed;
    private float? _customTwinkleSpeed;
    private bool _connectLines;
    private SKBitmap? _screenOverlayBitmap;
    private float _screenOverlayOpacity = 0.65f;

    public ThemeParticlesOverlay()
    {
        InputTransparent = true;
        IgnorePixelScaling = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    //обработка изменения размера канваса с пересчетом позиций частиц
    private void OnSizeChanged(object? sender, EventArgs e)
    {
        if (Width <= 1 || Height <= 1 || _particles.Count == 0)
        {
            return;
        }

        var newW = (float)Width;
        var newH = (float)Height;
        if (Math.Abs(newW - _lastWidth) < 4f && Math.Abs(newH - _lastHeight) < 4f)
        {
            return;
        }

        if (_lastWidth > 1f && _lastHeight > 1f)
        {
            var ratioX = newW / _lastWidth;
            var ratioY = newH / _lastHeight;
            foreach (var p in _particles)
            {
                p.X = Math.Clamp(p.X * ratioX, -20f, newW + 20f);
                p.Y = Math.Clamp(p.Y * ratioY, -20f, newH + 20f);
            }
        }
        else
        {
            foreach (var p in _particles)
            {
                p.X = (float)(_random.NextDouble() * newW);
                p.Y = (float)(_random.NextDouble() * newH);
            }
        }

        _lastWidth = newW;
        _lastHeight = newH;
        InvalidateSurface();
    }

#if WINDOWS
    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        if (Handler?.PlatformView is Microsoft.UI.Xaml.UIElement element)
        {
            element.IsHitTestVisible = false;
        }
    }
#endif

    private void OnLoaded(object? sender, EventArgs e)
    {
        if (IsVisible)
        {
            StartAnimation();
        }
    }

    private void OnUnloaded(object? sender, EventArgs e) => StopAnimation();

    //конфигурация эффектов темы с одиночным спрайтом
    public void Configure(string? spritePath, ThemeVfx? vfx, Color? primaryColor, Color? accentColor)
    {
        IEnumerable<string>? paths = !string.IsNullOrWhiteSpace(spritePath) ? [spritePath] : null;
        Configure(paths, vfx, primaryColor, accentColor, particlesEnabled: true);
    }

    //конфигурация эффектов темы с набором спрайтов и палитрой
    public void Configure(
        IEnumerable<string>? spritePaths,
        ThemeVfx? vfx,
        Color? primaryColor,
        Color? accentColor,
        bool particlesEnabled = true,
        string? screenOverlayImagePath = null)
    {
        ClearSprites();
        ClearOverlayBitmap();

        _primarySkColor = primaryColor is not null
            ? new SKColor((byte)(primaryColor.Red * 255), (byte)(primaryColor.Green * 255), (byte)(primaryColor.Blue * 255), 220)
            : new SKColor(0, 120, 212, 220);

        _accentSkColor = accentColor is not null
            ? new SKColor((byte)(accentColor.Red * 255), (byte)(accentColor.Green * 255), (byte)(accentColor.Blue * 255), 220)
            : _primarySkColor;

        if (vfx is null)
        {
            _particleTypeName = "none";
            _screenOverlay = "none";
            _activeOverrideOverlay = null;
            _particles.Clear();
            _initialized = false;
            StopAnimation();
            IsVisible = false;
            InvalidateSurface();
            return;
        }

        _interactiveParticles = vfx.InteractiveParticles;
        _trafficReactive = vfx.TrafficReactive;
        _screenOverlay = string.IsNullOrWhiteSpace(vfx.ScreenOverlay) ? "none" : vfx.ScreenOverlay.Trim().ToLowerInvariant();
        _activeOverrideOverlay = vfx.ActiveOverrides?.ScreenOverlay?.Trim().ToLowerInvariant();
        _activeSpeedOverride = (float)Math.Clamp(vfx.ActiveOverrides?.ParticleSpeedMultiplier ?? 1.5, 0.5, 4.0);

        _hasCustomTint = !string.IsNullOrWhiteSpace(vfx.Color) && SKColor.TryParse(vfx.Color, out _customTintColor);
        _particleColor = _hasCustomTint
            ? _customTintColor
            : accentColor is not null
                ? _accentSkColor
                : _primarySkColor;

        _fallbackPaint.Color = _particleColor;

        var rawParticles = particlesEnabled && !string.IsNullOrWhiteSpace(vfx.Particles)
            ? vfx.Particles.Trim().ToLowerInvariant()
            : "none";
        _particleTypeName = rawParticles;

        if (!string.Equals(_particleTypeName, "none", StringComparison.OrdinalIgnoreCase))
        {
            if (spritePaths != null)
            {
                var loadedBmps = new List<SKBitmap>();
                foreach (var path in spritePaths)
                {
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    {
                        continue;
                    }

                    try
                    {
#pragma warning disable CA1508 // skia interop может вернуть null несмотря на отсутствие аннотаций
                        using var codec = SKCodec.Create(path);
                        if (codec is { Info: { Width: > 0 and <= SafeThemeExtractor.MaxImageDimension, Height: > 0 and <= SafeThemeExtractor.MaxImageDimension } })
                        {
                            var bmp = SKBitmap.Decode(codec);
                            if (bmp is not null)
                            {
                                loadedBmps.Add(bmp);
                            }
                        }
#pragma warning restore CA1508
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ThemeParticles] Error decoding sprite '{path}': {ex.Message}");
                    }
                }

                lock (_spriteBitmaps)
                {
                    _spriteBitmaps.AddRange(loadedBmps);
                }
            }

            if (!string.IsNullOrWhiteSpace(screenOverlayImagePath) && File.Exists(screenOverlayImagePath))
            {
                try
                {
#pragma warning disable CA1508 // skia interop может вернуть null несмотря на отсутствие аннотаций
                    using var codec = SKCodec.Create(screenOverlayImagePath);
                    if (codec is { Info: { Width: > 0 and <= SafeThemeExtractor.MaxImageDimension, Height: > 0 and <= SafeThemeExtractor.MaxImageDimension } })
                    {
                        _screenOverlayBitmap = SKBitmap.Decode(codec);
                    }
#pragma warning restore CA1508
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ThemeParticles] Ошибка декодирования оверлея '{screenOverlayImagePath}': {ex.Message}");
                }
            }

            var explicitDirection = vfx.Physics?.Direction ?? vfx.Direction;
            _movementType = !string.IsNullOrWhiteSpace(explicitDirection)
                ? explicitDirection.Trim().ToLowerInvariant() switch
                {
                    "rising" or "up" or "ascend" => MovementType.Rising,
                    "floating" or "float" or "drift" or "cosmic" => MovementType.Floating,
                    _ => MovementType.Falling
                }
                : _particleTypeName switch
                {
                    "stars" or "twinkle" or "floating" or "dust" or "cosmic" => MovementType.Floating,
                    "rising" or "embers" or "sparks" or "neon_sparks" or "bubbles" or "fireflies" => MovementType.Rising,
                    _ => MovementType.Falling
                };

            _screenOverlayOpacity = (float)(vfx.Physics?.OverlayOpacity ?? vfx.OverlayOpacity ?? 0.65);

            _customMinSize = (float?)(vfx.Physics?.MinSize ?? vfx.MinSize);
            _customMaxSize = (float?)(vfx.Physics?.MaxSize ?? vfx.MaxSize);
            _customSpeedX = (float?)(vfx.Physics?.SpeedX);
            _customSpeedY = (float?)(vfx.Physics?.SpeedY);
            _customRotationSpeed = (float?)(vfx.Physics?.RotationSpeed ?? vfx.RotationSpeed);
            _customSwaySpeed = (float?)(vfx.Physics?.SwaySpeed ?? vfx.SwaySpeed);
            _customTwinkleSpeed = (float?)(vfx.Physics?.TwinkleSpeed ?? vfx.TwinkleSpeed);
            _connectLines = vfx.Physics?.ConnectLines ?? vfx.ConnectLines ?? false;

            _speedMultiplier = (float)Math.Clamp(vfx.Speed > 0 ? vfx.Speed : 1.0, 0.2, 5.0);
            _sizeMultiplier = (float)Math.Clamp(vfx.Size > 0 ? vfx.Size : 1.0, 0.2, 5.0);

            var count = (int)(18 + (Math.Clamp(vfx.Intensity, 0.1, 1.0) * 30));
            var initW = Width > 1 ? (float)Width : 800f;
            var initH = Height > 1 ? (float)Height : 600f;
            _lastWidth = Width > 1 ? (float)Width : 0f;
            _lastHeight = Height > 1 ? (float)Height : 0f;
            InitParticles(count, initW, initH);
        }
        else
        {
            _particles.Clear();
            _initialized = false;
        }

        if (!HasActiveVisuals())
        {
            StopAnimation();
            IsVisible = false;
            InvalidateSurface();
            return;
        }

        IsVisible = true;
        StartAnimation();
        InvalidateSurface();
    }

    //запуск анимации кадров
    public void StartAnimation()
    {
        if (!_stopwatch.IsRunning)
        {
            _stopwatch.Start();
            _lastFrameSeconds = _stopwatch.Elapsed.TotalSeconds;
        }

        if (_timer is null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += (_, _) =>
            {
                if (!IsVisible || !HasActiveVisuals())
                {
                    return;
                }

                var now = _stopwatch.Elapsed.TotalSeconds;
                var dt = (float)Math.Clamp(now - _lastFrameSeconds, 0.001, 0.1);
                _lastFrameSeconds = now;

                UpdatePhysics((float)Width, (float)Height, dt);
                InvalidateSurface();
            };
        }

        if (!_timer.IsRunning)
        {
            _lastFrameSeconds = _stopwatch.Elapsed.TotalSeconds;
            _timer.Start();
        }
    }

    //остановка анимации
    public void StopAnimation()
    {
        if (_timer?.IsRunning is true)
        {
            _timer.Stop();
        }
    }

    //освобождение ресурсов канваса и шейдеров
    //очистка загруженного растрового оверлея
    private void ClearOverlayBitmap()
    {
        _screenOverlayBitmap?.Dispose();
        _screenOverlayBitmap = null;
    }

    public void Dispose()
    {
        StopAnimation();
        ClearSprites();
        ClearOverlayBitmap();
        _bitmapPaint.Dispose();
        _fallbackPaint.Dispose();
        _shaderPaint.Dispose();
        _strokePaint.Dispose();
        _glowPaint.Dispose();
        _starPath.Dispose();
        _petalPath.Dispose();
        _diamondPath.Dispose();
        _wavePathBuilder.Dispose();
        GC.SuppressFinalize(this);
    }
}
