namespace obxodka.Maui.Controls;

public partial class OrbLoader : SKCanvasView, IDisposable
{
    private static ReadOnlySpan<float> Angles => [0f, 1f, -1f, 0.5f, -0.5f, 1.5f, -1.5f];
    private static ReadOnlySpan<float> Offsets => [0f, 0f, 0f, 60f, -60f, 120f, -90f];
    private static readonly (float X, float Y)[] t_origins =
    [
        (0.5f, 0.5f), (0.5f, 0.5f), (0.5f, 0.6f),
        (0.4f, 0.4f), (0.4f, 0.4f), (0.6f, 0.4f), (0.6f, 0.4f)
    ];

    public static readonly BindableProperty IsAnimatingProperty =
        BindableProperty.Create(
            nameof(IsAnimating),
            typeof(bool),
            typeof(OrbLoader),
            false,
            propertyChanged: static (b, _, n) => ((OrbLoader)b).OnAnimatingChanged((bool)n));

    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }

    private float _angle;
    private float _phase;
    private IDispatcherTimer? _timer;
    private EventHandler? _timerTick;
    private readonly Stopwatch _stopwatch = new();
    private readonly SKPaint _glowPaint = new() { IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18f) };
    private readonly SKPaint _bgPaint = new() { IsAntialias = true };
    private readonly SKPaint _borderPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private readonly SKPaint _polyPaint = new() { IsAntialias = true };

    public OrbLoader() => Unloaded += (_, _) => Dispose();

    private void OnAnimatingChanged(bool animating)
    {
        if (animating)
        {
            StartTimer();
        }
        else
        {
            StopTimer();
        }
    }

    private void StartTimer()
    {
        if (_timer is not null)
        {
            if (!_timer.IsRunning)
            {
                _timer.Start();
            }
            return;
        }

        _stopwatch.Restart();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timerTick = (_, _) =>
        {
            var seconds = (float)_stopwatch.Elapsed.TotalSeconds;
            _angle = seconds * 112.5f % 360f;
            _phase = (MathF.Sin(seconds * 1.8f) + 1f) * 0.5f;
            InvalidateSurface();
        };
        _timer.Tick += _timerTick;
        _timer.Start();
    }

    private void StopTimer()
    {
        if (_timer is not null)
        {
            if (_timerTick is not null)
            {
                _timer.Tick -= _timerTick;
                _timerTick = null;
            }
            if (_timer.IsRunning)
            {
                _timer.Stop();
            }
            _timer = null;
        }

        _stopwatch.Reset();
    }

    private static (SKColor Primary, SKColor Accent) GetThemeColors()
    {
        var primary = new SKColor(0x7C, 0x3A, 0xED);
        var accent = new SKColor(0x00, 0xE5, 0xFF);

        var res = Application.Current?.Resources;
        if (res is not null)
        {
            if (res.TryGetValue("Primary", out var pObj) && pObj is Color pCol)
            {
                primary = new SKColor((byte)(pCol.Red * 255), (byte)(pCol.Green * 255), (byte)(pCol.Blue * 255));
            }
            if (res.TryGetValue("Accent", out var aObj) && aObj is Color aCol)
            {
                accent = new SKColor((byte)(aCol.Red * 255), (byte)(aCol.Green * 255), (byte)(aCol.Blue * 255));
            }
        }

        return (primary, accent);
    }

    private static SKColor LerpColor(SKColor a, SKColor b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return new SKColor(
            (byte)(a.Red + ((b.Red - a.Red) * t)),
            (byte)(a.Green + ((b.Green - a.Green) * t)),
            (byte)(a.Blue + ((b.Blue - a.Blue) * t)),
            (byte)(a.Alpha + ((b.Alpha - a.Alpha) * t)));
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var (w, h) = (e.Info.Width, e.Info.Height);
        float cx = w / 2f, cy = h / 2f;
        var r = (Math.Min(w, h) / 2f) - 8f;

        canvas.Clear(SKColors.Transparent);

        var (primary, accent) = GetThemeColors();
        var mainColor = LerpColor(primary, accent, _phase);
        var altColor = LerpColor(accent, primary, _phase);

        _glowPaint.Color = mainColor.WithAlpha(60);
        canvas.DrawCircle(cx, cy, r, _glowPaint);

        _bgPaint.Shader?.Dispose();
        _bgPaint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy - (r * 0.3f)),
            r,
            [
                mainColor.WithAlpha(40),
                altColor.WithAlpha(80)
            ],
            SKShaderTileMode.Clamp);
        canvas.DrawCircle(cx, cy, r, _bgPaint);

        _borderPaint.Color = mainColor.WithAlpha(180);
        canvas.DrawCircle(cx, cy, r, _borderPaint);

        DrawPolygons(canvas, cx, cy, r, primary, accent);
    }

    private void DrawPolygons(SKCanvas canvas, float cx, float cy, float r, SKColor primary, SKColor accent)
    {
        for (var i = 0; i < 4; i++)
        {
            var rot = (_angle * Angles[i]) + Offsets[i];
            var alpha = 120f + (i * 20f);
            var t = (_phase + (i * 0.25f)) % 1f;

            _polyPaint.Color = LerpColor(primary, accent, t).WithAlpha((byte)alpha);
            _polyPaint.MaskFilter?.Dispose();
            _polyPaint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f);

            _ = canvas.Save();
            var ox = cx + ((t_origins[i].X - 0.5f) * r);
            var oy = cy + ((t_origins[i].Y - 0.5f) * r);

            canvas.RotateDegrees(rot, ox, oy);
            var pr = r * 0.45f;

            using var path = MakePolygon(ox, oy, pr, 5);
            canvas.DrawPath(path, _polyPaint);
            canvas.Restore();
        }
    }

    private static SKPath MakePolygon(float cx, float cy, float r, int sides)
    {
        using var builder = new SKPathBuilder();
        var step = MathF.Tau / sides;
        var offset = MathF.PI / 2f;

        for (var i = 0; i < sides; i++)
        {
            var a = (i * step) - offset;
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

    public void Dispose()
    {
        StopTimer();
        _glowPaint.Dispose();
        _bgPaint.Shader?.Dispose();
        _bgPaint.Dispose();
        _borderPaint.Dispose();
        _polyPaint.Dispose();
        _stopwatch.Reset();
        GC.SuppressFinalize(this);
    }
}
