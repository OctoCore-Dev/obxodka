#pragma warning disable CS0618

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
        public float BaseAlpha { get; set; }
        public float CurrentAlpha { get; set; }
        public float SwayPhase { get; set; }
        public float SwaySpeed { get; set; }
        public float TwinklePhase { get; set; }
        public float TwinkleSpeed { get; set; }
    }

    private readonly List<Particle> _particles = [];
    private readonly Random _random = new(1337);
    private readonly SKPaint _bitmapPaint = new() { IsAntialias = true };
    private readonly SKPaint _fallbackPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPath _starPath = CreateStarPath();
    private readonly SKPath _petalPath = CreatePetalPath();
    private readonly SKPath _diamondPath = CreateDiamondPath();

    private SKBitmap? _spriteBitmap;
    private IDispatcherTimer? _timer;
    private MovementType _movementType = MovementType.Falling;
    private string _particleTypeName = "none";
    private SKColor _particleColor = new(255, 255, 255, 180);
    private float _speedMultiplier = 1.0f;
    private float _sizeMultiplier = 1.0f;
    private bool _initialized;
    private bool _resized;

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
        if (Width > 0 && Height > 0 && _particles.Count > 0 && !_resized)
        {
            _resized = true;
            foreach (var p in _particles)
            {
                p.X = (float)(_random.NextDouble() * Width);
                p.Y = (float)(_random.NextDouble() * Height);
            }
            InvalidateSurface();
        }
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

    public void Configure(string? spritePath, ThemeVfx? vfx, Color? primaryColor, Color? accentColor)
    {
        _resized = false;
        _spriteBitmap?.Dispose();
        _spriteBitmap = null;

        if (vfx == null || string.Equals(vfx.Particles, "none", StringComparison.OrdinalIgnoreCase))
        {
            _particleTypeName = "none";
            StopAnimation();
            IsVisible = false;
            InvalidateSurface();
            return;
        }

        _particleTypeName = vfx.Particles.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(spritePath) && File.Exists(spritePath))
        {
            try
            {
                _spriteBitmap = SKBitmap.Decode(spritePath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ThemeParticles] Error decoding sprite '{spritePath}': {ex.Message}");
            }
        }

        _movementType = _particleTypeName switch
        {
            "stars" or "floating" or "dust" or "cosmic" => MovementType.Floating,
            "rising" or "embers" or "sparks" or "bubbles" or "fireflies" => MovementType.Rising,
            _ => MovementType.Falling
        };

        _speedMultiplier = (float)Math.Clamp(vfx.Speed > 0 ? vfx.Speed : 1.0, 0.2, 5.0);
        _sizeMultiplier = (float)Math.Clamp(vfx.Size > 0 ? vfx.Size : 1.0, 0.2, 5.0);

        _particleColor = !string.IsNullOrWhiteSpace(vfx.Color) && SKColor.TryParse(vfx.Color, out var parsedColor)
            ? parsedColor
            : accentColor != null
                ? new SKColor(
                (byte)(accentColor.Red * 255),
                (byte)(accentColor.Green * 255),
                (byte)(accentColor.Blue * 255),
                220)
                : primaryColor != null
                ? new SKColor(
                (byte)(primaryColor.Red * 255),
                (byte)(primaryColor.Green * 255),
                (byte)(primaryColor.Blue * 255),
                220)
                : new SKColor(255, 255, 255, 200);

        _fallbackPaint.Color = _particleColor;

        var count = (int)(18 + (Math.Clamp(vfx.Intensity, 0.1, 1.0) * 26));
        InitParticles(count, (float)Math.Max(Width, 400), (float)Math.Max(Height, 600));

        IsVisible = true;
        StartAnimation();
        InvalidateSurface();
    }

    private void InitParticles(int count, float width, float height)
    {
        _particles.Clear();

        for (var i = 0; i < count; i++)
        {
            var baseSize = _movementType switch
            {
                MovementType.Floating => (float)(4 + (_random.NextDouble() * 12)),
                MovementType.Rising => (float)(5 + (_random.NextDouble() * 14)),
                MovementType.Falling => (float)(10 + (_random.NextDouble() * 16)),
                _ => (float)(8 + (_random.NextDouble() * 18))
            } * _sizeMultiplier;

            var speedY = _movementType switch
            {
                MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                MovementType.Rising => (float)(-0.8 - (_random.NextDouble() * 1.5)),
                MovementType.Falling => (float)(1.0 + (_random.NextDouble() * 1.8)),
                _ => (float)(1.0 + (_random.NextDouble() * 1.8))
            } * _speedMultiplier;

            var speedX = _movementType switch
            {
                MovementType.Floating => (float)((_random.NextDouble() - 0.5) * 0.3),
                MovementType.Rising => (float)((_random.NextDouble() - 0.5) * 0.8),
                MovementType.Falling => (float)(0.4 + (_random.NextDouble() * 1.0)),
                _ => (float)(0.4 + (_random.NextDouble() * 1.0))
            } * _speedMultiplier;

            var baseAlpha = (float)(0.35 + (_random.NextDouble() * 0.55));

            _particles.Add(new Particle
            {
                X = (float)(_random.NextDouble() * width),
                Y = (float)(_random.NextDouble() * height),
                SpeedX = speedX,
                SpeedY = speedY,
                Rotation = (float)(_random.NextDouble() * 360),
                RotationSpeed = (float)((_random.NextDouble() - 0.5) * 2.5),
                Size = baseSize,
                BaseAlpha = baseAlpha,
                CurrentAlpha = baseAlpha,
                SwayPhase = (float)(_random.NextDouble() * Math.PI * 2),
                SwaySpeed = (float)(0.02 + (_random.NextDouble() * 0.04)),
                TwinklePhase = (float)(_random.NextDouble() * Math.PI * 2),
                TwinkleSpeed = (float)(0.03 + (_random.NextDouble() * 0.06))
            });
        }

        _initialized = true;
    }

    public void StartAnimation()
    {
        if (_timer == null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += (s, e) =>
            {
                if (IsVisible && _initialized && _particles.Count > 0)
                {
                    UpdatePhysics((float)Width, (float)Height);
                    InvalidateSurface();
                }
            };
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    public void StopAnimation()
    {
        if (_timer?.IsRunning == true)
        {
            _timer.Stop();
        }
    }

    private void UpdatePhysics(float width, float height)
    {
        var w = Math.Max(width, 400f);
        var h = Math.Max(height, 600f);

        foreach (var p in _particles)
        {
            p.SwayPhase += p.SwaySpeed;
            var sway = (float)Math.Sin(p.SwayPhase) * 1.0f;

            p.TwinklePhase += p.TwinkleSpeed;
            var twinkle = (float)Math.Sin(p.TwinklePhase) * 0.25f;
            p.CurrentAlpha = Math.Clamp(p.BaseAlpha + twinkle, 0.1f, 1.0f);

            p.X += p.SpeedX + sway;
            p.Y += p.SpeedY;
            p.Rotation += p.RotationSpeed;

            switch (_movementType)
            {
                case MovementType.Falling:
                    if (p.Y > h + 30 || p.X > w + 50 || p.X < -50)
                    {
                        p.Y = -25;
                        p.X = (float)((_random.NextDouble() * (w + 100)) - 50);
                        p.Rotation = (float)(_random.NextDouble() * 360);
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

        if (!_initialized || _particles.Count == 0)
        {
            return;
        }

        var scaleX = e.Info.Width / (float)Math.Max(Width, 1);
        var scaleY = e.Info.Height / (float)Math.Max(Height, 1);

        _ = canvas.Save();
        canvas.Scale(scaleX, scaleY);

        foreach (var p in _particles)
        {
            _ = canvas.Save();
            canvas.Translate(p.X, p.Y);
            canvas.RotateDegrees(p.Rotation);

            if (_spriteBitmap != null)
            {
                var halfSize = p.Size * 0.5f;
                var destRect = new SKRect(-halfSize, -halfSize, halfSize, halfSize);

                _bitmapPaint.Color = new SKColor(255, 255, 255, (byte)(p.CurrentAlpha * 255));
                canvas.DrawBitmap(_spriteBitmap, destRect, _bitmapPaint);
            }
            else
            {
                var alphaByte = (byte)(p.CurrentAlpha * 255);
                _fallbackPaint.Color = _particleColor.WithAlpha(alphaByte);

                var r = p.Size * 0.5f;

                if (_particleTypeName.Contains("star", StringComparison.OrdinalIgnoreCase))
                {
                    _ = canvas.Save();
                    canvas.Scale(r * 0.15f);
                    canvas.DrawPath(_starPath, _fallbackPaint);
                    canvas.Restore();
                }
                else if (_particleTypeName.Contains("snow", StringComparison.OrdinalIgnoreCase) ||
                         _particleTypeName.Contains("bubble", StringComparison.OrdinalIgnoreCase))
                {
                    canvas.DrawCircle(0, 0, r, _fallbackPaint);
                }
                else if (_particleTypeName.Contains("spark", StringComparison.OrdinalIgnoreCase) ||
                         _particleTypeName.Contains("ember", StringComparison.OrdinalIgnoreCase))
                {
                    _ = canvas.Save();
                    canvas.Scale(r * 0.1f);
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

        canvas.Restore();
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
        _spriteBitmap?.Dispose();
        _spriteBitmap = null;
        _bitmapPaint.Dispose();
        _fallbackPaint.Dispose();
        _starPath.Dispose();
        _petalPath.Dispose();
        _diamondPath.Dispose();
        GC.SuppressFinalize(this);
    }
}
