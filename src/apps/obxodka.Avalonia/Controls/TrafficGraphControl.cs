namespace obxodka.Avalonia.Controls;

//живой векторный график скорости скачивания и отдачи без слепых таймеров
public sealed class TrafficGraphControl : Control
{
    private const int Capacity = 40;
    private readonly long[] _download = new long[Capacity];
    private readonly long[] _upload = new long[Capacity];
    private int _count;

    private static readonly IPen s_gridPen = new Pen(new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1);
    private static readonly IPen s_dlPen = new Pen(new SolidColorBrush(Color.Parse("#60CDFF")), 2);
    private static readonly IPen s_ulPen = new Pen(new SolidColorBrush(Color.Parse("#A78BFA")), 1.8);
    private static readonly IBrush s_dlFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(80, 96, 205, 255), 0),
            new GradientStop(Color.FromArgb(4, 96, 205, 255), 1)
        ]
    };
    private static readonly IBrush s_ulFill = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        GradientStops =
        [
            new GradientStop(Color.FromArgb(56, 167, 139, 250), 0),
            new GradientStop(Color.FromArgb(4, 167, 139, 250), 1)
        ]
    };

    //добавление нового замера скорости и перерисовка по событию ядра
    public void PushSample(long downloadBps, long uploadBps)
    {
        if (_count < Capacity)
        {
            _download[_count] = Math.Max(0, downloadBps);
            _upload[_count] = Math.Max(0, uploadBps);
            _count++;
        }
        else
        {
            Array.Copy(_download, 1, _download, 0, Capacity - 1);
            Array.Copy(_upload, 1, _upload, 0, Capacity - 1);
            _download[Capacity - 1] = Math.Max(0, downloadBps);
            _upload[Capacity - 1] = Math.Max(0, uploadBps);
        }

        InvalidateVisual();
    }

    public void Clear()
    {
        Array.Clear(_download);
        Array.Clear(_upload);
        _count = 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 4 || h <= 4)
        {
            return;
        }

        //горизонтальная сетка
        for (var i = 1; i <= 3; i++)
        {
            var y = h * i / 4.0;
            context.DrawLine(s_gridPen, new Point(0, y), new Point(w, y));
        }

        if (_count < 2)
        {
            return;
        }

        long peak = 64 * 1024;
        for (var i = 0; i < _count; i++)
        {
            if (_download[i] > peak)
            {
                peak = _download[i];
            }

            if (_upload[i] > peak)
            {
                peak = _upload[i];
            }
        }

        DrawSeries(context, _upload, _count, peak, w, h, s_ulPen, s_ulFill);
        DrawSeries(context, _download, _count, peak, w, h, s_dlPen, s_dlFill);
    }

    private static void DrawSeries(DrawingContext ctx, long[] data, int count, long peak, double w, double h, IPen pen, IBrush fill)
    {
        var stepX = w / (Capacity - 1);
        var startIdx = Capacity - count;
        var padBottom = h - 4;
        var usableH = h - 10;

        var strokeGeo = new StreamGeometry();
        var fillGeo = new StreamGeometry();

        using (var sCtx = strokeGeo.Open())
        using (var fCtx = fillGeo.Open())
        {
            var firstX = startIdx * stepX;
            var firstY = padBottom - ((double)data[0] / peak * usableH);
            sCtx.BeginFigure(new Point(firstX, firstY), false);
            fCtx.BeginFigure(new Point(firstX, h), true);
            fCtx.LineTo(new Point(firstX, firstY));

            for (var i = 1; i < count; i++)
            {
                var x = (startIdx + i) * stepX;
                var y = padBottom - ((double)data[i] / peak * usableH);
                sCtx.LineTo(new Point(x, y));
                fCtx.LineTo(new Point(x, y));
            }

            var lastX = (startIdx + count - 1) * stepX;
            fCtx.LineTo(new Point(lastX, h));
            fCtx.EndFigure(true);
            sCtx.EndFigure(false);
        }

        ctx.DrawGeometry(fill, null, fillGeo);
        ctx.DrawGeometry(null, pen, strokeGeo);
    }
}
