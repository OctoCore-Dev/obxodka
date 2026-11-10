#if WINDOWS
using System.Security.Cryptography;

namespace obxodka.Platforms.Windows;

//служба автоматического обновления приложения windows через microsoft store и github releases
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsAppUpdaterService : IAppUpdaterService
{
    public const string StoreProductId = "9NZXP5WR803J";
    public const string GitHubRepo = "OctoCore-Dev/obxodka";

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    //проверка запущено ли приложение из microsoft store
    public static bool IsInstalledFromStore()
    {
        try
        {
            var package = global::Windows.ApplicationModel.Package.Current;
            return package.SignatureKind == global::Windows.ApplicationModel.PackageSignatureKind.Store;
        }
        catch
        {
            return false;
        }
    }

    //получение текущей версии приложения
    public static string GetCurrentAppVersion()
    {
        try
        {
            var v = AppInfo.Current.VersionString;
            if (!string.IsNullOrWhiteSpace(v) && v != "0.0.0.0" && v != "0.0.0" && v != "0.0")
            {
                return v;
            }
        }
        catch { }

        var asmVer = typeof(WindowsAppUpdaterService).Assembly.GetName().Version;
        return asmVer is not null && asmVer.Major > 0
            ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
            : "1.0.0";
    }

    //проверка наличия новой версии в microsoft store или github releases
    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = GetCurrentAppVersion();

        if (IsInstalledFromStore())
        {
            var storeUrl = $"ms-windows-store://pdp/?productid={StoreProductId}";
            try
            {
                var catalogUrl = $"https://displaycatalog.mp.microsoft.com/v7/products/{StoreProductId}?market=RU&languages=ru-RU";
                using var req = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
                req.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                using var resp = await s_httpClient.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var storeVersion = StoreVersionParser.ParseMicrosoftStoreCatalogJson(json);

                    if (!string.IsNullOrWhiteSpace(storeVersion))
                    {
                        var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, storeVersion);
                        string? whatsNew = null;
                        if (hasUpdate)
                        {
                            whatsNew = await FetchReleaseNotesForVersionAsync(storeVersion).ConfigureAwait(false);
                        }

                        return new StoreUpdateInfo(
                            HasUpdate: hasUpdate,
                            CurrentVersion: currentVersion,
                            LatestVersion: storeVersion,
                            StoreName: "Microsoft Store",
                            StoreUrl: storeUrl,
                            ReleaseNotes: whatsNew);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[WINDOWS STORE CATALOG CHECK ERROR] {ex.Message}", ex);
            }

            return new StoreUpdateInfo(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                StoreName: "Microsoft Store",
                StoreUrl: storeUrl);
        }
        else
        {
            var fallbackUrl = $"https://github.com/{GitHubRepo}/releases/latest";
            try
            {
                var apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
                using var req = new HttpRequestMessage(HttpMethod.Get, apiUrl);
                req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");

                using var resp = await s_httpClient.SendAsync(req).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var (ghVersion, whatsNew, downloadUrl, expectedSha256) = StoreVersionParser.ParseGitHubReleaseJson(json);

                    if (!string.IsNullOrWhiteSpace(ghVersion))
                    {
                        var hasUpdate = StoreVersionParser.IsNewerVersion(currentVersion, ghVersion);
                        return new StoreUpdateInfo(
                            HasUpdate: hasUpdate,
                            CurrentVersion: currentVersion,
                            LatestVersion: ghVersion,
                            StoreName: "GitHub Releases",
                            StoreUrl: downloadUrl ?? fallbackUrl,
                            ReleaseNotes: whatsNew,
                            ExpectedSha256: expectedSha256);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[GITHUB RELEASE CHECK ERROR] {ex.Message}", ex);
            }

            return new StoreUpdateInfo(
                HasUpdate: false,
                CurrentVersion: currentVersion,
                LatestVersion: currentVersion,
                StoreName: "GitHub Releases",
                StoreUrl: fallbackUrl);
        }
    }

    //запуск проверки обновлений и отображение диалога пользователю
    public async Task CheckForUpdatesAsync(bool manualCheck = false)
    {
        try
        {
            if (IsInstalledFromStore() && !manualCheck)
            {
                return;
            }

            var updateInfo = await CheckVersionAsync().ConfigureAwait(false);
            if (updateInfo.HasUpdate)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    if (IsInstalledFromStore())
                    {
                        var notes = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes) ? string.Empty : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                        if (notes.Length > 450)
                        {
                            notes = notes[..450] + "...";
                        }
                        var msg = $"В Microsoft Store доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете открыть страницу в Microsoft Store?";
                        var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");
                        if (update)
                        {
                            await OpenStorePageAsync();
                        }
                    }
                    else
                    {
                        var notes = string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes) ? string.Empty : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                        if (notes.Length > 450)
                        {
                            notes = notes[..450] + "...";
                        }
                        var msg = $"Доступна новая версия Obxodka v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете скачать и установить обновление сейчас?";
                        var update = await NeoAlert.ShowConfirmAsync("Обновление Obxodka", msg, "Обновить", "Позже");
                        if (update)
                        {
                            _ = Task.Run(() => DownloadAndInstallGitHubUpdateAsync(updateInfo.StoreUrl, updateInfo.LatestVersion, updateInfo.ExpectedSha256));
                        }
                    }
                });
            }
            else if (manualCheck)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    var source = IsInstalledFromStore() ? "Microsoft Store" : "GitHub";
                    var msg = $"У вас установлена самая свежая версия v{updateInfo.CurrentVersion} ({source}).";
                    await NeoAlert.ShowAsync("Обновления", msg, "OK");
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[WINDOWS UPDATE ERROR] {ex.Message}", ex);
            if (manualCheck)
            {
                if (IsInstalledFromStore())
                {
                    await OpenStorePageAsync();
                }
                else
                {
                    _ = await Launcher.OpenAsync(new Uri($"https://github.com/{GitHubRepo}/releases/latest"));
                }
            }
        }
    }

    //получение описания изменений версии из github releases
    private static async Task<string?> FetchReleaseNotesForVersionAsync(string version)
    {
        try
        {
            var tagUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/tags/v{version}";
            using var req = new HttpRequestMessage(HttpMethod.Get, tagUrl);
            req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");
            using var resp = await s_httpClient.SendAsync(req).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var (_, whatsNew, _, _) = StoreVersionParser.ParseGitHubReleaseJson(json);
                if (!string.IsNullOrWhiteSpace(whatsNew))
                {
                    return whatsNew;
                }
            }

            var latestUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
            using var latestReq = new HttpRequestMessage(HttpMethod.Get, latestUrl);
            latestReq.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");
            using var latestResp = await s_httpClient.SendAsync(latestReq).ConfigureAwait(false);
            if (latestResp.IsSuccessStatusCode)
            {
                var json = await latestResp.Content.ReadAsStringAsync().ConfigureAwait(false);
                var (latestVer, whatsNew, _, _) = StoreVersionParser.ParseGitHubReleaseJson(json);
                if (latestVer == version && !string.IsNullOrWhiteSpace(whatsNew))
                {
                    return whatsNew;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[RELEASE NOTES FETCH ERROR] {ex.Message}");
        }

        return null;
    }

    //вычисление sha256 хеша файла
    private static async Task<string> ComputeFileSha256Async(string filePath, CancellationToken ct = default)
    {
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hashBytes);
    }

    //проверка наличия и валидности сертификата authenticode у исполняемого файла
    private static bool TryVerifyAuthenticode(string filePath)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert = new X509Certificate2(rawCert);
