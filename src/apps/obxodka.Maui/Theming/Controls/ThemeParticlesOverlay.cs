#if WINDOWS
using Microsoft.UI.Xaml;
#endif

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

    private readonly SKPaint _bitmapPaint = new()
    {
        IsAntialias = true
    };

    private readonly SKPaint _fallbackPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };

    private readonly SKPaint _shaderPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill
    };

    private readonly SKPaint _strokePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke
    };

    private readonly SKPaint _glowPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        BlendMode = SKBlendMode.Plus
    };

    private readonly SKPath _starPath = CreateStarPath();
    private readonly SKPath _petalPath = CreatePetalPath();
    private readonly SKPath _diamondPath = CreateDiamondPath();
    private readonly SKPathBuilder _wavePathBuilder = new();
    private SKPath? _rayUnitPath;
    private readonly SKColorFilter?[] _tintFilterCache = new SKColorFilter?[32];
    private SKColor _lastTintFilterColor;
    private SKShader? _auroraRibbonShader0;
    private SKShader? _auroraRibbonShader1;
    private float _lastAuroraHeight;
    private SKColor _lastAuroraAccent;
    private SKColor _lastAuroraPrimary;
    private SKShader? _cyberBeamShader;
    private SKColor _lastCyberAccent;
    private SKShader? _vignetteShader;
    private float _lastVignetteW;
    private float _lastVignetteH;
    private SKColor _lastVignetteColor;
    private SKShader? _fogShader0;
    private SKShader? _fogShader1;
    private float _lastFogTopY;
    private float _lastFogH;
    private SKColor _lastFogPrimary;
    private SKColor _lastFogAccent;
    private SKShader? _rayShader;
    private float _lastRayLength;
    private SKColor _lastRayColor;
    private readonly Lock _frameLock = new();
    private readonly SKPaint _framePaint = new() { IsAntialias = true };
    private SKImage? _frameImage;
    private string? _frameImagePath;
    private FrameSlice? _frameSlice;
    private SKRectI _frameCachedCenter;
    private bool _frameUseNinePatch;
    private bool _hasScreenFrame;
    private float _screenFrameOpacity = 1f;
    private static readonly SKSamplingOptions s_samplingOptions = new(SKFilterMode.Linear);

#if WINDOWS
    private bool _vsyncSubscribed;
    private long _lastVsyncTickMs;
#else
    private IDispatcherTimer? _timer;
