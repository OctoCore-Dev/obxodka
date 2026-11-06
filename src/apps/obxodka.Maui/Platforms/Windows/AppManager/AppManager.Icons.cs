using System.Security.Cryptography;
using System.Text;

namespace obxodka.Platforms.Windows;

//извлечение и кэширование иконок исполняемых файлов через win32 gdi и skiasharp
public sealed partial class AppManager
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public int FIcon;
        public uint XHotspot;
        public uint YHotspot;
        public IntPtr HbmMask;
        public IntPtr HbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
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
    private struct BITMAPINFOHEADER
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

    [LibraryImport("shell32.dll", EntryPoint = "SHGetFileInfoW", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SHGetFileInfoW(
        string pszPath,
        uint dwFileAttributes,
        ref byte psfi,
        uint cbFileInfo,
        uint uFlags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetObjectW(IntPtr hObject, int nCount, out BITMAP lpObject);

    [LibraryImport("gdi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDIBits(
        IntPtr hdc,
        IntPtr hbmp,
        uint uStartScan,
        uint cScanLines,
        IntPtr lpvBits,
        ref BITMAPINFOHEADER lpbmi,
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

        if (SHGetFileInfoW(exePath, 0, ref MemoryMarshal.GetReference(shfiBytes), shfiSize, ShgfiIcon | ShgfiLargeIcon) == IntPtr.Zero)
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

                if (GetObjectW(iconInfo.HbmColor, Marshal.SizeOf<BITMAP>(), out var bmp) == 0 ||
                    bmp.BmWidth <= 0 || bmp.BmHeight <= 0)
                {
                    return false;
                }

                var width = bmp.BmWidth;
                var height = bmp.BmHeight;

                var bmi = new BITMAPINFOHEADER
                {
                    BiSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    BiWidth = width,
                    BiHeight = -height,
                    BiPlanes = 1,
                    BiBitCount = 32,
                    BiCompression = 0
                };

                var hdc = GetDC(IntPtr.Zero);
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

                    var lines = GetDIBits(hdc, iconInfo.HbmColor, 0, (uint)height, pixelsPtr, ref bmi, 0);
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
                    _ = ReleaseDC(IntPtr.Zero, hdc);
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
}
