using Android.Content;
using Android.Content.PM;
using Application = Android.App.Application;

namespace obxodka.Platforms.Android;

//управление списком установленных приложений и исключениями из vpn туннеля (split tunneling)
[SupportedOSPlatform("android29.0")]
public sealed class AppManager : IAppManager
{
    private const string BypassedAppsKey = "obxodka_bypassed_apps";
    private static List<AppInfoItem>? s_cachedApps;

    //получение списка установленных приложений с иконками и статусом исключения
    public Task<List<AppInfoItem>> GetInstalledAppsAsync() =>
        Task.Run(() =>
        {
            var apps = new List<AppInfoItem>();
            var context = Application.Context;
            var pm = context.PackageManager;
            if (pm is null)
            {
                return apps;
            }

            var bypassed = GetBypassedPackages().ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (s_cachedApps is not null && s_cachedApps.Count > 0)
            {
                foreach (var app in s_cachedApps)
                {
                    app.IsBypassed = bypassed.Contains(app.PackageName);
                }

                return [.. s_cachedApps];
            }

            var cacheDir = FileSystem.Current.CacheDirectory;
            var iconsDir = Path.Combine(cacheDir, "AppIcons");
            _ = Directory.CreateDirectory(iconsDir);

            var mainIntent = new Intent(Intent.ActionMain, null);
            _ = mainIntent.AddCategory(Intent.CategoryLauncher);

            var resolveInfos = pm.QueryIntentActivities(mainIntent, 0);
            var seenPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pendingIcons = new List<(ApplicationInfo appInfo, string pkgName, string iconPath)>();

            if (resolveInfos is not null)
            {
                foreach (var ri in resolveInfos)
                {
                    if (ri.ActivityInfo?.ApplicationInfo is not { } appInfo)
                    {
                        continue;
                    }

                    var pkgName = appInfo.PackageName ?? string.Empty;
                    if (string.IsNullOrEmpty(pkgName) ||
                        pkgName.Equals(context.PackageName, StringComparison.OrdinalIgnoreCase) ||
                        pkgName.Contains("obxodka", StringComparison.OrdinalIgnoreCase) ||
                        !seenPackages.Add(pkgName))
                    {
                        continue;
                    }

                    var appName = ri.LoadLabel(pm)?.ToString() ?? appInfo.LoadLabel(pm)?.ToString() ?? pkgName;
                    if (appName.Contains("obxodka", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var iconPath = Path.Combine(iconsDir, $"{pkgName}.png");
                    var hasIcon = File.Exists(iconPath);

                    if (!hasIcon)
                    {
                        pendingIcons.Add((appInfo, pkgName, iconPath));
                    }

                    apps.Add(new AppInfoItem
                    {
                        Name = appName,
                        PackageName = pkgName,
                        IsBypassed = bypassed.Contains(pkgName),
                        IconPath = hasIcon ? iconPath : null
                    });
                }
            }

            foreach (var bypassedPkg in bypassed)
            {
                if (bypassedPkg.Contains("obxodka", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (seenPackages.Add(bypassedPkg))
                {
                    try
                    {
                        var appInfo = pm.GetApplicationInfo(bypassedPkg, 0);
                        var appName = appInfo.LoadLabel(pm)?.ToString() ?? bypassedPkg;
                        if (appName.Contains("obxodka", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var iconPath = Path.Combine(iconsDir, $"{bypassedPkg}.png");
                        var hasIcon = File.Exists(iconPath);

                        if (!hasIcon)
                        {
                            pendingIcons.Add((appInfo, bypassedPkg, iconPath));
                        }

                        apps.Add(new AppInfoItem
                        {
                            Name = appName,
                            PackageName = bypassedPkg,
                            IsBypassed = true,
                            IconPath = hasIcon ? iconPath : null
                        });
                    }
                    catch { }
                }
            }

            s_cachedApps = [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];

            if (pendingIcons.Count > 0)
            {
                var appsByPkg = apps.ToDictionary(a => a.PackageName, a => a, StringComparer.OrdinalIgnoreCase);
                var orderedPending = pendingIcons
                    .OrderByDescending(p => bypassed.Contains(p.pkgName))
                    .ToArray();

                _ = Task.Run(async () =>
                {
                    await Task.Delay(180).ConfigureAwait(false);
                    var batch = new List<(AppInfoItem Target, string Path)>(35);
                    foreach (var (appInfo, pkgName, iconPath) in orderedPending)
                    {
                        try
                        {
                            if (File.Exists(iconPath))
                            {
                                if (appsByPkg.TryGetValue(pkgName, out var existingTarget))
                                {
                                    batch.Add((existingTarget, iconPath));
                                }
                            }
                            else if (appInfo.LoadIcon(pm) is { } drawable)
                            {
                                var width = Math.Clamp(drawable.IntrinsicWidth > 0 ? drawable.IntrinsicWidth : 48, 32, 56);
                                var height = Math.Clamp(drawable.IntrinsicHeight > 0 ? drawable.IntrinsicHeight : 48, 32, 56);
                                using var bmp = global::Android.Graphics.Bitmap.CreateBitmap(
                                    width,
                                    height,
                                    global::Android.Graphics.Bitmap.Config.Argb8888!);

                                if (bmp is not null)
                                {
                                    var canvas = new global::Android.Graphics.Canvas(bmp);
                                    drawable.SetBounds(0, 0, canvas.Width, canvas.Height);
                                    drawable.Draw(canvas);

                                    using var fs = new FileStream(iconPath, FileMode.Create, FileAccess.Write);
                                    _ = await bmp.CompressAsync(global::Android.Graphics.Bitmap.CompressFormat.Png!, 80, fs).ConfigureAwait(false);
                                    bmp.Recycle();

                                    if (appsByPkg.TryGetValue(pkgName, out var targetApp))
                                    {
                                        batch.Add((targetApp, iconPath));
                                    }
                                }
                            }

                            if (batch.Count >= 35)
                            {
                                var toApply = batch.ToArray();
                                batch.Clear();
                                MainThread.BeginInvokeOnMainThread(() =>
                                {
                                    foreach (var (target, path) in toApply)
                                    {
                                        target.IconPath = path;
                                    }
                                });
                                await Task.Delay(45).ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }

                    if (batch.Count > 0)
                    {
                        var toApply = batch.ToArray();
                        batch.Clear();
                        MainThread.BeginInvokeOnMainThread(() =>
                        {
                            foreach (var (target, path) in toApply)
                            {
                                target.IconPath = path;
                            }
                        });
                    }
                });
            }

            return [.. s_cachedApps];
        });

    private static readonly HashSet<string> s_defaultCloakedPackages = new(
    [
        "ru.sberbankmobile",
        "com.idamob.tinkoff.android",
        "ru.vtb24.mobilebanking",
        "ru.alfabank.mobile.android",
        "ru.raiffeisennews",
        "ru.gosuslugi.online",
        "ru.gosuslugi.pos",
        "ru.yandex.yandexnavi",
        "ru.yandex.taxi",
        "ru.yandex.searchplugin",
        "ru.yandex.market",
        "com.vkontakte.android",
        "com.vk.im",
        "ru.mail.mailapp",
        "com.wildberries.work",
        "com.wildberries.wbdeti",
        "ru.ozon.app.android",
        "com.avito.android",
        "ru.nspk.mirpay",
        "ru.samokat.app",
        "ru.magnit.app",
        "ru.x5.app"
    ], StringComparer.OrdinalIgnoreCase);

    //получение списка пакетов приложений исключенных из маршрутизации vpn
    public List<string> GetBypassedPackages()
    {
        if (Preferences.ContainsKey(BypassedAppsKey))
        {
            var saved = Preferences.Get(BypassedAppsKey, string.Empty);
            return string.IsNullOrWhiteSpace(saved)
                ? []
                : [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(p => !p.Contains("obxodka", StringComparison.OrdinalIgnoreCase))];
        }

        return [.. s_defaultCloakedPackages];
    }

    //сохранение обновленного списка пакетов приложений исключенных из vpn
    public void SaveBypassedPackages(List<string> packages) =>
        Preferences.Set(BypassedAppsKey, string.Join(",", packages.Where(p => !p.Contains("obxodka", StringComparison.OrdinalIgnoreCase))));
}