#endif
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
            var span = CollectionsMarshal.AsSpan(_particles);
            for (var i = 0; i < span.Length; i++)
            {
                span[i].X = Math.Clamp(span[i].X * ratioX, -20f, newW + 20f);
                span[i].Y = Math.Clamp(span[i].Y * ratioY, -20f, newH + 20f);
            }
        }
        else
        {
            var span = CollectionsMarshal.AsSpan(_particles);
            for (var i = 0; i < span.Length; i++)
            {
                span[i].X = (float)(_random.NextDouble() * newW);
                span[i].Y = (float)(_random.NextDouble() * newH);
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
        if (Handler?.PlatformView is UIElement element)
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
        IEnumerable<string>? paths = !string.IsNullOrWhiteSpace(spritePath) ? (string[])[spritePath] : null;
        Configure(paths, vfx, primaryColor, accentColor);
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
            IsVisible = _hasScreenFrame;
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
                        if (codec is
                            {
                                Info:
                                {
                                    Width: > 0 and <= SafeThemeExtractor.MaxImageDimension,
                                    Height: > 0 and <= SafeThemeExtractor.MaxImageDimension
                                }
                            })
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
                        AppLogger.LogWarning($"[ThemeParticles] Error decoding sprite '{path}': {ex.Message}");
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
                    if (codec is
                        {
                            Info:
                            {
                                Width: > 0 and <= SafeThemeExtractor.MaxImageDimension,
                                Height: > 0 and <= SafeThemeExtractor.MaxImageDimension
                            }
                        })
                    {
                        _screenOverlayBitmap = SKBitmap.Decode(codec);
                    }
#pragma warning restore CA1508
                }
                catch (Exception ex)
                {
                    AppLogger.LogWarning($"[ThemeParticles] Ошибка декодирования оверлея '{screenOverlayImagePath}': {ex.Message}");
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
            _customSpeedX = (float?)vfx.Physics?.SpeedX;
            _customSpeedY = (float?)vfx.Physics?.SpeedY;
            _customRotationSpeed = (float?)(vfx.Physics?.RotationSpeed ?? vfx.RotationSpeed);
            _customSwaySpeed = (float?)(vfx.Physics?.SwaySpeed ?? vfx.SwaySpeed);
            _customTwinkleSpeed = (float?)(vfx.Physics?.TwinkleSpeed ?? vfx.TwinkleSpeed);
            _connectLines = vfx.Physics?.ConnectLines ?? vfx.ConnectLines ?? false;

            _speedMultiplier = (float)Math.Clamp(vfx.Speed > 0 ? vfx.Speed : 1.0, 0.2, 5.0);
            _sizeMultiplier = (float)Math.Clamp(vfx.Size > 0 ? vfx.Size : 1.0, 0.2, 5.0);

            var count = (int)(18 + Math.Clamp(vfx.Intensity, 0.1, 1.0) * 30);
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
            IsVisible = _hasScreenFrame;
            InvalidateSurface();
            return;
        }

        IsVisible = true;
        if (HasAnimatedVisuals())
        {
            StartAnimation();
        }
        else
        {
            StopAnimation();
        }

        InvalidateSurface();
    }

    //установка 9-patch рамки экрана на единый холст без отдельного swapchain
    public void SetScreenFrame(string? imagePath, FrameSlice? slice)
    {
        lock (_frameLock)
        {
            if (string.Equals(_frameImagePath, imagePath, StringComparison.OrdinalIgnoreCase) && Equals(_frameSlice, slice))
            {
                return;
            }

            _frameImage?.Dispose();
            _frameImage = null;
            _frameImagePath = imagePath;
            _frameSlice = slice;
            _frameUseNinePatch = false;
            _frameCachedCenter = default;

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    _frameImage = SKImage.FromEncodedData(imagePath);
                }
                catch
                {
                    _frameImage = null;
                }
            }

            if (_frameImage is not null)
            {
                if (slice is { IsValid: true })
                {
                    var left = Math.Clamp(slice.Left, 0, _frameImage.Width / 2);
                    var top = Math.Clamp(slice.Top, 0, _frameImage.Height / 2);
                    var right = Math.Clamp(slice.Right, 0, _frameImage.Width / 2);
                    var bottom = Math.Clamp(slice.Bottom, 0, _frameImage.Height / 2);

                    _frameCachedCenter = new SKRectI(left, top, Math.Max(left + 1, _frameImage.Width - right),
                        Math.Max(top + 1, _frameImage.Height - bottom));
                    _frameUseNinePatch = true;
                }
                else if (DeviceInfo.Idiom == DeviceIdiom.Phone && _frameImage.Width > _frameImage.Height * 1.15f)
                {
                    var sliceX = Math.Max(1, (int)(_frameImage.Width * 0.12f));
                    var sliceY = Math.Max(1, (int)(_frameImage.Height * 0.12f));
                    _frameCachedCenter = new SKRectI(sliceX, sliceY, _frameImage.Width - sliceX, _frameImage.Height - sliceY);
                    _frameUseNinePatch = true;
                }
            }

            _hasScreenFrame = _frameImage is not null;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsVisible = _hasScreenFrame || HasAnimatedVisuals();
            InvalidateSurface();
        });
    }

    //очистка рамки экрана с освобождением нативного skimage
    public void ClearScreenFrame()
    {
        lock (_frameLock)
        {
            _frameImage?.Dispose();
            _frameImage = null;
            _frameImagePath = null;
            _frameSlice = null;
            _frameUseNinePatch = false;
            _frameCachedCenter = default;
            _hasScreenFrame = false;
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            IsVisible = HasAnimatedVisuals();
            InvalidateSurface();
        });
    }

    //изменение прозрачности рамки экрана при приближении курсора к краю
    public void SetScreenFrameOpacity(float opacity)
    {
        var clamped = Math.Clamp(opacity, 0f, 1f);
        if (Math.Abs(_screenFrameOpacity - clamped) < 0.01f)
        {
            return;
        }

        _screenFrameOpacity = clamped;
        if (_hasScreenFrame)
        {
            InvalidateSurface();
        }
    }

    //обработка одного кадра физики частиц с защитой от рывков dt
    private void StepAnimationFrame()
    {
        if (!IsVisible || !HasAnimatedVisuals())
        {
            return;
        }

        var now = _stopwatch.Elapsed.TotalSeconds;
        var dt = (float)Math.Clamp(now - _lastFrameSeconds, 0.001, 0.033);
        _lastFrameSeconds = now;

        UpdatePhysics((float)Width, (float)Height, dt);
        InvalidateSurface();
    }