#pragma warning restore SYSLIB0057
            AppLogger.Log($"[AUTHENTICODE] Installer is signed by '{cert.Subject}', Thumbprint: {cert.Thumbprint}, Valid: {cert.NotBefore:d} - {cert.NotAfter:d}");
            return cert.Verify();
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AUTHENTICODE] File is not signed with Authenticode or signature is invalid: {ex.Message}");
            return false;
        }
    }

    //загрузка верификация sha256 и запуск установщика обновления с github
    private static async Task DownloadAndInstallGitHubUpdateAsync(string? url, string version, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _ = await Launcher.OpenAsync(new Uri($"https://github.com/{GitHubRepo}/releases/latest"));
            return;
        }

        if (url.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var tempPath = Path.Combine(Path.GetTempPath(), $"Obxodka-Setup-v{version}.exe");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");

                using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                    }

                    //проверка целостности sha256
                    var computedHash = await ComputeFileSha256Async(tempPath).ConfigureAwait(false);
                    AppLogger.Log($"[UPDATE-SECURITY] Downloaded installer SHA256: {computedHash}");

                    if (!string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        if (!string.Equals(computedHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
                        {
                            AppLogger.LogError($"[UPDATE-SECURITY ALERT] Checksum mismatch! Expected: {expectedSha256}, Actual: {computedHash}. Deleting file.");
                            try
                            {
                                File.Delete(tempPath);
                            }
                            catch { }

                            MainThread.BeginInvokeOnMainThread(async () =>
                                await NeoAlert.ShowAsync(
                                    "Ошибка безопасности",
                                    "Контрольная сумма загруженного обновления не совпадает с официальным релизом. Запуск заблокирован для безопасности системы.",
                                    "OK"));
                            return;
                        }

                        AppLogger.Log("[UPDATE-SECURITY] SHA256 checksum verified successfully.");
                    }
                    else
                    {
                        AppLogger.LogWarning("[UPDATE-SECURITY] Expected SHA256 was not found in release metadata; checking Authenticode...");
                    }

                    //проверка подписи authenticode при ее наличии
                    _ = TryVerifyAuthenticode(tempPath);

                    _ = Process.Start(new ProcessStartInfo(tempPath)
                    {
                        UseShellExecute = true
                    });

                    Environment.Exit(0);
                    return;
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[DOWNLOAD INSTALLER ERROR] {ex.Message}", ex);
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch { }
            }
        }

        _ = await Launcher.OpenAsync(new Uri(url));
    }

    //открытие страницы приложения в магазине microsoft store
    public static async Task OpenStorePageAsync()
    {
        try
        {
            _ = await Launcher.OpenAsync(new Uri($"ms-windows-store://pdp/?productid={StoreProductId}"));
        }
        catch
        {
            try
            {
                _ = await Launcher.OpenAsync(new Uri("ms-windows-store://downloadsandupdates"));
            }
            catch (Exception ex)
            {
                AppLogger.LogError($"[STORE LAUNCH ERROR] {ex.Message}", ex);
            }
        }
    }
}
#endif
