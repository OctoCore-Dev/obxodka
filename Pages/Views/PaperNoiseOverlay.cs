namespace obxodka.Views;

public sealed partial class PaperNoiseOverlay : Image
{
    private static byte[]? t_noisePngBytes;
    private static readonly Lock t_syncLock = new();

    public PaperNoiseOverlay()
    {
        InputTransparent = true;
        Aspect = Aspect.AspectFill;
        Opacity = 0.05;

        Source = ImageSource.FromStream(() => new MemoryStream(GetOrCreateNoisePng()));
    }

    private static byte[] GetOrCreateNoisePng()
    {
        if (t_noisePngBytes != null)
        {
            return t_noisePngBytes;
        }

        lock (t_syncLock)
        {
            if (t_noisePngBytes != null)
            {
                return t_noisePngBytes;
            }

            const int width = 1920;
            const int height = 1080;
            using var bmp = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            var random = new Random(42);
            var pixels = new uint[width * height];

            for (var i = 0; i < pixels.Length; i++)
            {
                var u1 = 1.0 - random.NextDouble();
                var u2 = 1.0 - random.NextDouble();
                var randStdNormal = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);

                var value = (int)(128 + (randStdNormal * 32));
                var clamped = (byte)Math.Clamp(value, 0, 255);

                var alpha = (byte)random.Next(90, 200);

                pixels[i] = (uint)((alpha << 24) | (clamped << 16) | (clamped << 8) | clamped);
            }

            unsafe
            {
                fixed (uint* ptr = pixels)
                {
                    bmp.SetPixels((IntPtr)ptr);
                }
            }

            using var image = SKImage.FromBitmap(bmp);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            t_noisePngBytes = data.ToArray();
            return t_noisePngBytes;
        }
    }
}