#if WINDOWS
    //аппаратный такт перерисовки частиц по vsync экрана
    private void OnVsyncRendering(object? sender, object e)
    {
        var nowTicks = Environment.TickCount64;
        if (nowTicks - _lastVsyncTickMs < 15)
        {
            return;
        }

        _lastVsyncTickMs = nowTicks;
        StepAnimationFrame();
    }
#endif

    //запуск анимации кадров
    public void StartAnimation()
    {
        if (!_stopwatch.IsRunning)
        {
            _stopwatch.Start();
            _lastFrameSeconds = _stopwatch.Elapsed.TotalSeconds;
        }

#if WINDOWS
        if (_vsyncSubscribed)
        {
            return;
        }

        _lastFrameSeconds = _stopwatch.Elapsed.TotalSeconds;
        _lastVsyncTickMs = 0;
        _vsyncSubscribed = true;
        WinUiVsync.Subscribe(OnVsyncRendering);
#else
        if (_timer is null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += (_, _) => StepAnimationFrame();
        }

        if (!_timer.IsRunning)
        {
            _lastFrameSeconds = _stopwatch.Elapsed.TotalSeconds;
            _timer.Start();
        }
#endif
    }

    //остановка анимации
    public void StopAnimation()
    {
#if WINDOWS
        if (!_vsyncSubscribed)
        {
            return;
        }

        _vsyncSubscribed = false;
        WinUiVsync.Unsubscribe(OnVsyncRendering);
#else
        if (_timer?.IsRunning is true)
        {
            _timer.Stop();
        }
#endif
    }

    //освобождение ресурсов канваса и шейдеров
    //очистка загруженного растрового оверлея
    private void ClearOverlayBitmap()
    {
        _screenOverlayBitmap?.Dispose();
        _screenOverlayBitmap = null;
    }

    //очистка кэша цветовых фильтров частиц
    private void ClearTintFilterCache()
    {
        for (var i = 0; i < _tintFilterCache.Length; i++)
        {
            _tintFilterCache[i]?.Dispose();
            _tintFilterCache[i] = null;
        }
    }

    public void Dispose()
    {
        StopAnimation();
        ClearSprites();
        ClearOverlayBitmap();
        ClearTintFilterCache();
        lock (_frameLock)
        {
            _frameImage?.Dispose();
            _frameImage = null;
            _hasScreenFrame = false;
        }

        _framePaint.Dispose();
        _bitmapPaint.Dispose();
        _fallbackPaint.Dispose();
        _shaderPaint.Dispose();
        _strokePaint.Dispose();
        _glowPaint.Dispose();
        _starPath.Dispose();
        _petalPath.Dispose();
        _diamondPath.Dispose();
        _wavePathBuilder.Dispose();
        _rayUnitPath?.Dispose();
        _auroraRibbonShader0?.Dispose();
        _auroraRibbonShader1?.Dispose();
        _cyberBeamShader?.Dispose();
        _vignetteShader?.Dispose();
        _fogShader0?.Dispose();
        _fogShader1?.Dispose();
        _rayShader?.Dispose();
        GC.SuppressFinalize(this);
    }
}
