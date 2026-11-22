#if WINDOWS
using System.Security.Cryptography;
using Windows.ApplicationModel;
using AppInfo = Microsoft.Maui.ApplicationModel.AppInfo;
using Path = System.IO.Path;

namespace obxodka.Platforms.Windows;

//служба автоматического обновления приложения windows через microsoft store и github releases
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed partial class WindowsAppUpdaterService : IAppUpdaterService
{
    private const string StoreProductId = "9NZXP5WR803J";
    private const string GitHubRepo = "OctoCore-Dev/obxodka";

    private static readonly HttpClient s_httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private static readonly HttpClient s_downloadClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    //проверка наличия новой версии в microsoft store или github releases
    public async Task<StoreUpdateInfo> CheckVersionAsync()
    {
        var currentVersion = GetCurrentAppVersion();

        if (IsInstalledFromStore())
        {
            const string storeUrl = $"ms-windows-store://pdp/?productid={StoreProductId}";
            try
            {
                const string catalogUrl = $"https://displaycatalog.mp.microsoft.com/v7/products/{StoreProductId}?market=RU&languages=ru-RU";
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
                            hasUpdate,
                            currentVersion,
                            storeVersion,
                            "Microsoft Store",
                            storeUrl,
                            whatsNew);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.LogWarning($"[WINDOWS STORE CATALOG CHECK] {ex.Message}");
            }

            return new StoreUpdateInfo(
                false,
                currentVersion,
                currentVersion,
                "Microsoft Store",
                storeUrl);
        }

        const string fallbackUrl = $"https://github.com/{GitHubRepo}/releases/latest";
        try
        {
            const string apiUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
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
                        hasUpdate,
                        currentVersion,
                        ghVersion,
                        "GitHub Releases",
                        downloadUrl ?? fallbackUrl,
                        whatsNew,
                        expectedSha256);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[GITHUB RELEASE CHECK] {ex.Message}");
        }

        return new StoreUpdateInfo(
            false,
            currentVersion,
            currentVersion,
            "GitHub Releases",
            fallbackUrl);
    }

    //запуск проверки обновлений и отображение диалога пользователю
    [Obsolete("Obsolete")]
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
                MainThread.BeginInvokeOnMainThread(async void () =>
                {
                    try
                    {
                        if (IsInstalledFromStore())
                        {
                            var notes =
                                string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes)
                                    ? string.Empty
                                    : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                            if (notes.Length > 450)
                            {
                                notes = notes[..450] + "...";
                            }

                            var msg =
                                $"В Microsoft Store доступна новая версия v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете открыть страницу в Microsoft Store?";
                            var update = await NeoAlert.ShowConfirmAsync("Доступно обновление", msg, "Обновить", "Позже");
                            if (update)
                            {
                                await OpenStorePageAsync();
                            }
                        }
                        else
                        {
                            var notes =
                                string.IsNullOrWhiteSpace(updateInfo.ReleaseNotes)
                                    ? string.Empty
                                    : $"\n\nЧто нового:\n{updateInfo.ReleaseNotes.Trim()}";
                            if (notes.Length > 450)
                            {
                                notes = notes[..450] + "...";
                            }

                            var msg =
                                $"Доступна новая версия Obxodka v{updateInfo.LatestVersion} (у вас установлена v{updateInfo.CurrentVersion}).{notes}\n\nЖелаете скачать и установить обновление сейчас?";
                            var update = await NeoAlert.ShowConfirmAsync("Обновление Obxodka", msg, "Обновить", "Позже");
                            if (update)
                            {
                                _ =
                                    Task.Run(() => DownloadAndInstallGitHubUpdateAsync(updateInfo.StoreUrl, updateInfo.LatestVersion,
                                        updateInfo.ExpectedSha256));
                            }
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                });
            }
            else if (manualCheck)
            {
                MainThread.BeginInvokeOnMainThread(async void () =>
                {
                    try
                    {
                        var source = IsInstalledFromStore() ? "Microsoft Store" : "GitHub";
                        var msg = $"У вас установлена самая свежая версия v{updateInfo.CurrentVersion} ({source}).";
                        await NeoAlert.ShowAsync("Обновления", msg);
                    }
                    catch
                    {
                        // ignored
                    }
                });
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[WINDOWS UPDATE ERROR] {ex.Message}");
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetCurrentPackageFullName(ref int packageFullNameLength, nint packageFullName);

    //проверка запущено ли приложение из microsoft store
    private static bool IsInstalledFromStore()
    {
        var len = 0;
        var rc = GetCurrentPackageFullName(ref len, 0);
        //15700 = APP_MODEL_ERROR_NO_PACKAGE (не пакетированное приложение, исключает InvalidOperationException в WinRT)
        if (rc == 15700)
        {
            return false;
        }

        try
        {
            var package = Package.Current;
            return package.SignatureKind == PackageSignatureKind.Store;
        }
        catch
        {
            return false;
        }
    }

    //получение текущей версии приложения
    private static string GetCurrentAppVersion()
    {
        try
        {
            var v = AppInfo.Current.VersionString;
            if (!string.IsNullOrWhiteSpace(v) && v != "0.0.0.0" && v != "0.0.0" && v != "0.0")
            {
                return v;
            }
        }
        catch
        {
            // ignored
        }

        var asmVer = typeof(WindowsAppUpdaterService).Assembly.GetName().Version;
        return asmVer is not null && asmVer.Major > 0
            ? $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}"
            : "1.0.0";
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

            const string latestUrl = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
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
        await using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        var hashBytes = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hashBytes);
    }

    //проверка наличия и валидности сертификата authenticode у исполняемого файла
    private static readonly Guid s_wintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint CbStruct;
        public IntPtr PcwszFilePath;
        public IntPtr HFile;
        public IntPtr PgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint CbStruct;
        public IntPtr PPolicyCallbackData;
        public IntPtr PSipClientData;
        public uint DwUiChoice;
        public uint FdwRevocationChecks;
        public uint DwUnionChoice;
        public IntPtr PFile;
        public uint DwStateAction;
        public IntPtr HWvtStateData;
        public IntPtr PwszUrlReference;
        public uint DwProvFlags;
        public uint DwUiContext;
        public IntPtr PSignatureSettings;
    }

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    private static int VerifyPeTrust(string filePath)
    {
        var filePathPtr = Marshal.StringToCoTaskMemUni(filePath);
        var fileInfoPtr = IntPtr.Zero;
        try
        {
            var fileInfoSize = Marshal.SizeOf<WinTrustFileInfo>();
            var fileInfo = new WinTrustFileInfo
            {
                CbStruct = (uint)fileInfoSize,
                PcwszFilePath = filePathPtr
            };
            fileInfoPtr = Marshal.AllocCoTaskMem(fileInfoSize);
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);

            var trustData = new WinTrustData
            {
                CbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                DwUiChoice = 2, //WTD_UI_NONE
                FdwRevocationChecks = 0, //WTD_REVOKE_NONE
                DwUnionChoice = 1, //WTD_CHOICE_FILE
                PFile = fileInfoPtr,
                DwStateAction = 1, //WTD_STATEACTION_VERIFY
                DwProvFlags = 0x00000010 | 0x00000080 //WTD_SAFER_FLAG | WTD_CACHE_ONLY_URL_RETRIEVAL
            };

            var actionId = s_wintrustActionGenericVerifyV2;
            var hr = WinVerifyTrust(new IntPtr(-1), ref actionId, ref trustData);

            trustData.DwStateAction = 2; //WTD_STATEACTION_CLOSE
            _ = WinVerifyTrust(new IntPtr(-1), ref actionId, ref trustData);
            return hr;
        }
        finally
        {
            if (fileInfoPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(fileInfoPtr);
            }

            if (filePathPtr != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(filePathPtr);
            }
        }
    }

    [Obsolete("Obsolete")]
    private static AuthenticodeCheckStatus CheckAuthenticodeSignature(string filePath)
    {
        try
        {
            var wvtHr = VerifyPeTrust(filePath);
            if (wvtHr == 0)
            {
                return AuthenticodeCheckStatus.TrustedChain;
            }

            //TRUST_E_NOSIGNATURE (0x800B0100) — подпись отсутствует
            if (wvtHr == unchecked((int)0x800B0100))
            {
                return AuthenticodeCheckStatus.Unsigned;
            }

            //TRUST_E_BAD_DIGEST (0x80096010) или TRUST_E_SUBJECT_NOT_TRUSTED — целостность PE нарушена
            if (wvtHr is unchecked((int)0x80096010) or unchecked((int)0x800B0004))
            {
                return AuthenticodeCheckStatus.InvalidSignature;
            }

            using var rawCert = X509Certificate.CreateFromSignedFile(filePath);
            using var cert = new X509Certificate2(rawCert);

            AppLogger.Log(
                $"[AUTHENTICODE] Installer is signed by '{cert.Subject}', Thumbprint: {cert.Thumbprint}, Valid: {cert.NotBefore:d} - {cert.NotAfter:d}");

            if (cert.Verify())
            {
                return AuthenticodeCheckStatus.TrustedChain;
            }

            //само подписанный сертификат GitHub Actions допустим только при проверке sha256 без повреждений подписи
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            _ = chain.Build(cert);
            foreach (var status in chain.ChainStatus)
            {
                if (status.Status is X509ChainStatusFlags.NotSignatureValid or X509ChainStatusFlags.InvalidBasicConstraints)
                {
                    return AuthenticodeCheckStatus.InvalidSignature;
                }
            }

            return AuthenticodeCheckStatus.SelfSignedUntrustedRoot;
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AUTHENTICODE] File is unsigned or signature metadata unavailable: {ex.Message}");
            return AuthenticodeCheckStatus.Unsigned;
        }
    }

    //проверка доверенного https-источника для загрузки и открытия релиза
    private static bool IsTrustedUpdateUri(string? url, [NotNullWhen(true)] out Uri? uri)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            uri = null;
            return false;
        }

        var host = uri.Host;
        var trustedHost =
            host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
            host.Equals("obxodka.one", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".obxodka.one", StringComparison.OrdinalIgnoreCase);

        if (trustedHost)
        {
            return true;
        }

        uri = null;
        return false;
    }

    //загрузка верификация sha256 и запуск установщика обновления с github
    [Obsolete("Obsolete")]
    private static async Task DownloadAndInstallGitHubUpdateAsync(string? url, string version, string? expectedSha256)
    {
        if (!IsTrustedUpdateUri(url, out var updateUri))
        {
            if (!string.IsNullOrWhiteSpace(url))
            {
                AppLogger.LogError($"[UPDATE-SECURITY ALERT] Untrusted update URL blocked: {url}");
            }

            _ = await MainThread.InvokeOnMainThreadAsync(() =>
                Launcher.OpenAsync(new Uri($"https://github.com/{GitHubRepo}/releases/latest"))).ConfigureAwait(false);
            return;
        }

        if (updateUri.AbsolutePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "obxodka", "updates");
            _ = Directory.CreateDirectory(updateDir);
            var safeVersion = string.Concat(version.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'));
            if (string.IsNullOrEmpty(safeVersion))
            {
                safeVersion = "latest";
            }

            var installerPath = Path.Combine(updateDir, $"Obxodka-Setup-v{safeVersion}.exe");
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, updateUri);
                req.Headers.Add("User-Agent", "Obxodka-Desktop-Updater");

                using var resp = await s_downloadClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    await using (var fs = new FileStream(installerPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        await resp.Content.CopyToAsync(fs).ConfigureAwait(false);
                    }

                    //строгая проверка контрольной суммы sha256 перед запуском инсталлятора
                    var computedHash = await ComputeFileSha256Async(installerPath).ConfigureAwait(false);
                    AppLogger.Log($"[UPDATE-SECURITY] Downloaded installer SHA256: {computedHash}");

                    if (string.IsNullOrWhiteSpace(expectedSha256))
                    {
                        AppLogger.LogError(
                            "[UPDATE-SECURITY ALERT] Missing SHA256 checksum in GitHub release metadata. Aborting installer launch and deleting file.");
                        TryDeleteFile(installerPath);

                        MainThread.BeginInvokeOnMainThread(async void () =>
                        {
                            try
                            {
                                await NeoAlert.ShowAsync(
                                    "Ошибка безопасности",
                                    "В описании релиза отсутствует обязательная контрольная сумма SHA-256. Запуск обновления заблокирован.");
                            }
                            catch
                            {
                                // ignored
                            }
                        });
                        return;
                    }

                    if (!string.Equals(computedHash, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        AppLogger.LogError(
                            $"[UPDATE-SECURITY ALERT] Checksum mismatch! Expected: {expectedSha256}, Actual: {computedHash}. Deleting file.");
                        TryDeleteFile(installerPath);

                        MainThread.BeginInvokeOnMainThread(async void () =>
                        {
                            try
                            {
                                await NeoAlert.ShowAsync(
                                    "Ошибка безопасности",
                                    "Контрольная сумма загруженного обновления не совпадает с официальным релизом. Запуск заблокирован для безопасности системы.");
                            }
                            catch
                            {
                                // ignored
                            }
                        });
                        return;
                    }

                    AppLogger.Log("[UPDATE-SECURITY] SHA256 checksum verified successfully.");

                    //проверка встроенной подписи authenticode перед запуском инсталлятора
                    var sigStatus = CheckAuthenticodeSignature(installerPath);
                    if (sigStatus is not AuthenticodeCheckStatus.TrustedChain)
                    {
                        AppLogger.LogError(
                            $"[UPDATE-SECURITY ALERT] Installer Authenticode signature invalid or missing ({sigStatus}). Blocking launch.");
                        TryDeleteFile(installerPath);

                        MainThread.BeginInvokeOnMainThread(async void () =>
                        {
                            try
                            {
                                await NeoAlert.ShowAsync(
                                    "Ошибка безопасности",
                                    "Цифровая подпись установщика отсутствует или повреждена. Запуск обновления заблокирован."
                                );
                            }
                            catch
                            {
                                // ignored
                            }
                        });
                        return;
                    }

                    //корректная остановка активного vpn перед перезапуском в установщик
                    try
                    {
                        var vpnService = IPlatformApplication.Current?.Services.GetService<IVpnService>();
                        if (vpnService is not null && vpnService.CurrentState != AppVpnState.Disconnected)
                        {
                            await vpnService.StopVpnAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        }
                    }
                    catch (Exception stopEx)
                    {
                        AppLogger.LogWarning($"[UPDATE CLEANUP WARN] Failed to stop VPN before restart: {stopEx.Message}");
                    }

                    _ = Process.Start(new ProcessStartInfo(installerPath)
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
                TryDeleteFile(installerPath);
            }
        }

        _ = await MainThread.InvokeOnMainThreadAsync(() => Launcher.OpenAsync(updateUri)).ConfigureAwait(false);
    }

    //безопасное удаление временного файла установщика
    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
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

    private enum AuthenticodeCheckStatus
    {
        Unsigned,
        TrustedChain,
        SelfSignedUntrustedRoot,
        InvalidSignature
    }
}
#endif
