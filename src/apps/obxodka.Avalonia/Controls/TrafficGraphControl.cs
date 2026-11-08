namespace obxodka.Avalonia.Controls;

public class TrafficGraphControl : Control
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<TrafficGraphControl, bool>(nameof(IsActive), defaultValue: true);

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private static readonly IPen t_gridPen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1, DashStyle.Dash);
    private static readonly IPen t_upPen = new Pen(new SolidColorBrush(Color.Parse("#9F6FF0")), 2);
    private static readonly IPen t_downPen = new Pen(new SolidColorBrush(Color.Parse("#00E5FF")), 2);

    private static readonly IBrush t_upSolidBrush = new SolidColorBrush(Color.Parse("#9F6FF0"));
    private static readonly IBrush t_downSolidBrush = new SolidColorBrush(Color.Parse("#00E5FF"));

    private readonly DispatcherTimer _animationTimer;
    private double _timePhase;
    private double _pulsePhase;

    public TrafficGraphControl()
    {
        _animationTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20) // ~50 FPS silky smooth packet flow
        };
        _animationTimer.Tick += OnAnimationTick;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _animationTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animationTimer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        _timePhase += 0.09;
        if (_timePhase > 2000.0)
        {
            _timePhase = 0;
        }

        _pulsePhase += 0.12;
        if (_pulsePhase > Math.PI * 2)
        {
            _pulsePhase -= Math.PI * 2;
        }

        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        double width = Bounds.Width;
        double height = Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Draw 3 horizontal dashed grid lines
        for (int i = 1; i <= 3; i++)
        {
            double y = height * (i / 4.0);
            context.DrawLine(t_gridPen, new Point(0, y), new Point(width, y));
        }

        // Draw streaming upload packets (purple, bottom curve)
        DrawStreamingWave(
            context,
            strokePen: t_upPen,
            fillStartColor: Color.FromArgb(55, 159, 111, 240),
            glowColor: Color.FromArgb(90, 159, 111, 240),
            solidBrush: t_upSolidBrush,
            isUpload: true,
            width: width,
            height: height,
            phase: _timePhase,
            pulsePhase: _pulsePhase,
            isActive: IsActive);

        // Draw streaming download packets (cyan, top curve)
        DrawStreamingWave(
            context,
            strokePen: t_downPen,
            fillStartColor: Color.FromArgb(75, 0, 229, 255),
            glowColor: Color.FromArgb(120, 0, 229, 255),
            solidBrush: t_downSolidBrush,
            isUpload: false,
            width: width,
            height: height,
            phase: _timePhase,
            pulsePhase: _pulsePhase,
            isActive: IsActive);
    }

    private static void DrawStreamingWave(
        DrawingContext context,
        IPen strokePen,
        Color fillStartColor,
        Color glowColor,
        IBrush solidBrush,
        bool isUpload,
        double width,
        double height,
        double phase,
        double pulsePhase,
        bool isActive)
    {
        const int pointsCount = 48;
        double stepX = width / (pointsCount - 1);

        var strokeGeometry = new StreamGeometry();
        var fillGeometry = new StreamGeometry();

        Point firstPoint = new(0, height);
        Point lastPoint = new(width, height);

        using (var sCtx = strokeGeometry.Open())
        using (var fCtx = fillGeometry.Open())
        {
            bool started = false;

            for (int i = 0; i < pointsCount; i++)
            {
                double x = i * stepX;
                double waveT = (i * 0.44) - phase;

                double norm;
                if (!isActive)
                {
                    norm = 0.02;
                }
                else if (isUpload)
                {
                    // Upload stream: bursts of purple packets in lower half (~0.16 - 0.42)
                    double baseWave = 0.24 + (0.07 * Math.Sin(waveT * 1.6)) + (0.04 * Math.Cos(waveT * 3.3));
                    double packetPulse = Math.Pow(Math.Max(0, Math.Sin((waveT * 0.95) + 1.2)), 6) * 0.14;
                    norm = Math.Clamp(baseWave + packetPulse, 0.10, 0.48);
                }
                else
                {
                    // Download stream: intense bursts of cyan packets in upper half (~0.30 - 0.94)
                    double baseWave = 0.54 + (0.24 * Math.Sin(waveT * 1.3)) + (0.16 * Math.Cos(waveT * 2.9));
                    double packetPulse = Math.Pow(Math.Max(0, Math.Sin(waveT * 0.72)), 5) * 0.26;
                    norm = Math.Clamp(baseWave + packetPulse, 0.20, 0.96);
                }

                double y = height - (norm * (height - 14)) - 7;
                var currentPoint = new Point(x, y);

                if (!started)
                {
                    firstPoint = currentPoint;
                    sCtx.BeginFigure(currentPoint, false);
                    fCtx.BeginFigure(currentPoint, true);
                    started = true;
                }
                else
                {
                    sCtx.LineTo(currentPoint);
                    fCtx.LineTo(currentPoint);
                }

                lastPoint = currentPoint;
            }

            sCtx.EndFigure(false);

            // Close fill polygon down to the bottom
            fCtx.LineTo(new Point(lastPoint.X, height));
            fCtx.LineTo(new Point(firstPoint.X, height));
            fCtx.EndFigure(true);
        }

        // Draw translucent gradient under the packet wave
        var gradientBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            [
                new GradientStop(fillStartColor, 0.0),
                new GradientStop(Color.FromArgb(0, fillStartColor.R, fillStartColor.G, fillStartColor.B), 1.0)
            ]
        };

        context.DrawGeometry(gradientBrush, null, fillGeometry);

        // Draw the crisp packet wave line
        context.DrawGeometry(null, strokePen, strokeGeometry);

        if (isActive)
        {
            // Draw glowing pulsating head dot at the front edge of the packet stream
            double pulseRadius = 4.5 + (1.8 * Math.Sin(pulsePhase));
            var glowBrush = new SolidColorBrush(glowColor);

            context.DrawEllipse(glowBrush, null, lastPoint, pulseRadius, pulseRadius);
            context.DrawEllipse(solidBrush, null, lastPoint, 2.5, 2.5);
        }
    }
}
