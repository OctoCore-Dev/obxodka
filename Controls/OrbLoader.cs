namespace obxodka.Controls;

public partial class OrbLoader : SKCanvasView
{
    private float _angle;
    private float _hue;
    private IDispatcherTimer? _timer;
    public static readonly BindableProperty IsAnimatingProperty =
        BindableProperty.Create(nameof(IsAnimating), typeof(bool), typeof(OrbLoader), false,
            propertyChanged: (b, o, n) => ((OrbLoader)b).OnAnimatingChanged((bool)n));
    public bool IsAnimating
    {
        get => (bool)GetValue(IsAnimatingProperty);
        set => SetValue(IsAnimatingProperty, value);
    }
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
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(16);
        _timer.Tick += (s, e) =>
        {
            _angle += 1.8f;
            _hue = (_hue + 0.3f) % 360f;
            InvalidateSurface();
        };
        _timer.Start();
    }
    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }
    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        var w = e.Info.Width;
        var h = e.Info.Height;
        float cx = w / 2f, cy = h / 2f;
        var r = (Math.Min(w, h) / 2f) - 8f;
        canvas.Clear(SKColors.Transparent);
        using var glowPaint = new SKPaint
        {
            IsAntialias = true,
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 18f)
        };
        glowPaint.Color = SKColor.FromHsv(_hue, 80f, 100f).WithAlpha(60);
        canvas.DrawCircle(cx, cy, r, glowPaint);
        using var bgPaint = new SKPaint { IsAntialias = true };
        bgPaint.Shader = SKShader.CreateRadialGradient(
            new SKPoint(cx, cy - (r * 0.3f)), r,
            [
                SKColor.FromHsv(_hue, 60f, 100f).WithAlpha(40),
                SKColor.FromHsv((_hue + 30f) % 360f, 90f, 70f).WithAlpha(80)
            ],
            SKShaderTileMode.Clamp);
        canvas.DrawCircle(cx, cy, r, bgPaint);
        using var borderPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = SKColor.FromHsv(_hue, 70f, 100f).WithAlpha(180)
        };
        canvas.DrawCircle(cx, cy, r, borderPaint);
        DrawPolygons(canvas, cx, cy, r);
    }
    private void DrawPolygons(SKCanvas canvas, float cx, float cy, float r)
    {
        var angles = new[] { 0f, 1f, -1f, 0.5f, -0.5f, 1.5f, -1.5f };
        var offsets = new[] { 0f, 0f, 0f, 60f, -60f, 120f, -90f };
        var origins = new (float x, float y)[]
        {
            (0.5f, 0.5f), (0.5f, 0.5f), (0.5f, 0.6f),
            (0.4f, 0.4f), (0.4f, 0.4f), (0.6f, 0.4f), (0.6f, 0.4f)
        };
        using var paint = new SKPaint { IsAntialias = true };
        for (var i = 0; i < 4; i++)
        {
            var rot = (_angle * angles[i]) + offsets[i];
            var alpha = 120f + (i * 20f);
            paint.Color = SKColor.FromHsv((_hue + (i * 25f)) % 360f, 85f, 100f).WithAlpha((byte)alpha);
            paint.MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 10f);
            _ = canvas.Save();
            var ox = cx + ((origins[i].x - 0.5f) * r);
            var oy = cy + ((origins[i].y - 0.5f) * r);
            canvas.RotateDegrees(rot, ox, oy);
            var pr = r * 0.45f;
            var path = MakePolygon(ox, oy, pr, 5);
            canvas.DrawPath(path, paint);
            canvas.Restore();
        }
    }
#pragma warning disable CS0618
    private static SKPath MakePolygon(float cx, float cy, float r, int sides)
    {
        var path = new SKPath();
        for (var i = 0; i < sides; i++)
        {
            var a = (float)((i * 2 * Math.PI / sides) - (Math.PI / 2));
            var x = cx + (r * MathF.Cos(a));
            var y = cy + (r * MathF.Sin(a));
            if (i == 0)
            {
                path.MoveTo(x, y);
            }
            else
            {
                path.LineTo(x, y);
            }
        }
        path.Close();
        return path;
    }
#pragma warning restore CS0618
}
