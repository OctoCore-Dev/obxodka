namespace obxodka.Maui.Controls;

public sealed partial class ThemeParticlesOverlay : SKCanvasView, IDisposable
{
    private enum MovementType
    {
        Falling,
        Rising,
        Floating
    }

    private sealed class Particle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float SpeedX { get; set; }
        public float SpeedY { get; set; }
        public float Rotation { get; set; }
        public float RotationSpeed { get; set; }
        public float Size { get; set; }
        public float Depth { get; set; } = 1.0f;
        public float BaseAlpha { get; set; }
        public float CurrentAlpha { get; set; }
        public float SwayPhase { get; set; }
        public float SwaySpeed { get; set; }
        public float TwinklePhase { get; set; }
        public float TwinkleSpeed { get; set; }
        public int SpriteIndex { get; set; }
    }

    private sealed class BurstParticle
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Vx { get; set; }
        public float Vy { get; set; }
        public float Size { get; set; }
        public float Alpha { get; set; }
        public float Life { get; set; }
        public float MaxLife { get; set; }
        public SKColor Color { get; set; }
    }

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
    private static readonly SKSamplingOptions t_samplingOptions = new(SKFilterMode.Linear);

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

    public ThemeParticlesOverlay()
    {
        InputTransparent = true;
        IgnorePixelScaling = false;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

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

    public void SetPointerPosition(float x, float y, bool active)
    {
        _pointerX = x;
        _pointerY = y;
        _pointerActive = active;
        if (active)
        {
            _lastPointerMoveTicks = Environment.TickCount64;
        }
    }

    public void TriggerPointerBurst(float x, float y)
    {
        if (!_interactiveParticles)
        {
            return;
        }

        var count = Math.Min(12, 36 - _burstParticles.Count);
        for (var i = 0; i < count; i++)
        {
            var angle = (float)(_random.NextDouble() * Math.PI * 2);
            var speed = (float)(1.8 + (_random.NextDouble() * 3.6));
            var life = (float)(0.35 + (_random.NextDouble() * 0.45));
            var color = _random.Next(2) == 0 ? _accentSkColor : _primarySkColor;

            _burstParticles.Add(new BurstParticle
            {
                X = x,
                Y = y,
                Vx = MathF.Cos(angle) * speed,
                Vy = MathF.Sin(angle) * speed,
                Size = (float)(3.0 + (_random.NextDouble() * 4.5)),
                Alpha = 1.0f,
                Life = 0f,
                MaxLife = life,
                Color = color
            });
        }

        if (!IsVisible && HasActiveVisuals())
        {
            IsVisible = true;
            StartAnimation();
        }
    }

    public void SetTrafficSpeedMbps(double totalMbps)
    {
        if (!_trafficReactive)
        {
            _targetTrafficMultiplier = 1.0f;
            return;
        }

        var safeMbps = Math.Max(0.0, totalMbps);
        var scaled = 1.0f + (float)(Math.Log10(1.0 + safeMbps) * 0.95);
        _targetTrafficMultiplier = Math.Clamp(scaled, 1.0f, 2.8f);
    }

    public void TriggerVpnStateEffect(AppVpnState state)
    {
        var previous = _vpnState;
        _vpnState = state;

        if (state == AppVpnState.Connected && previous != AppVpnState.Connected)
        {
            _shockwaveActive = true;
            _shockwaveProgress = 0f;
            if (!IsVisible && HasActiveVisuals())
            {
                IsVisible = true;
            }
            if (IsVisible)
            {
                StartAnimation();
            }
        }
    }

    private bool HasActiveVisuals()
    {
        var activeOverlay = GetActiveOverlayName();
        return (_initialized && _particles.Count > 0 && !string.Equals(_particleTypeName, "none", StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(activeOverlay) && !string.Equals(activeOverlay, "none", StringComparison.OrdinalIgnoreCase)) ||
               _shockwaveActive ||
               _burstParticles.Count > 0;
    }

    private string GetActiveOverlayName() =>
        _vpnState == AppVpnState.Connected &&
        !string.IsNullOrWhiteSpace(_activeOverrideOverlay) &&
        !string.Equals(_activeOverrideOverlay, "none", StringComparison.OrdinalIgnoreCase)
            ? _activeOverrideOverlay
            : _screenOverlay;

    public void Configure(string? spritePath, ThemeVfx? vfx, Color? primaryColor, Color? accentColor)
    {
        IEnumerable<string>? paths = !string.IsNullOrWhiteSpace(spritePath) ? [spritePath] : null;
        Configure(paths, vfx, primaryColor, accentColor, particlesEnabled: true);
    }

    public void Configure(
        IEnumerable<string>? spritePaths,
        ThemeVfx? vfx,
        Color? primaryColor,
        Color? accentColor,
        bool particlesEnabled = true)
    {
        ClearSprites();

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
                        using var codec = SKCodec.Create(path);
                        if (codec != null &&
                            codec.Info.Width > 0 &&
                            codec.Info.Height > 0 &&
                            codec.Info.Width <= SafeThemeExtractor.MaxImageDimension &&
                            codec.Info.Height <= SafeThemeExtractor.MaxImageDimension)
                        {
                            var bmp = SKBitmap.Decode(codec);
                            if (bmp != null)
                            {
                                loadedBmps.Add(bmp);
                            }
                        }
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

            _movementType = _particleTypeName switch
            {
                "stars" or "twinkle" or "floating" or "dust" or "cosmic" => MovementType.Floating,
                "rising" or "embers" or "sparks" or "neon_sparks" or "bubbles" or "fireflies" => MovementType.Rising,
                _ => MovementType.Falling
            };

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

    private void ClearSprites()
    {
        lock (_spriteBitmaps)
        {
            foreach (var bmp in _spriteBitmaps)
            {
                bmp.Dispose();
            }
            _spriteBitmaps.Clear();
            _bitmapPaint.ColorFilter = null;
        }
    }

    private void InitParticles(int count, float width, float height)
    {
        _particles.Clear();
        int spriteCount;
        lock (_spriteBitmaps)
        {
            spriteCount = _spriteBitmaps.Count;
        }

        for (var i = 0; i < count; i++)
        {
            float baseSize;
            float speedX;
            float speedY;
            float rotSpeed;
            float swaySpeed;
            float twinkleSpeed;

            switch (_particleTypeName)
            {
                case "neon_sparks":
                    baseSize = (float)(4 + (_random.NextDouble() * 10));
                    speedX = (float)((_random.NextDouble() - 0.5) * 1.1);
                    speedY = (float)(-1.6 - (_random.NextDouble() * 2.2));
                    rotSpeed = (float)((_random.NextDouble() - 0.5) * 4.5);
                    swaySpeed = (float)(0.04 + (_random.NextDouble() * 0.06));
                    twinkleSpeed = (float)(0.08 + (_random.NextDouble() * 0.12));
                    break;

                case "twinkle":
                    baseSize = (float)(5 + (_random.NextDouble() * 12));
                    speedX = (float)((_random.NextDouble() - 0.5) * 0.25);
                    speedY = (float)((_random.NextDouble() - 0.5) * 0.25);
                    rotSpeed = (float)((_random.NextDouble() - 0.5) * 1.2);
                    swaySpeed = (float)(0.015 + (_random.NextDouble() * 0.025));
                    twinkleSpeed = (float)(0.05 + (_random.NextDouble() * 0.09));
                    break;

                case "rain":
                    baseSize = (float)(12 + (_random.NextDouble() * 16));
                    speedX = (float)(0.8 + (_random.NextDouble() * 0.8));
                    speedY = (float)(4.5 + (_random.NextDouble() * 3.5));
                    rotSpeed = 0f;
                    swaySpeed = 0.01f;
                    twinkleSpeed = 0.02f;
                    break;

                case "matrix":
                    baseSize = (float)(10 + (_random.NextDouble() * 12));
                    speedX = 0f;
                    speedY = (float)(2.4 + (_random.NextDouble() * 2.8));
                    rotSpeed = 0f;
                    swaySpeed = 0f;
                    twinkleSpeed = (float)(0.06 + (_random.NextDouble() * 0.08));
                    break;

                case "fireflies":
                    baseSize = (float)(6 + (_random.NextDouble() * 11));
                    speedX = (float)((_random.NextDouble() - 0.5) * 0.6);
                    speedY = (float)(-0.5 - (_random.NextDouble() * 0.9));
                    rotSpeed = (float)((_random.NextDouble() - 0.5) * 1.5);
                    swaySpeed = (float)(0.03 + (_random.NextDouble() * 0.04));
                    twinkleSpeed = (float)(0.04 + (_random.NextDouble() * 0.07));
                    break;

                case "sakura_petals":
                case "crows_feathers":
                    baseSize = (float)(12 + (_random.NextDouble() * 16));
                    speedX = (float)(0.6 + (_random.NextDouble() * 1.3));
                    speedY = (float)(0.9 + (_random.NextDouble() * 1.5));
                    rotSpeed = (float)((_random.NextDouble() - 0.5) * 3.6);
                    swaySpeed = (float)(0.025 + (_random.NextDouble() * 0.045));
                    twinkleSpeed = (float)(0.02 + (_random.NextDouble() * 0.04));
                    break;

                default:
                    baseSize = _movementType switch
                    {
                        MovementType.Floating => (float)(4 + (_random.NextDouble() * 12)),
                        MovementType.Rising => (float)(5 + (_random.NextDouble() * 14)),
                        MovementType.Falling => (float)(9 + (_random.NextDouble() * 15)),
                        _ => (float)(9 + (_random.NextDouble() * 15))
                    };
                    speedY = _movementType switch
                    {
                        MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                        MovementType.Rising => (float)(-0.8 - (_random.NextDouble() * 1.5)),
                        MovementType.Falling => (float)(1.0 + (_random.NextDouble() * 1.8)),
                        _ => (float)(1.0 + (_random.NextDouble() * 1.8))
                    };
                    speedX = _movementType switch
                    {
                        MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                        MovementType.Rising => (float)((_random.NextDouble() - 0.5) * 0.8),
                        MovementType.Falling => (float)(0.3 + (_random.NextDouble() * 0.9)),
                        _ => (float)(0.3 + (_random.NextDouble() * 0.9))
                    };
                    rotSpeed = (float)((_random.NextDouble() - 0.5) * 3.0);
                    swaySpeed = (float)(0.02 + (_random.NextDouble() * 0.04));
                    twinkleSpeed = (float)(0.03 + (_random.NextDouble() * 0.06));
                    break;
            }

            var depth = (float)(0.40 + (_random.NextDouble() * 1.10));
            var baseAlpha = (float)(0.30 + (_random.NextDouble() * 0.45)) * (0.65f + (depth * 0.35f));

            _particles.Add(new Particle
            {
                X = (float)(_random.NextDouble() * width),
                Y = (float)(_random.NextDouble() * height),
                SpeedX = speedX * _speedMultiplier * depth,
                SpeedY = speedY * _speedMultiplier * depth,
                Rotation = _particleTypeName == "rain" ? 12f : (float)(_random.NextDouble() * 360),
                RotationSpeed = rotSpeed,
                Size = baseSize * _sizeMultiplier * depth,
                Depth = depth,
                BaseAlpha = baseAlpha,
                CurrentAlpha = baseAlpha,
                SwayPhase = (float)(_random.NextDouble() * Math.PI * 2),
                SwaySpeed = swaySpeed,
                TwinklePhase = (float)(_random.NextDouble() * Math.PI * 2),
                TwinkleSpeed = twinkleSpeed,
                SpriteIndex = spriteCount > 0 ? i % spriteCount : 0
            });
        }

        _initialized = true;
    }

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

    public void StopAnimation()
    {
        if (_timer?.IsRunning is true)
        {
            _timer.Stop();
        }
    }

    private void UpdatePhysics(float width, float height, float dt)
    {
        var w = width > 1f ? width : (_lastWidth > 1f ? _lastWidth : 800f);
        var h = height > 1f ? height : (_lastHeight > 1f ? _lastHeight : 600f);
        var step = dt * 36f;

        _shaderTime += dt;

        for (var i = _burstParticles.Count - 1; i >= 0; i--)
        {
            var bp = _burstParticles[i];
            bp.Life += dt;
            if (bp.Life >= bp.MaxLife)
            {
                _burstParticles.RemoveAt(i);
                continue;
            }

            var progress = bp.Life / bp.MaxLife;
            bp.Alpha = 1.0f - (progress * progress);
            bp.X += bp.Vx * step;
            bp.Y += bp.Vy * step;
            bp.Vx *= 0.94f;
            bp.Vy = (bp.Vy * 0.94f) + (0.06f * step);
        }

        if (_shockwaveActive)
        {
            _shockwaveProgress += dt * 1.15f;
            if (_shockwaveProgress >= 1.0f)
            {
                _shockwaveActive = false;
                _shockwaveProgress = 0f;
            }
        }

        if (_pointerActive && Environment.TickCount64 - _lastPointerMoveTicks > 2500)
        {
            _pointerActive = false;
        }

        var targetMult = _trafficReactive ? _targetTrafficMultiplier : 1.0f;
        if (_vpnState == AppVpnState.Connected)
        {
            targetMult = Math.Max(targetMult, _activeSpeedOverride);
        }
        _currentTrafficMultiplier += (targetMult - _currentTrafficMultiplier) * Math.Clamp(dt * 4.0f, 0.01f, 0.35f);

        if (!_initialized || _particles.Count == 0)
        {
            return;
        }

        var speedScale = _currentTrafficMultiplier * step;
        var centerX = w * 0.5f;
        var centerY = h * 0.5f;

        foreach (var p in _particles)
        {
            p.SwayPhase += p.SwaySpeed * step;
            var sway = _particleTypeName is "matrix" or "rain"
                ? 0f
                : (float)Math.Sin(p.SwayPhase) * step;

            p.TwinklePhase += p.TwinkleSpeed * step;
            var twinkleAmp = _particleTypeName is "twinkle" or "neon_sparks" ? 0.38f : 0.25f;
            var twinkle = (float)Math.Sin(p.TwinklePhase) * twinkleAmp;
            p.CurrentAlpha = Math.Clamp(p.BaseAlpha + twinkle, 0.08f, 1.0f);

            p.X += (p.SpeedX * speedScale) + sway;
            p.Y += p.SpeedY * speedScale;
            p.Rotation += p.RotationSpeed * step;

            if (_vpnState == AppVpnState.Connecting)
            {
                var cdx = centerX - p.X;
                var cdy = centerY - p.Y;
                var cDist = MathF.Sqrt((cdx * cdx) + (cdy * cdy));
                if (cDist > 18f && cDist < Math.Max(w, h) * 0.65f)
                {
                    var pull = (1.0f - (cDist / (Math.Max(w, h) * 0.65f))) * 1.35f * step;
                    var nx = cdx / cDist;
                    var ny = cdy / cDist;
                    p.X += (nx * pull) - (ny * pull * 0.6f);
                    p.Y += (ny * pull) + (nx * pull * 0.6f);
                }
            }

            if (_shockwaveActive)
            {
                var maxR = MathF.Sqrt((w * w) + (h * h)) * 0.65f;
                var easedR = 1f - MathF.Pow(1f - _shockwaveProgress, 3f);
                var waveR = 24f + (maxR * easedR);
                var cdx = p.X - centerX;
                var cdy = p.Y - centerY;
                var cDist = MathF.Sqrt((cdx * cdx) + (cdy * cdy));
                var delta = MathF.Abs(cDist - waveR);
                if (delta < 42f && cDist > 10f)
                {
                    var pushWave = (1f - (delta / 42f)) * 14f * (1f - _shockwaveProgress) * p.Depth;
                    var nx = cdx / cDist;
                    var ny = cdy / cDist;
                    p.X += nx * pushWave;
                    p.Y += ny * pushWave;
                    p.CurrentAlpha = Math.Min(1.0f, p.CurrentAlpha + 0.45f);
                }
            }

            if (_interactiveParticles && _pointerActive)
            {
                var dx = p.X - _pointerX;
                var dy = p.Y - _pointerY;
                var distSq = (dx * dx) + (dy * dy);
                const float repulsionRadius = 150f;
                if (distSq is > 0.01f and < repulsionRadius * repulsionRadius)
                {
                    var dist = MathF.Sqrt(distSq);
                    var factor = 1.0f - (dist / repulsionRadius);
                    var push = factor * factor * 5.2f * step * p.Depth;
                    var nx = dx / dist;
                    var ny = dy / dist;
                    p.X += (nx * push) - (ny * push * 0.45f * step);
                    p.Y += (ny * push) + (nx * push * 0.45f * step);
                    p.Rotation += factor * 3.5f * step;
                }
            }

            switch (_movementType)
            {
                case MovementType.Falling:
                    if (p.Y > h + 30 || p.X > w + 50 || p.X < -50)
                    {
                        p.Y = -25;
                        p.X = (float)((_random.NextDouble() * (w + 100)) - 50);
                        if (_particleTypeName is not ("rain" or "matrix"))
                        {
                            p.Rotation = (float)(_random.NextDouble() * 360);
                        }
                    }
                    break;

                case MovementType.Rising:
                    if (p.Y < -30 || p.X > w + 30 || p.X < -30)
                    {
                        p.Y = h + 25;
                        p.X = (float)(_random.NextDouble() * w);
                        p.Rotation = (float)(_random.NextDouble() * 360);
                    }
                    break;

                case MovementType.Floating:
                    if (p.X > w + 30)
                    {
                        p.X = -20;
                    }

                    if (p.X < -30)
                    {
                        p.X = w + 20;
                    }

                    if (p.Y > h + 30)
                    {
                        p.Y = -20;
                    }

                    if (p.Y < -30)
                    {
                        p.Y = h + 20;
                    }

                    break;

                default:
                    break;
            }
        }
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        if (!HasActiveVisuals())
        {
            return;
        }

        var logicalW = Width <= 1 ? e.Info.Width : (float)Width;
        var logicalH = Height <= 1 ? e.Info.Height : (float)Height;

        if (_lastWidth <= 1f && logicalW > 1f && logicalH > 1f && _particles.Count > 0)
        {
            _lastWidth = logicalW;
            _lastHeight = logicalH;
            foreach (var p in _particles)
            {
                p.X = (float)(_random.NextDouble() * logicalW);
                p.Y = (float)(_random.NextDouble() * logicalH);
            }
        }

        var scaleX = e.Info.Width / Math.Max(logicalW, 1f);
        var scaleY = e.Info.Height / Math.Max(logicalH, 1f);

        _ = canvas.Save();
        canvas.Scale(scaleX, scaleY);

        var activeOverlay = GetActiveOverlayName();
        if (!string.IsNullOrWhiteSpace(activeOverlay) && !string.Equals(activeOverlay, "none", StringComparison.OrdinalIgnoreCase))
        {
            DrawScreenOverlay(canvas, logicalW, logicalH, activeOverlay);
        }

        if (_initialized && _particles.Count > 0 && !string.Equals(_particleTypeName, "none", StringComparison.OrdinalIgnoreCase))
        {
            DrawParticles(canvas);
        }

        if (_shockwaveActive)
        {
            DrawShockwave(canvas, logicalW, logicalH);
        }

        canvas.Restore();
    }

    private void DrawParticles(SKCanvas canvas)
    {
        var connectConstellations = _particles.Count >= 2 &&
            (_particleTypeName is "twinkle" or "matrix" or "stars" or "neon_sparks" ||
             _screenOverlay is "cyber_hud");

        if (connectConstellations)
        {
            const float maxDist = 85f;
            const float maxDistSq = maxDist * maxDist;
            var lineBaseColor = _accentSkColor;

            for (var i = 0; i < _particles.Count; i++)
            {
                var p1 = _particles[i];
                var connections = 0;
                for (var j = i + 1; j < _particles.Count && connections < 2; j++)
                {
                    var p2 = _particles[j];
                    var dx = p1.X - p2.X;
                    var dy = p1.Y - p2.Y;
                    var distSq = (dx * dx) + (dy * dy);
                    if (distSq < maxDistSq)
                    {
                        var dist = MathF.Sqrt(distSq);
                        var ratio = 1.0f - (dist / maxDist);
                        var lineAlpha = (byte)Math.Clamp(ratio * ratio * 55f * Math.Min(p1.CurrentAlpha, p2.CurrentAlpha), 0f, 255f);
                        if (lineAlpha > 3)
                        {
                            _strokePaint.Color = lineBaseColor.WithAlpha(lineAlpha);
                            _strokePaint.StrokeWidth = 1.0f;
                            canvas.DrawLine(p1.X, p1.Y, p2.X, p2.Y, _strokePaint);
                            connections++;
                        }
                    }
                }
            }
        }

        lock (_spriteBitmaps)
        {
            var hasSprites = _spriteBitmaps.Count > 0;

            foreach (var p in _particles)
            {
                _ = canvas.Save();
                canvas.Translate(p.X, p.Y);
                canvas.RotateDegrees(p.Rotation);

                var alphaByte = (byte)Math.Clamp(p.CurrentAlpha * 255f, 0f, 255f);

                if (hasSprites)
                {
                    var bmp = _spriteBitmaps[p.SpriteIndex % _spriteBitmaps.Count];
                    var halfSize = p.Size * 0.5f;
                    var destRect = new SKRect(-halfSize, -halfSize, halfSize, halfSize);

                    if (_hasCustomTint)
                    {
                        var tint = _customTintColor.WithAlpha(alphaByte);
                        using var cf = SKColorFilter.CreateBlendMode(tint, SKBlendMode.Modulate);
                        _bitmapPaint.ColorFilter = cf;
                        _bitmapPaint.Color = SKColors.White;
                        canvas.DrawBitmap(bmp, destRect, t_samplingOptions, _bitmapPaint);
                        _bitmapPaint.ColorFilter = null;
                    }
                    else
                    {
                        _bitmapPaint.ColorFilter = null;
                        _bitmapPaint.Color = new SKColor(255, 255, 255, alphaByte);
                        canvas.DrawBitmap(bmp, destRect, t_samplingOptions, _bitmapPaint);
                    }
                }
                else
                {
                    _fallbackPaint.Color = _particleColor.WithAlpha(alphaByte);
                    var r = p.Size * 0.5f;

                    if (_particleTypeName == "rain")
                    {
                        _strokePaint.Color = _particleColor.WithAlpha(alphaByte);
                        _strokePaint.StrokeWidth = Math.Max(1.2f, r * 0.18f);
                        canvas.DrawLine(0, -r, 0, r, _strokePaint);
                    }
                    else if (_particleTypeName == "matrix")
                    {
                        var stepH = Math.Max(3f, r * 0.45f);
                        for (var k = 0; k < 3; k++)
                        {
                            var headAlpha = (byte)Math.Clamp(alphaByte * (1f - (k * 0.32f)), 10f, 255f);
                            _fallbackPaint.Color = (k == 0 ? SKColors.White : _particleColor).WithAlpha(headAlpha);
                            var rectY = -k * (stepH + 2f);
                            canvas.DrawRoundRect(-stepH * 0.4f, rectY, stepH * 0.8f, stepH, 1.5f, 1.5f, _fallbackPaint);
                        }
                    }
                    else if (_particleTypeName.Contains("star", StringComparison.OrdinalIgnoreCase) ||
                             _particleTypeName.Equals("twinkle", StringComparison.OrdinalIgnoreCase))
                    {
                        var glowR = r * 1.9f;
                        _glowPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.26f));
                        canvas.DrawCircle(0, 0, glowR, _glowPaint);

                        _ = canvas.Save();
                        var pulse = 0.85f + (0.3f * (p.CurrentAlpha / Math.Max(0.1f, p.BaseAlpha)));
                        canvas.Scale(r * 0.15f * pulse);
                        canvas.DrawPath(_starPath, _fallbackPaint);
                        canvas.Restore();
                    }
                    else if (_particleTypeName.Contains("snow", StringComparison.OrdinalIgnoreCase) ||
                             _particleTypeName.Contains("bubble", StringComparison.OrdinalIgnoreCase) ||
                             _particleTypeName.Contains("firefl", StringComparison.OrdinalIgnoreCase))
                    {
                        canvas.DrawCircle(0, 0, r * 0.65f, _fallbackPaint);
                        if (_particleTypeName.Contains("firefl", StringComparison.OrdinalIgnoreCase))
                        {
                            _fallbackPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.3f));
                            canvas.DrawCircle(0, 0, r * 1.35f, _fallbackPaint);
                        }
                    }
                    else if (_particleTypeName.Contains("spark", StringComparison.OrdinalIgnoreCase) ||
                             _particleTypeName.Contains("ember", StringComparison.OrdinalIgnoreCase))
                    {
                        var glowR = r * 1.85f;
                        _glowPaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.28f));
                        canvas.DrawCircle(0, 0, glowR, _glowPaint);

                        var trailLen = Math.Max(2.5f, r * 1.8f) * _currentTrafficMultiplier;
                        _strokePaint.Color = _particleColor.WithAlpha((byte)(alphaByte * 0.40f));
                        _strokePaint.StrokeWidth = Math.Max(1.0f, r * 0.35f);
                        canvas.DrawLine(0, 0, -p.SpeedX * trailLen, -p.SpeedY * trailLen, _strokePaint);

                        _ = canvas.Save();
                        canvas.Scale(r * 0.11f);
                        canvas.DrawPath(_diamondPath, _fallbackPaint);
                        canvas.Restore();
                    }
                    else
                    {
                        _ = canvas.Save();
                        canvas.Scale(r * 0.1f);
                        canvas.DrawPath(_petalPath, _fallbackPaint);
                        canvas.Restore();
                    }
                }

                canvas.Restore();
            }
        }

        foreach (var bp in _burstParticles)
        {
            var alphaByte = (byte)Math.Clamp(bp.Alpha * 255f, 0f, 255f);
            if (alphaByte == 0)
            {
                continue;
            }

            _glowPaint.Color = bp.Color.WithAlpha((byte)(alphaByte * 0.35f));
            canvas.DrawCircle(bp.X, bp.Y, bp.Size * 1.8f, _glowPaint);

            _fallbackPaint.Color = SKColors.White.WithAlpha(alphaByte);
            canvas.DrawCircle(bp.X, bp.Y, bp.Size * 0.6f, _fallbackPaint);
        }
    }

    private void DrawShockwave(SKCanvas canvas, float w, float h)
    {
        var maxRadius = MathF.Sqrt((w * w) + (h * h)) * 0.65f;
        var eased = 1f - MathF.Pow(1f - _shockwaveProgress, 3f);
        var radius = 24f + (maxRadius * eased);
        var alpha = (byte)Math.Clamp((1f - _shockwaveProgress) * 220f, 0f, 255f);

        _strokePaint.Color = _accentSkColor.WithAlpha(alpha);
        _strokePaint.StrokeWidth = Math.Max(2.5f, 16f * (1f - _shockwaveProgress));
        canvas.DrawCircle(w * 0.5f, h * 0.5f, radius, _strokePaint);

        if (radius > 32f)
        {
            var innerRadius = radius * 0.84f;
            var innerAlpha = (byte)Math.Clamp(alpha * 0.55f, 0f, 255f);
            _strokePaint.Color = _primarySkColor.WithAlpha(innerAlpha);
            _strokePaint.StrokeWidth = Math.Max(1.5f, 8f * (1f - _shockwaveProgress));
            canvas.DrawCircle(w * 0.5f, h * 0.5f, innerRadius, _strokePaint);
        }
    }

    private void DrawScreenOverlay(SKCanvas canvas, float w, float h, string overlay)
    {
        if (string.IsNullOrEmpty(overlay) || overlay.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!(ThemeManager.Instance?.FramesEnabled ?? true))
        {
            return;
        }

        switch (overlay)
        {
            case "cyber_hud":
                DrawCyberHudOverlay(canvas, w, h);
                break;
            case "scanlines":
                DrawScanlinesOverlay(canvas, w, h, 4f, 16);
                break;
            case "aurora":
                DrawAuroraOverlay(canvas, w, h);
                break;
            case "crimson_vignette":
                DrawCrimsonVignetteOverlay(canvas, w, h);
                break;
            case "mystic_fog":
                DrawMysticFogOverlay(canvas, w, h);
                break;
            case "golden_rays":
                DrawGoldenRaysOverlay(canvas, w, h);
                break;
            case "frost_vignette":
                DrawFrostVignetteOverlay(canvas, w, h);
                break;
            default:
                break;
        }
    }

    private void DrawScanlinesOverlay(SKCanvas canvas, float w, float h, float step, byte lineAlpha)
    {
        _strokePaint.Color = new SKColor(0, 0, 0, lineAlpha);
        _strokePaint.StrokeWidth = 1f;
        for (var y = 0f; y < h; y += step)
        {
            canvas.DrawLine(0, y, w, y, _strokePaint);
        }
    }

    private void DrawCyberHudOverlay(SKCanvas canvas, float w, float h)
    {
        DrawScanlinesOverlay(canvas, w, h, 5f, 14);

        var scanY = (_shaderTime * 90f % (h + 120f)) - 60f;
        using (var beamShader = SKShader.CreateLinearGradient(
            new SKPoint(0, scanY - 35f),
            new SKPoint(0, scanY + 35f),
            [_accentSkColor.WithAlpha(0), _accentSkColor.WithAlpha(28), _accentSkColor.WithAlpha(0)],
            [0f, 0.5f, 1f],
            SKShaderTileMode.Clamp))
        {
            _shaderPaint.Shader = beamShader;
            canvas.DrawRect(0, scanY - 35f, w, 70f, _shaderPaint);
            _shaderPaint.Shader = null;
        }

        var margin = 16f;
        var len = 26f;
        _strokePaint.Color = _accentSkColor.WithAlpha(95);
        _strokePaint.StrokeWidth = 1.6f;

        canvas.DrawLine(margin, margin, margin + len, margin, _strokePaint);
        canvas.DrawLine(margin, margin, margin, margin + len, _strokePaint);

        canvas.DrawLine(w - margin - len, margin, w - margin, margin, _strokePaint);
        canvas.DrawLine(w - margin, margin, w - margin, margin + len, _strokePaint);

        canvas.DrawLine(margin, h - margin, margin + len, h - margin, _strokePaint);
        canvas.DrawLine(margin, h - margin - len, margin, h - margin, _strokePaint);

        canvas.DrawLine(w - margin - len, h - margin, w - margin, h - margin, _strokePaint);
        canvas.DrawLine(w - margin, h - margin - len, w - margin, h - margin, _strokePaint);
    }

    private void DrawAuroraOverlay(SKCanvas canvas, float w, float h)
    {
        var ribbonHeight = Math.Min(h * 0.38f, 260f);
        for (var layer = 0; layer < 2; layer++)
        {
            _wavePathBuilder.Reset();
            var baseColor = layer == 0 ? _accentSkColor : _primarySkColor;
            var phaseOffset = (_shaderTime * (0.9f + (layer * 0.4f))) + (layer * 1.7f);

            _wavePathBuilder.MoveTo(0, 0);
            const int segments = 16;
            for (var i = 0; i <= segments; i++)
            {
                var x = w * i / segments;
                var wave = (MathF.Sin((i * 0.45f) + phaseOffset) * 28f) +
                           (MathF.Cos((i * 0.25f) - (phaseOffset * 0.7f)) * 18f);
                var y = (ribbonHeight * (0.55f + (layer * 0.18f))) + wave;
                _wavePathBuilder.LineTo(x, y);
            }
            _wavePathBuilder.LineTo(w, 0);
            _wavePathBuilder.Close();

            using var wavePath = _wavePathBuilder.Detach();
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(0, ribbonHeight),
                [baseColor.WithAlpha((byte)(48 - (layer * 14))), baseColor.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _shaderPaint.Shader = shader;
            canvas.DrawPath(wavePath, _shaderPaint);
            _shaderPaint.Shader = null;
        }
    }

    private void DrawCrimsonVignetteOverlay(SKCanvas canvas, float w, float h)
    {
        var pulse = 0.5f + (0.5f * MathF.Sin(_shaderTime * 2.2f));
        var boost = _vpnState == AppVpnState.Connected ? 1.35f : 1.0f;
        var edgeAlpha = (byte)Math.Clamp((42f + (pulse * 28f)) * boost, 0f, 135f);
        var crimson = new SKColor(220, 20, 50, edgeAlpha);

        var center = new SKPoint(w * 0.5f, h * 0.5f);
        var radius = MathF.Max(w, h) * 0.72f;
        using var shader = SKShader.CreateRadialGradient(
            center,
            radius,
            [SKColors.Transparent, crimson.WithAlpha((byte)(edgeAlpha * 0.35f)), crimson],
            [0.45f, 0.80f, 1.0f],
            SKShaderTileMode.Clamp);
        _shaderPaint.Shader = shader;
        canvas.DrawRect(0, 0, w, h, _shaderPaint);
        _shaderPaint.Shader = null;
    }

    private void DrawMysticFogOverlay(SKCanvas canvas, float w, float h)
    {
        var fogHeight = Math.Min(h * 0.32f, 220f);
        var topY = h - fogHeight;

        for (var layer = 0; layer < 2; layer++)
        {
            _wavePathBuilder.Reset();
            var phase = (_shaderTime * (0.65f + (layer * 0.35f))) + (layer * 2.1f);
            _wavePathBuilder.MoveTo(0, h);

            const int segments = 16;
            for (var i = 0; i <= segments; i++)
            {
                var x = w * i / segments;
                var offset = MathF.Sin((i * 0.4f) + phase) * 20f;
                var y = topY + (layer * 28f) + offset;
                _wavePathBuilder.LineTo(x, y);
            }

            _wavePathBuilder.LineTo(w, h);
            _wavePathBuilder.Close();

            using var wavePath = _wavePathBuilder.Detach();
            var fogColor = (layer == 0 ? _primarySkColor : _accentSkColor).WithAlpha((byte)(42 - (layer * 12)));
            using var shader = SKShader.CreateLinearGradient(
                new SKPoint(0, topY),
                new SKPoint(0, h),
                [fogColor.WithAlpha(0), fogColor],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _shaderPaint.Shader = shader;
            canvas.DrawPath(wavePath, _shaderPaint);
            _shaderPaint.Shader = null;
        }
    }

    private void DrawGoldenRaysOverlay(SKCanvas canvas, float w, float h)
    {
        var origin = new SKPoint(w * 0.5f, -30f);
        var rayLength = MathF.Max(w, h) * 0.85f;
        var gold = new SKColor(255, 210, 90);

        for (var i = -2; i <= 2; i++)
        {
            var baseAngle = (MathF.PI * 0.5f) + (i * 0.22f) + (MathF.Sin((_shaderTime * 0.7f) + i) * 0.05f);
            var spread = 0.09f;

            var p1 = new SKPoint(
                origin.X + (MathF.Cos(baseAngle - spread) * rayLength),
                origin.Y + (MathF.Sin(baseAngle - spread) * rayLength));
            var p2 = new SKPoint(
                origin.X + (MathF.Cos(baseAngle + spread) * rayLength),
                origin.Y + (MathF.Sin(baseAngle + spread) * rayLength));

            _wavePathBuilder.Reset();
            _wavePathBuilder.MoveTo(origin);
            _wavePathBuilder.LineTo(p1);
            _wavePathBuilder.LineTo(p2);
            _wavePathBuilder.Close();

            using var wavePath = _wavePathBuilder.Detach();
            var alpha = (byte)(26 - (Math.Abs(i) * 4));
            using var shader = SKShader.CreateLinearGradient(
                origin,
                new SKPoint(origin.X, rayLength * 0.75f),
                [gold.WithAlpha(alpha), gold.WithAlpha(0)],
                [0f, 1f],
                SKShaderTileMode.Clamp);
            _shaderPaint.Shader = shader;
            canvas.DrawPath(wavePath, _shaderPaint);
            _shaderPaint.Shader = null;
        }
    }

    private void DrawFrostVignetteOverlay(SKCanvas canvas, float w, float h)
    {
        var shimmer = 0.5f + (0.5f * MathF.Sin(_shaderTime * 1.6f));
        var edgeAlpha = (byte)(36 + (shimmer * 22f));
        var ice = new SKColor(150, 230, 255, edgeAlpha);

        var center = new SKPoint(w * 0.5f, h * 0.5f);
        var radius = MathF.Max(w, h) * 0.72f;
        using var shader = SKShader.CreateRadialGradient(
            center,
            radius,
            [SKColors.Transparent, ice.WithAlpha((byte)(edgeAlpha * 0.3f)), ice],
            [0.52f, 0.84f, 1.0f],
            SKShaderTileMode.Clamp);
        _shaderPaint.Shader = shader;
        canvas.DrawRect(0, 0, w, h, _shaderPaint);
        _shaderPaint.Shader = null;
    }

    private static SKPath CreateDiamondPath()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0, -10);
        builder.LineTo(6, 0);
        builder.LineTo(0, 10);
        builder.LineTo(-6, 0);
        builder.Close();
        return builder.Detach();
    }

    private static SKPath CreateStarPath()
    {
        using var builder = new SKPathBuilder();
        const int points = 5;
        const float outerRadius = 10f;
        const float innerRadius = 4f;

        for (var i = 0; i < points * 2; i++)
        {
            var radius = i % 2 == 0 ? outerRadius : innerRadius;
            var angle = (i * Math.PI / points) - (Math.PI / 2);
            var x = (float)(radius * Math.Cos(angle));
            var y = (float)(radius * Math.Sin(angle));

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

    private static SKPath CreatePetalPath()
    {
        using var builder = new SKPathBuilder();
        builder.MoveTo(0, -10);
        builder.CubicTo(6, -4, 8, 5, 0, 10);
        builder.CubicTo(-8, 5, -6, -4, 0, -10);
        builder.Close();
        return builder.Detach();
    }

    public void Dispose()
    {
        StopAnimation();
        ClearSprites();
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
