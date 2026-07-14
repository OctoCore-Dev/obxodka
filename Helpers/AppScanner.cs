#if ANDROID
using Android.Graphics;
using Android.Graphics.Drawables;
#endif

namespace obxodka.Helpers;

public static class AppScanner
{
    public static List<AppInfoItem> GetInstalledApps()
    {
        var apps = new List<AppInfoItem>();
        var savedExcluded = Preferences.Get("ExcludedApps", "");
        var excludedSet = new HashSet<string>(savedExcluded.Split([','], StringSplitOptions.RemoveEmptyEntries));

#if ANDROID
        ScanAndroidApps(apps, excludedSet);
#endif

        return [.. apps.OrderBy(a => a.Name)];
    }

    public static void SaveExcludedApps(IEnumerable<AppInfoItem> apps)
    {
        var excluded = apps.Where(a => a.IsBypassed).Select(a => a.PackageName);
        Preferences.Set("ExcludedApps", string.Join(",", excluded));
    }

#if ANDROID
    private static void ScanAndroidApps(List<AppInfoItem> apps, HashSet<string> excludedSet)
    {
        var context = Platform.AppContext;
        var pm = context.PackageManager;
        if (pm == null)
        {
            return;
        }

        var mainIntent = new Android.Content.Intent(Android.Content.Intent.ActionMain, null);
        _ = mainIntent.AddCategory(Android.Content.Intent.CategoryLauncher);

        var resolvedInfos = pm.QueryIntentActivities(mainIntent, 0);
        foreach (var info in resolvedInfos)
        {
            var packageName = info.ActivityInfo?.PackageName;
            if (string.IsNullOrEmpty(packageName) || packageName == context.PackageName)
            {
                continue;
            }

            using var icon = info.LoadIcon(pm);
            var iconBytes = icon != null ? GetIconBytes(icon) : [];

            apps.Add(new AppInfoItem
            {
                Name = info.LoadLabel(pm)?.ToString() ?? packageName,
                PackageName = packageName,
                IsBypassed = excludedSet.Contains(packageName),
                Icon = iconBytes.Length > 0
                    ? ImageSource.FromStream(() => new MemoryStream(iconBytes))
                    : "default_icon.png"
            });
        }
    }

    private static byte[] GetIconBytes(Drawable drawable)
    {
        Bitmap bitmap;
        const int targetDim = 64;

        if (drawable is BitmapDrawable bd && bd.Bitmap != null)
        {
            var origBitmap = bd.Bitmap;
            bitmap = origBitmap.Width > targetDim || origBitmap.Height > targetDim
                ? Bitmap.CreateScaledBitmap(origBitmap, targetDim, targetDim, true)
                : origBitmap;
        }
        else
        {
            bitmap = Bitmap.CreateBitmap(targetDim, targetDim, Bitmap.Config.Argb8888!);
            using var canvas = new Canvas(bitmap);
            drawable.SetBounds(0, 0, targetDim, targetDim);
            drawable.Draw(canvas);
        }

        using var stream = new MemoryStream();
        _ = bitmap.Compress(Bitmap.CompressFormat.Png!, 100, stream);

        if (bitmap != (drawable as BitmapDrawable)?.Bitmap)
        {
            bitmap.Recycle();
        }

        return stream.ToArray();
    }
#endif
}
