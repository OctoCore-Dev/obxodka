using Android.Content.PM;
using Application = Android.App.Application;

namespace obxodka.Platforms.Android;

public class AppManager : IAppManager
{
    private const string BypassedAppsKey = "obxodka_bypassed_apps";
    private static List<AppInfoItem>? t_cachedApps;
    private static int t_lastPackageCount;

    public Task<List<AppInfoItem>> GetInstalledAppsAsync()
    {
        return Task.Run(() =>
        {
            var apps = new List<AppInfoItem>();
            var pm = Application.Context.PackageManager;
            if (pm == null)
            {
                return apps;
            }

#pragma warning disable CA1416
            var flags = PackageInfoFlags.MatchUninstalledPackages;
            var packages = pm.GetInstalledPackages(flags);
#pragma warning restore CA1416
            if (packages == null)
            {
                return apps;
            }

            var bypassed = GetBypassedPackages().ToHashSet();

            if (t_cachedApps != null && packages.Count == t_lastPackageCount)
            {
                foreach (var app in t_cachedApps)
                {
                    app.IsBypassed = bypassed.Contains(app.PackageName);
                }
                return [.. t_cachedApps];
            }

            foreach (var packageInfo in packages)
            {
                if ((packageInfo.ApplicationInfo!.Flags & ApplicationInfoFlags.System) != 0)
                {
                    var launchIntent = pm.GetLaunchIntentForPackage(packageInfo.PackageName!);
                    if (launchIntent == null)
                    {
                        continue;
                    }
                }

                if (packageInfo.PackageName == Application.Context.PackageName)
                {
                    continue;
                }

                var appName = packageInfo.ApplicationInfo.LoadLabel(pm)?.ToString() ?? packageInfo.PackageName!;

                string? iconPath = null;
                try
                {
                    var cacheDir = FileSystem.Current.CacheDirectory;
                    var iconsDir = Path.Combine(cacheDir, "AppIcons");
                    _ = Directory.CreateDirectory(iconsDir);
                    iconPath = Path.Combine(iconsDir, $"{packageInfo.PackageName}.png");

                    if (!File.Exists(iconPath))
                    {
                        var drawable = packageInfo.ApplicationInfo.LoadIcon(pm);
                        if (drawable != null)
                        {
                            var bmp = global::Android.Graphics.Bitmap.CreateBitmap(
                                drawable.IntrinsicWidth > 0 ? drawable.IntrinsicWidth : 48,
                                drawable.IntrinsicHeight > 0 ? drawable.IntrinsicHeight : 48,
                                global::Android.Graphics.Bitmap.Config.Argb8888!);
                            var canvas = new global::Android.Graphics.Canvas(bmp);
                            drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
                            drawable.Draw(canvas);
                            using var fs = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
                            _ = bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 100, fs);
                            bmp.Recycle();
                            bmp.Dispose();
                        }
                        else
                        {
                            iconPath = null;
                        }
                    }
                }
                catch { iconPath = null; }

                apps.Add(new AppInfoItem
                {
                    Name = appName,
                    PackageName = packageInfo.PackageName!,
                    IsBypassed = bypassed.Contains(packageInfo.PackageName!),
                    IconPath = iconPath
                });
            }

            t_cachedApps = [.. apps.OrderBy(a => a.Name)];
            t_lastPackageCount = packages.Count;
            return [.. t_cachedApps];
        });
    }

    public List<string> GetBypassedPackages()
    {
        var saved = Preferences.Get(BypassedAppsKey, string.Empty);
        return string.IsNullOrWhiteSpace(saved) ? [] : [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries)];
    }

    public void SaveBypassedPackages(List<string> packages) => Preferences.Set(BypassedAppsKey, string.Join(",", packages));
}
