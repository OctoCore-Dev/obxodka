namespace obxodka.Core;
public class AppInfoItem
{
    public string Name { get; set; } = string.Empty;
    public string PackageName { get; set; } = string.Empty;
    public bool IsBypassed { get; set; }
    public ImageSource? Icon { get; set; }
}
public static class AppScanner
{
    public static List<AppInfoItem> GetInstalledApps()
    {
        var apps = new List<AppInfoItem>();
        string savedExcluded = Preferences.Get("ExcludedApps", "");
        var excludedSet = new HashSet<string>(savedExcluded.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries));
#if ANDROID
        try
        {
            var context = Android.App.Application.Context;
            var pm = context.PackageManager;
            if (pm != null)
            {
                var packages = pm.GetInstalledApplications(Android.Content.PM.PackageInfoFlags.MatchAll);
                if (packages != null)
                {
                    foreach (var p in packages)
                    {
                        var launchIntent = pm.GetLaunchIntentForPackage(p.PackageName ?? "");
                        if (launchIntent != null && p.PackageName != context.PackageName)
                        {
                            var iconDrawable = p.LoadIcon(pm);
                            if (iconDrawable != null)
                            {
                                byte[] iconBytes = GetIconBytes(iconDrawable);
                                apps.Add(new AppInfoItem
                                {
                                    Name = p.LoadLabel(pm)?.ToString() ?? p.PackageName ?? "Unknown",
                                    PackageName = p.PackageName ?? "",
                                    IsBypassed = excludedSet.Contains(p.PackageName ?? ""),
                                    Icon = ImageSource.FromStream(() => new MemoryStream(iconBytes))
                                });
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ANDROID SCAN] {ex.Message}");
        }
#elif WINDOWS
        try
        {
            var packageManager = new Windows.Management.Deployment.PackageManager();
            var packages = packageManager.FindPackagesForUser("");
            foreach (var package in packages)
            {
                try
                {
                    if (package.IsFramework || package.IsResourcePackage) continue;
                    var fullName = package.Id.FullName;
                    apps.Add(new AppInfoItem
                    {
                        Name = package.DisplayName,
                        PackageName = fullName,
                        IsBypassed = excludedSet.Contains(fullName),
                        Icon = "splash_win.png"
                    });
                }
                catch { }
            }
            foreach (var path in excludedSet)
            {
                if (System.IO.File.Exists(path) && !apps.Any(a => a.PackageName == path))
                {
                    apps.Add(new AppInfoItem
                    {
                        Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        PackageName = path,
                        IsBypassed = true,
                        Icon = ImageSource.FromFile(path)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WIN SCAN] {ex.Message}");
        }
#endif
        return apps.OrderBy(a => a.Name).ToList();
    }
    public static void SaveExcludedApps(IEnumerable<AppInfoItem> apps)
    {
        var excluded = apps.Where(a => a.IsBypassed).Select(a => a.PackageName);
        string toSave = string.Join(",", excluded);
        Preferences.Set("ExcludedApps", toSave);
    }
#if WINDOWS
    public static async Task<AppInfoItem?> PickWindowsAppAsync()
    {
        return await Microsoft.Maui.Controls.Application.Current.Dispatcher.DispatchAsync(async () =>
        {
            try
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                var mauiWindow = Microsoft.Maui.Controls.Application.Current?.Windows.FirstOrDefault();
                var nativeWindow = mauiWindow?.Handler?.PlatformView as Microsoft.UI.Xaml.Window;
                if (nativeWindow == null) return null;
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(nativeWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                picker.ViewMode = Windows.Storage.Pickers.PickerViewMode.Thumbnail;
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.ComputerFolder;
                picker.FileTypeFilter.Add(".exe");
                var file = await picker.PickSingleFileAsync();
                if (file != null)
                {
                    return new AppInfoItem
                    {
                        Name = file.DisplayName,
                        PackageName = file.Path,
                        IsBypassed = true,
                        Icon = "splash_win.png"
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[CRITICAL FILE PICKER ERROR] {ex.Message}");
            }
            return null;
        });
    }
#endif
#if ANDROID
    private static byte[] GetIconBytes(Drawable drawable)
    {
        Bitmap? bitmap = null;
        if (drawable is BitmapDrawable bd && bd.Bitmap != null)
        {
            bitmap = bd.Bitmap;
        }
        else
        {
            int width = drawable.IntrinsicWidth <= 0 ? 1 : drawable.IntrinsicWidth;
            int height = drawable.IntrinsicHeight <= 0 ? 1 : drawable.IntrinsicHeight;
            bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
            using var canvas = new Canvas(bitmap);
            drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
            drawable.Draw(canvas);
        }
        using var stream = new MemoryStream();
        bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);
        return stream.ToArray();
    }
#endif
}