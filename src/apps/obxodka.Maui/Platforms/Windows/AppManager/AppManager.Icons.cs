using System.Security.Cryptography;
using System.Text;
using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//извлечение и кэширование иконок исполняемых файлов через win32 gdi и skiasharp
public sealed partial class AppManager
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr ShGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref byte psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", EntryPoint = "GetDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetDc(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "ReleaseDC", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseDc(IntPtr hWnd, IntPtr hDc);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetObjectW(IntPtr hObject, int nCount, out BitmapInfo lpObject);

    [LibraryImport("gdi32.dll", EntryPoint = "GetDIBits", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDiBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        IntPtr lpvBits,
        ref BitmapInfoHeader lpbmi,
        uint uUsage);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    //генерация уникального пути к кэшированной иконке по sha256 пути exe
    private static string GetCachedIconPath(string exePath, string iconsDir)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant())))[..16];
        return Path.Combine(iconsDir, $"win_{hash}.png");
    }

    //получение пути к существующей иконке или извлечение новой
    private static string? GetOrExtractIcon(string exePath, string iconsDir)
    {
        var iconPath = GetCachedIconPath(exePath, iconsDir);
        return File.Exists(iconPath) || TryExtractIconPng(exePath, iconPath) ? iconPath : null;
    }

    //извлечение hicon через shell32, чтение растра через gdi и сохранение в png через skiasharp
    private static bool TryExtractIconPng(string exePath, string outputPngPath)
    {
        var shfiSize = (uint)(IntPtr.Size + 8 + 520 + 160);
        Span<byte> shfiBytes = stackalloc byte[704];
        shfiBytes.Clear();

        if (ShGetFileInfoW(exePath, 0, ref MemoryMarshal.GetReference(shfiBytes), shfiSize, ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero)
        {
            return false;
        }

        var hIcon = MemoryMarshal.Read<IntPtr>(shfiBytes);
        if (hIcon == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!GetIconInfo(hIcon, out var iconInfo))
            {
                return false;
            }

            try
            {
                if (iconInfo.HbmColor == IntPtr.Zero)
                {
                    return false;
                }

                if (GetObjectW(iconInfo.HbmColor, Marshal.SizeOf<BitmapInfo>(), out var bmp) == 0 ||
                    bmp.BmWidth <= 0 || bmp.BmHeight <= 0)
                {
                    return false;
                }

                var width = bmp.BmWidth;
                var height = bmp.BmHeight;

                var bmi = new BitmapInfoHeader
                {
                    BiSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    BiWidth = width,
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                };

                var hdc = GetDc(IntPtr.Zero);
                if (hdc == IntPtr.Zero)
                {
                    return false;
                }

                try
                {
                    using var skBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                    var pixelsPtr = skBitmap.GetPixels();
                    if (pixelsPtr == IntPtr.Zero)
                    {
                        return false;
                    }

                    var lines = GetDiBits(hdc, iconInfo.HbmColor, 0, (uint)height, pixelsPtr, ref bmi, 0);
                    if (lines <= 0)
                    {
                        return false;
                    }

                    using var image = SKImage.FromBitmap(skBitmap);
                    using var data = image.Encode(SKEncodedImageFormat.Png, 90);
                    if (data is null)
                    {
                        return false;
                    }

                    using var fs = File.OpenWrite(outputPngPath);
                    data.SaveTo(fs);
                    return true;
                }
                finally
                {
                    _ = ReleaseDc(IntPtr.Zero, hdc);
                }
            }
            finally
            {
                if (iconInfo.HbmColor != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.HbmColor);
                }

                if (iconInfo.HbmMask != IntPtr.Zero)
                {
                    _ = DeleteObject(iconInfo.HbmMask);
                }
            }
        }
        finally
        {
            _ = DestroyIcon(hIcon);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IconInfo
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public int BmType;
        public int BmWidth;
        public int BmHeight;
        public int BmWidthBytes;
        public ushort BmPlanes;
        public ushort BmBitsPixel;
        public IntPtr BmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint BiSize;
        public int BiWidth;
        public int BiHeight;
        public ushort BiPlanes;
        public ushort BiBitCount;
        public uint BiCompression;
        public uint BiSizeImage;
        public int BiXPelsPerMeter;
        public int BiYPelsPerMeter;
        public uint BiClrUsed;
        public uint BiClrImportant;
    }
}
