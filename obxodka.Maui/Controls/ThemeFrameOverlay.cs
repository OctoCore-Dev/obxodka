namespace obxodka.Maui.Controls;

public sealed partial class ThemeFrameOverlay : SKCanvasView, IDisposable
{
    private readonly Lock _lock = new();
    private readonly SKPaint _paint = new()
    {
        IsAntialias = true
    };
    private static readonly SKSamplingOptions t_samplingOptions = new(SKFilterMode.Linear);

    private string? _imagePath;
    private SKImage? _skImage;
    private FrameSlice? _slice;
    private bool _isDisposed;

    public ThemeFrameOverlay()
    {
        InputTransparent = true;
        IsVisible = false;
        ZIndex = 20;
    }

    public void SetFrame(string? imagePath, FrameSlice? slice)
    {
        lock (_lock)
        {
            if (string.Equals(_imagePath, imagePath, StringComparison.OrdinalIgnoreCase) &&
                Equals(_slice, slice))
            {
                return;
            }

            _skImage?.Dispose();
            _skImage = null;
            _imagePath = imagePath;
            _slice = slice;

            if (!string.IsNullOrEmpty(imagePath) && File.Exists(imagePath))
            {
                try
                {
                    _skImage = SKImage.FromEncodedData(imagePath);
                }
                catch
                {
                    _skImage = null;
                }
            }

            var hasImage = _skImage != null;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsVisible = hasImage;
                InvalidateSurface();
            });
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _skImage?.Dispose();
            _skImage = null;
            _imagePath = null;
            _slice = null;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsVisible = false;
                InvalidateSurface();
            });
        }
    }

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);

        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        SKImage? img;
        FrameSlice? slice;

        lock (_lock)
        {
            img = _skImage;
            slice = _slice;
        }

        if (img == null)
        {
            return;
        }

        var info = e.Info;
        var dst = new SKRect(0, 0, info.Width, info.Height);

        if (slice != null && slice.IsValid)
        {
            var left = Math.Clamp(slice.Left, 0, img.Width / 2);
            var top = Math.Clamp(slice.Top, 0, img.Height / 2);
            var right = Math.Clamp(slice.Right, 0, img.Width / 2);
            var bottom = Math.Clamp(slice.Bottom, 0, img.Height / 2);

            var center = new SKRectI(left, top, Math.Max(left + 1, img.Width - right), Math.Max(top + 1, img.Height - bottom));
            canvas.DrawImageNinePatch(img, center, dst, _paint);
        }
        else if (DeviceInfo.Idiom == DeviceIdiom.Phone && img.Width > img.Height * 1.15f)
        {
            var sliceX = Math.Max(1, (int)(img.Width * 0.12f));
            var sliceY = Math.Max(1, (int)(img.Height * 0.12f));
            var center = new SKRectI(sliceX, sliceY, img.Width - sliceX, img.Height - sliceY);

            canvas.DrawImageNinePatch(img, center, dst, _paint);
        }
        else
        {
            canvas.DrawImage(img, dst, t_samplingOptions, _paint);
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        lock (_lock)
        {
            _skImage?.Dispose();
            _skImage = null;
        }

        _paint.Dispose();
    }
}
