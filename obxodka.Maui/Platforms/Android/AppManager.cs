using Android.Content;
using Android.Content.PM;
using Application = Android.App.Application;

namespace obxodka.Platforms.Android;

[SupportedOSPlatform("android29.0")]
public sealed class AppManager : IAppManager
{
    private const string BypassedAppsKey = "obxodka_bypassed_apps";
    private static List<AppInfoItem>? t_cachedApps;

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

            var bypassed = GetBypassedPackages().ToHashSet();

            if (t_cachedApps is not null && t_cachedApps.Count > 0)
            {
                foreach (var app in t_cachedApps)
                {
                    app.IsBypassed = bypassed.Contains(app.PackageName);
                }

                return [.. t_cachedApps];
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
                    if (string.IsNullOrEmpty(pkgName) || pkgName == context.PackageName || !seenPackages.Add(pkgName))
                    {
                        continue;
                    }

                    var appName = ri.LoadLabel(pm)?.ToString() ?? appInfo.LoadLabel(pm)?.ToString() ?? pkgName;
                    var iconPath = Path.Combine(iconsDir, $"{pkgName}.png");

                    if (!File.Exists(iconPath))
                    {
                        pendingIcons.Add((appInfo, pkgName, iconPath));
                    }

                    apps.Add(new AppInfoItem
                    {
                        Name = appName,
                        PackageName = pkgName,
                        IsBypassed = bypassed.Contains(pkgName),
                        IconPath = File.Exists(iconPath) ? iconPath : null
                    });
                }
            }

            foreach (var bypassedPkg in bypassed)
            {
                if (seenPackages.Add(bypassedPkg))
                {
                    try
                    {
                        var appInfo = pm.GetApplicationInfo(bypassedPkg, 0);
                        var appName = appInfo.LoadLabel(pm)?.ToString() ?? bypassedPkg;
                        var iconPath = Path.Combine(iconsDir, $"{bypassedPkg}.png");

                        if (!File.Exists(iconPath))
                        {
                            pendingIcons.Add((appInfo, bypassedPkg, iconPath));
                        }

                        apps.Add(new AppInfoItem
                        {
                            Name = appName,
                            PackageName = bypassedPkg,
                            IsBypassed = true,
                            IconPath = File.Exists(iconPath) ? iconPath : null
                        });
                    }
                    catch { }
                }
            }

            t_cachedApps = [.. apps.OrderBy(a => a.Name)];

            if (pendingIcons.Count > 0)
            {
                var appsByPkg = apps.ToDictionary(a => a.PackageName, a => a, StringComparer.OrdinalIgnoreCase);
                _ = Task.Run(() =>
                {
                    foreach (var (appInfo, pkgName, iconPath) in pendingIcons)
                    {
                        try
                        {
                            if (File.Exists(iconPath))
                            {
                                if (appsByPkg.TryGetValue(pkgName, out var targetApp))
                                {
                                    MainThread.BeginInvokeOnMainThread(() => targetApp.IconPath = iconPath);
                                }
                                continue;
                            }

                            if (appInfo.LoadIcon(pm) is { } drawable)
                            {
                                var width = Math.Clamp(drawable.IntrinsicWidth > 0 ? drawable.IntrinsicWidth : 48, 32, 64);
                                var height = Math.Clamp(drawable.IntrinsicHeight > 0 ? drawable.IntrinsicHeight : 48, 32, 64);
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
                                    _ = bmp.Compress(global::Android.Graphics.Bitmap.CompressFormat.Png!, 80, fs);
                                    bmp.Recycle();

                                    if (appsByPkg.TryGetValue(pkgName, out var targetApp))
                                    {
                                        MainThread.BeginInvokeOnMainThread(() => targetApp.IconPath = iconPath);
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                });
            }

            return [.. t_cachedApps];
        });

    private static readonly HashSet<string> t_defaultCloakedPackages =
    [
        with(StringComparer.OrdinalIgnoreCase),
        "ru.sberbankmobile",           // Сбербанк Онлайн
        "com.idamob.tinkoff.android",   // Т-Банк (Тинькофф)
        "ru.vtb24.mobilebanking",       // ВТБ
        "ru.alfabank.mobile.android",   // Альфа-Банк
        "ru.raiffeisennews",           // Райффайзен
        "ru.gosuslugi.online",          // Госуслуги
        "ru.gosuslugi.pos",             // Госуслуги Решаем вместе
        "ru.yandex.yandexnavi",         // Яндекс Навигатор
        "ru.yandex.taxi",               // Яндекс Go / Такси
        "ru.yandex.searchplugin",       // Яндекс с Алисой
        "ru.yandex.market",             // Яндекс Маркет
        "com.vkontakte.android",        // ВКонтакте
        "com.vk.im",                    // VK Мессенджер
        "ru.mail.mailapp",              // Почта Mail.ru
        "com.wildberries.work",         // Wildberries
        "com.wildberries.wbdeti",
        "ru.ozon.app.android",          // Ozon
        "com.avito.android",            // Авито
        "ru.nspk.mirpay",               // Mir Pay
        "ru.samokat.app",               // Самокат
        "ru.magnit.app",                // Магнит
        "ru.x5.app"                     // Пятёрочка
    ];

    public List<string> GetBypassedPackages()
    {
        if (Preferences.ContainsKey(BypassedAppsKey))
        {
            var saved = Preferences.Get(BypassedAppsKey, string.Empty);
            return string.IsNullOrWhiteSpace(saved) ? [] : [.. saved.Split(',', StringSplitOptions.RemoveEmptyEntries)];
        }

        return [.. t_defaultCloakedPackages];
    }

    public void SaveBypassedPackages(List<string> packages) =>
        Preferences.Set(BypassedAppsKey, string.Join(",", packages));
}

