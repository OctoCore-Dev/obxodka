namespace obxodka.Maui.Controls;

public sealed partial class ThemeFrameOverlay : SKCanvasView, IDisposable
{
    private static readonly SKSamplingOptions t_samplingOptions = new(SKFilterMode.Linear);
    private readonly Lock _lock = new();
    private readonly SKPaint _paint = new() { IsAntialias = true };

    private string? _imagePath;
    private SKImage? _skImage;
    private FrameSlice? _slice;
    private bool _isDisposed;

    private bool _useNinePatch;
    private SKRectI _cachedCenter;
    private bool _hasImage;

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
            if (string.Equals(_imagePath, imagePath, StringComparison.OrdinalIgnoreCase) && Equals(_slice, slice))
            {
                return;
            }

            _skImage?.Dispose();
            _skImage = null;
            _imagePath = imagePath;
            _slice = slice;
            _useNinePatch = false;
            _cachedCenter = default;

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

            if (_skImage is not null)
            {
                if (slice is { IsValid: true })
                {
                    var left = Math.Clamp(slice.Left, 0, _skImage.Width / 2);
                    var top = Math.Clamp(slice.Top, 0, _skImage.Height / 2);
                    var right = Math.Clamp(slice.Right, 0, _skImage.Width / 2);
                    var bottom = Math.Clamp(slice.Bottom, 0, _skImage.Height / 2);

                    _cachedCenter = new SKRectI(left, top, Math.Max(left + 1, _skImage.Width - right), Math.Max(top + 1, _skImage.Height - bottom));
                    _useNinePatch = true;
                }
                else if (DeviceInfo.Idiom == DeviceIdiom.Phone && _skImage.Width > _skImage.Height * 1.15f)
                {
                    var sliceX = Math.Max(1, (int)(_skImage.Width * 0.12f));
                    var sliceY = Math.Max(1, (int)(_skImage.Height * 0.12f));
                    _cachedCenter = new SKRectI(sliceX, sliceY, _skImage.Width - sliceX, _skImage.Height - sliceY);
                    _useNinePatch = true;
                }
            }

            _hasImage = _skImage is not null;

            MainThread.BeginInvokeOnMainThread(() =>
            {
                IsVisible = _hasImage;
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
            _useNinePatch = false;
            _cachedCenter = default;
            _hasImage = false;

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
        bool useNine;
        SKRectI center;

        lock (_lock)
        {
            img = _skImage;
            useNine = _useNinePatch;
            center = _cachedCenter;
        }

        if (img is null)
        {
            return;
        }

        var info = e.Info;
        var dst = new SKRect(0, 0, info.Width, info.Height);

        if (useNine)
        {
            canvas.DrawImageNinePatch(img, center, dst, _paint);
            return;
        }

        canvas.DrawImage(img, dst, t_samplingOptions, _paint);
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
        GC.SuppressFinalize(this);
    }
}
