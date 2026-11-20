namespace obxodka.Pages.Vpn;

//декодировщик и проигрыватель покадровых анимаций кнопки темы на skia
internal sealed partial class ButtonAnimatedClip : IDisposable
{
    private const int MaxButtonDimension = 280;
    private readonly List<int> _durations = [];
    private readonly List<SKBitmap> _frames = [];
    private int _currentFrameIndex;
    private int _elapsedMs;

    public int FrameCount => _frames.Count;
    public SKBitmap? CurrentBitmap => _frames.Count > 0 ? _frames[_currentFrameIndex] : null;

    //освобождение битмапов покадровой анимации
    public void Dispose()
    {
        foreach (var f in _frames)
        {
            f.Dispose();
        }

        _frames.Clear();
        _durations.Clear();
    }

    //уменьшение размера битмапа для оптимизации памяти
    private static SKBitmap DownscaleIfNeeded(SKBitmap bmp)
    {
        if (bmp is { Width: <= MaxButtonDimension, Height: <= MaxButtonDimension })
        {
            return bmp;
        }

        var scale = Math.Min((float)MaxButtonDimension / bmp.Width, (float)MaxButtonDimension / bmp.Height);
        var targetW = Math.Max(1, (int)Math.Round(bmp.Width * scale));
        var targetH = Math.Max(1, (int)Math.Round(bmp.Height * scale));
        var resizedInfo = new SKImageInfo(targetW, targetH, SKColorType.Rgba8888, SKAlphaType.Premul);
        var resized = bmp.Resize(resizedInfo, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        if (resized is null)
        {
            return bmp;
        }

        bmp.Dispose();
        return resized;
    }

    [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code",
        Justification = "SKCodec.Create can return null for non-codec or corrupt streams")]
    public static ButtonAnimatedClip? Load(string path, int maxFrames = 180, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return null;
            }

            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            if (codec is null)
            {
                var staticBmp = SKBitmap.Decode(path);
                if (staticBmp is null)
                {
                    return null;
                }

                var staticClip = new ButtonAnimatedClip();
                staticClip._frames.Add(DownscaleIfNeeded(staticBmp));
                staticClip._durations.Add(1000);
                return staticClip;
            }

            var count = codec.FrameCount;
            var clip = new ButtonAnimatedClip();
            var frameInfos = codec.FrameInfo;

            var step = count > maxFrames ? (int)Math.Ceiling((double)count / maxFrames) : 1;

            for (var i = 0; i < count; i += step)
            {
                if (ct.IsCancellationRequested)
                {
                    clip.Dispose();
                    return null;
                }

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var opts = new SKCodecOptions(i);
                var res = codec.GetPixels(info, bmp.GetPixels(), opts);
                if (res is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                {
                    bmp.Dispose();
                    continue;
                }

                var dur = frameInfos is not null && frameInfos.Length > i ? frameInfos[i].Duration : 16;
                if (dur <= 0)
                {
                    dur = 16;
                }

                dur *= step;

                clip._frames.Add(DownscaleIfNeeded(bmp));
                clip._durations.Add(dur);
            }

            if (clip._frames.Count != 0)
            {
                return clip;
            }

            clip.Dispose();
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[AnimButton] Не удалось загрузить кадры кнопки для {path}: {ex.Message}");
            return null;
        }
    }

    //продвижение тайминга кадра анимации кнопки
    public void Advance(int stepMs)
    {
        if (_frames.Count <= 1)
        {
            return;
        }

        _elapsedMs += stepMs;
        while (_durations.Count > _currentFrameIndex && _elapsedMs >= _durations[_currentFrameIndex])
        {
            _elapsedMs -= _durations[_currentFrameIndex];
            _currentFrameIndex = (_currentFrameIndex + 1) % _frames.Count;
        }
    }
}
