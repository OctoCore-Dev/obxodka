using Android.Content;
using Android.Net;
using Application = Android.App.Application;

namespace obxodka;

//единый фоновый ssot-координатор для плитки быстрых настроек и голосовых ассистентов android
[SupportedOSPlatform("android30.0")]
internal static class AndroidBackgroundVpnController
{
    public const string ExtraBackgroundAction = "obxodka.extra.BACKGROUND_ACTION";

    public const string ActionValueNoInternet = "no_internet";
    public const string ActionValueNoBalance = "no_balance";
    public const string ActionValueRequireAuth = "require_auth";
    public const string ActionValueRequireVpnPermission = "require_vpn_permission";
    public const string ActionValueAssistantDisabled = "assistant_disabled";

    private static readonly SemaphoreSlim s_operationLock = new(1, 1);
    private static readonly Lazy<ApiService> s_backgroundApiService = new(CreateBackgroundApiService);
    private static int s_stateHooked;
    private static CancellationTokenSource? s_backgroundBalanceCts;

    //подписка на события оркестратора для мгновенного обновления плитки и фонового контроля баланса
    public static void EnsureStateHooked(Context? context)
    {
        EnsurePlatformReady(context);

        if (Interlocked.CompareExchange(ref s_stateHooked, 1, 0) != 0)
        {
            return;
        }

        AndroidVpnService.Orchestrator.OnStateChanged += OnOrchestratorStateChanged;
    }

    //гарантия инициализации платформенных сервисов при холодном фоне без mainactivity
    public static void EnsurePlatformReady(Context? context)
    {
        var appContext = context?.ApplicationContext ?? Application.Context;
        if (appContext is null)
        {
            return;
        }

        OctopusVpnService.HookProtection();
        if (PlatformServices.Preferences is not DefaultPreferencesService)
        {
            return;
        }

        PlatformServices.Init(
            new MauiPreferencesService(),
            new AndroidSecureStorageService(),
            new AndroidConnectivityService(appContext),
            new MauiMainThreadService(),
            new AndroidDeviceInfoService(appContext),
            new AndroidCertificateAuditService(appContext));
    }

    //мгновенный прогрев foreground-службы vpn синхронно в окне клика плитки для холодного старта android 12+
    public static void TryPrewarmForegroundServiceOnClick(Context context)
    {
        try
        {
            var appContext = context.ApplicationContext ?? context;
            EnsurePlatformReady(appContext);

            if (OctopusVpnService.Instance is { IsActive: true })
            {
                return;
            }

            if (!PlatformServices.Preferences.Get(AppPreferenceKeys.UserIsLogged, false) ||
                !PlatformServices.Preferences.Get(AppPreferenceKeys.VpnDisclosureAccepted, false) ||
                !HasActiveNetworkUplink(appContext))
            {
                return;
            }

            using var vpnPrepareIntent = VpnService.Prepare(appContext);
            if (vpnPrepareIntent is not null)
            {
                return;
            }

            MainActivity.StartActualService(appContext);
            AppLogger.Log("[ANDROID-BG] Прогрев Foreground Service выполнен в окне клика плитки");
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[ANDROID-BG] Не удалось прогреть Foreground Service в OnClick: {ex.Message}");
        }
    }

    //переключение vpn по нажатию на системную плитку в шторке
    public static async Task ToggleFromTileAsync(Context context, Action<string> launchActivity)
    {
        var appContext = context.ApplicationContext ?? context;
        EnsureStateHooked(appContext);

        if (!await s_operationLock.WaitAsync(0).ConfigureAwait(false))
        {
            AppLogger.Log("[ANDROID-BG] Операция уже выполняется, повторный клик плитки пропущен");
            return;
        }

        try
        {
            var currentState = AndroidVpnService.Orchestrator.CurrentState;
            if (currentState is not (AppVpnState.Disconnected or AppVpnState.Error))
            {
                await StopVpnCoreAsync().ConfigureAwait(false);
                return;
            }

            await TryStartVpnInBackgroundAsync(appContext, launchActivity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ANDROID-BG TILE ERROR] {ex.Message}", ex);
            StopPrewarmedServiceIfNotConnected();
            OctopusTileService.RequestTileUpdate(appContext);
        }
        finally
        {
            _ = s_operationLock.Release();
        }
    }

    //неблокирующая обработка голосовой команды от google gemini или samsung bixby
    public static string DispatchAssistantCommand(Context context, VoiceAssistantCommand command, Action<string> launchActivity)
    {
        var appContext = context.ApplicationContext ?? context;
        EnsureStateHooked(appContext);

        var isEnabled = PlatformServices.Preferences.Get(AppPreferenceKeys.VoiceAssistantEnabled, true);
        if (!isEnabled)
        {
            launchActivity(ActionValueAssistantDisabled);
            return "Голосовое управление отключено в настройках Обходки";
        }

        var state = AndroidVpnService.Orchestrator.CurrentState;
        switch (command)
        {
            case VoiceAssistantCommand.Status:
                return FormatStatusSummary(state);

            case VoiceAssistantCommand.Connect:
                if (state == AppVpnState.Connected)
                {
                    return FormatStatusSummary(state);
                }

                if (state is AppVpnState.Connecting or AppVpnState.Reconnecting)
                {
                    return "VPN Обходка уже подключается";
                }

                _ = Task.Run(async () => await ExecuteAssistantConnectAsync(appContext, launchActivity).ConfigureAwait(false));
                return "Включаю VPN Обходка в фоне";

            case VoiceAssistantCommand.Disconnect:
                if (state is AppVpnState.Disconnected or AppVpnState.Error)
                {
                    return "VPN Обходка уже выключен";
                }

                _ = Task.Run(async () => await ExecuteAssistantDisconnectAsync(appContext).ConfigureAwait(false));
                return "VPN Обходка выключен";

            default:
                return "Неизвестная команда для Обходки";
        }
    }

    //человекочитаемое описание текущего состояния туннеля для ассистента
    public static string FormatStatusSummary(AppVpnState state) =>
        state switch
        {
            AppVpnState.Connected => string.IsNullOrWhiteSpace(OctopusEngine.Current.ActiveServerLocation)
                ? "VPN Обходка уже работает"
                : $"VPN Обходка уже работает ({OctopusEngine.Current.ActiveServerLocation})",
            AppVpnState.Connecting or AppVpnState.Reconnecting => "VPN Обходка сейчас подключается",
            AppVpnState.Disconnecting => "VPN Обходка отключается",
            _ => "VPN Обходка выключен"
        };

    //преобразование строкового маркера из intent в тип действия приложения
    public static BackgroundVpnActionKind ParseBackgroundAction(string? rawValue) =>
        rawValue switch
        {
            ActionValueNoInternet => BackgroundVpnActionKind.NoInternet,
            ActionValueNoBalance => BackgroundVpnActionKind.NoBalance,
            ActionValueRequireAuth => BackgroundVpnActionKind.RequireAuth,
            ActionValueRequireVpnPermission => BackgroundVpnActionKind.RequireVpnPermission,
            ActionValueAssistantDisabled => BackgroundVpnActionKind.AssistantDisabled,
            _ => BackgroundVpnActionKind.None
        };

    //формирование intent для вывода mainactivity с целевым диалогом
    public static Intent BuildMainActivityIntent(Context context, string actionValue)
    {
        var intent = new Intent(context, typeof(MainActivity));
        _ = intent.AddFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);
        _ = intent.PutExtra(ExtraBackgroundAction, actionValue);
        return intent;
    }

    //фоновое выполнение голосовой команды включения vpn
    private static async Task ExecuteAssistantConnectAsync(Context appContext, Action<string> launchActivity)
    {
        if (!await s_operationLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            if (AndroidVpnService.Orchestrator.CurrentState is not (AppVpnState.Disconnected or AppVpnState.Error))
            {
                return;
            }

            await TryStartVpnInBackgroundAsync(appContext, launchActivity).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ANDROID-BG ASSISTANT CONNECT ERROR] {ex.Message}", ex);
            StopPrewarmedServiceIfNotConnected();
            OctopusTileService.RequestTileUpdate(appContext);
        }
        finally
        {
            _ = s_operationLock.Release();
        }
    }

    //фоновое выполнение голосовой команды отключения vpn
    private static async Task ExecuteAssistantDisconnectAsync(Context appContext)
    {
        if (!await s_operationLock.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await StopVpnCoreAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLogger.LogError($"[ANDROID-BG ASSISTANT DISCONNECT ERROR] {ex.Message}", ex);
            OctopusTileService.RequestTileUpdate(appContext);
        }
        finally
        {
            _ = s_operationLock.Release();
        }
    }

    //проверка предусловий и запуск туннеля в фоне или перевод в приложение при нештатной ситуации
    private static async Task TryStartVpnInBackgroundAsync(Context appContext, Action<string> launchActivity)
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (!session.IsLoggedIn || string.IsNullOrWhiteSpace(session.JwtToken))
        {
            AppLogger.Log("[ANDROID-BG] Пользователь не авторизован — открываем экран входа");
            StopPrewarmedServiceIfNotConnected();
            launchActivity(ActionValueRequireAuth);
            OctopusTileService.RequestTileUpdate(appContext);
            return;
        }

        if (!HasActiveNetworkUplink(appContext))
        {
            AppLogger.Log("[ANDROID-BG] Нет интернета — открываем приложение с уведомлением");
            StopPrewarmedServiceIfNotConnected();
            launchActivity(ActionValueNoInternet);
            OctopusTileService.RequestTileUpdate(appContext);
            return;
        }

        var apiService = s_backgroundApiService.Value;
        var balanceSeconds = await ResolveCurrentBalanceSecondsAsync(apiService, session).ConfigureAwait(false);
        if (balanceSeconds <= 0)
        {
            AppLogger.Log("[ANDROID-BG] Закончилось время баланса — открываем приложение с предложением пополнения");
            StopPrewarmedServiceIfNotConnected();
            launchActivity(ActionValueNoBalance);
            OctopusTileService.RequestTileUpdate(appContext);
            return;
        }

        var isDisclosureAccepted = PlatformServices.Preferences.Get(AppPreferenceKeys.VpnDisclosureAccepted, false);
        using var vpnPrepareIntent = VpnService.Prepare(appContext);
        if (!isDisclosureAccepted || vpnPrepareIntent is not null)
        {
            AppLogger.Log("[ANDROID-BG] Требуется подтверждение VPN disclosure или системное разрешение Android VpnService");
            StopPrewarmedServiceIfNotConnected();
            launchActivity(ActionValueRequireVpnPermission);
            OctopusTileService.RequestTileUpdate(appContext);
            return;
        }

        var servers = await LoadServersForBackgroundAsync(apiService).ConfigureAwait(false);
        if (servers.Count == 0)
        {
            AppLogger.LogWarning("[ANDROID-BG] Список серверов недоступен при фоновом запуске");
            StopPrewarmedServiceIfNotConnected();
            launchActivity(ActionValueNoInternet);
            OctopusTileService.RequestTileUpdate(appContext);
            return;
        }

        var savedIp = PlatformServices.Preferences.Get(AppPreferenceKeys.SelectedServerIp, string.Empty);
        var targetServer = !string.IsNullOrWhiteSpace(savedIp)
            ? servers.FirstOrDefault(s => string.Equals(s.Ip, savedIp, StringComparison.Ordinal)) ?? servers[0]
            : servers[0];

        List<VpnServerDto> orderedCandidates =
        [
            targetServer,
            .. servers.Where(s => !string.Equals(s.Ip, targetServer.Ip, StringComparison.Ordinal))
        ];

        var directCertHash = orderedCandidates
            .Select(s => s.CertHash)
            .FirstOrDefault(h => !string.IsNullOrWhiteSpace(h));
        if (!string.IsNullOrWhiteSpace(directCertHash))
        {
            OctopusEngine.DirectOriginCertHash = directCertHash;
        }
        else if (string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash))
        {
            var (successHash, hashData, _) = await apiService.GetCertHashAsync().ConfigureAwait(false);
            if (successHash && hashData is not null && !string.IsNullOrWhiteSpace(hashData.Hash))
            {
                OctopusEngine.DirectOriginCertHash = hashData.Hash;
            }
        }

        var isShieldNode = SniEngine.ConfigureServerSecurity(targetServer, out _);
        if (!isShieldNode &&
            string.IsNullOrWhiteSpace(OctopusEngine.DynamicSslPublicKeyHash) &&
            !string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash))
        {
            OctopusEngine.DynamicSslPublicKeyHash = OctopusEngine.DirectOriginCertHash;
        }

        AppLogger.Log($"[ANDROID-BG] Фоновый запуск VPN: {targetServer.Location} ({targetServer.Ip}:{targetServer.Port})");
        await AndroidVpnService.Orchestrator.StartVpnAsync(targetServer.Ip, targetServer.Port, orderedCandidates).ConfigureAwait(false);
    }

    //остановка заранее прогретой foreground-службы если запуск отменился на проверке предусловий
    private static void StopPrewarmedServiceIfNotConnected()
    {
        if (AndroidVpnService.Orchestrator.CurrentState is AppVpnState.Disconnected or AppVpnState.Error)
        {
            try
            {
                OctopusVpnService.Instance?.StopNativeVpn();
            }
            catch
            {
                //игнорируем ошибку остановки прогретой службы
            }
        }
    }

    //штатная остановка vpn туннеля и оповещение сервера
    private static async Task StopVpnCoreAsync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await s_backgroundApiService.Value.StopVpnOnServerAsync().ConfigureAwait(false);
            }
            catch
            {
                //игнорируем сетевой сбой при отправке уведомления об остановке
            }
        });

        await AndroidVpnService.Orchestrator.StopVpnAsync().ConfigureAwait(false);
    }

    //проверка наличия активного сетевого канала на устройстве
    private static bool HasActiveNetworkUplink(Context appContext)
    {
        if (!OctopusEngine.IsUplinkAvailable)
        {
            return false;
        }

        try
        {
            if (appContext.GetSystemService(Context.ConnectivityService) is ConnectivityManager cm)
            {
                var network = cm.ActiveNetwork;
                if (network is null)
                {
                    return false;
                }

                var caps = cm.GetNetworkCapabilities(network);
                if (caps is null || !caps.HasCapability(NetCapability.Internet))
                {
                    return false;
                }
            }
        }
        catch
        {
            //fallback на флаг ядра OctopusEngine.IsUplinkAvailable
        }

        return true;
    }

    //получение актуального остатка секунд с быстрой синхронизацией по сети и кэш-фолбэком
    private static async Task<long> ResolveCurrentBalanceSecondsAsync(ApiService apiService, UserSession session)
    {
        var subFallback = session.SubscriptionUntil is { } until && until > DateTime.UtcNow
            ? Math.Max(0L, (long)(until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
            : 0L;
        var cachedSeconds = Math.Max(
            Math.Max(session.BalanceSeconds, subFallback),
            PlatformServices.Preferences.Get(AppPreferenceKeys.UserBalanceSec, 0L));

        try
        {
            using var timeoutCts = new CancellationTokenSource(3000);
            var (ok, profile, _) = await apiService.GetProfileAsync(maxRetries: 1, ct: timeoutCts.Token).ConfigureAwait(false);
            if (ok && profile is not null)
            {
                var liveSeconds = profile.BalanceSeconds > 0
                    ? profile.BalanceSeconds
                    : profile.SubscriptionUntil is { } liveUntil
                        ? Math.Max(0L, (long)(liveUntil.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                        : 0L;
                PlatformServices.Preferences.Set(AppPreferenceKeys.UserBalanceSec, liveSeconds);
                return liveSeconds;
            }
        }
        catch
        {
            //используем локальный кэш баланса при сетевой задержке
        }

        return cachedSeconds;
    }

    //загрузка списка серверов с api и фолбэком на кэш
    private static async Task<List<VpnServerDto>> LoadServersForBackgroundAsync(ApiService apiService)
    {
        try
        {
            var (success, servers, _) = await apiService.GetServersAsync().ConfigureAwait(false);
            if ((!success || servers is null || servers.Count == 0) &&
                !string.Equals(AppConfig.ApiBaseUrl, AppConfig.DefaultApiBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                AppConfig.ApiBaseUrl = AppConfig.DefaultApiBaseUrl;
                (success, servers, _) = await apiService.GetServersAsync().ConfigureAwait(false);
            }

            if (success && servers is { Count: > 0 })
            {
                try
                {
                    var serialized = JsonSerializer.Serialize(servers, AppJsonContext.Default.ListVpnServerDto);
                    PlatformServices.Preferences.Set(AppPreferenceKeys.CachedClusterServersJson, serialized);
                }
                catch
                {
                    //ошибка кэширования не критична
                }

                return servers;
            }
        }
        catch (Exception ex)
        {
            AppLogger.LogWarning($"[ANDROID-BG] Ошибка загрузки серверов с API: {ex.Message}");
        }

        var cachedJson = PlatformServices.Preferences.Get(AppPreferenceKeys.CachedClusterServersJson, string.Empty);
        if (!string.IsNullOrWhiteSpace(cachedJson))
        {
            try
            {
                var cached = JsonSerializer.Deserialize(cachedJson, AppJsonContext.Default.ListVpnServerDto);
                if (cached is { Count: > 0 })
                {
                    return cached;
                }
            }
            catch
            {
                //игнорируем поврежденный кэш
            }
        }

        return [];
    }

    //создание краткоживущего http-клиента api для фоновых вызовов без di окна
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope",
        Justification = "HttpClient owns handler and ApiService disposes HttpClient")]
    private static ApiService CreateBackgroundApiService()
    {
        var handler = ApiService.CreateDefaultHandler();
        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
            DefaultRequestVersion = new Version(1, 1),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        return new ApiService(client);
    }

    //единая реакция на любую смену состояния туннеля для синхронизации плитки и фонового учета времени
    private static void OnOrchestratorStateChanged(AppVpnState state)
    {
        var appContext = Application.Context;
        if (appContext is not null)
        {
            OctopusTileService.RequestTileUpdate(appContext);
        }

        if (state == AppVpnState.Connected)
        {
            StartBackgroundBalanceWatchdog();
        }
        else if (state is AppVpnState.Disconnected or AppVpnState.Error)
        {
            StopBackgroundBalanceWatchdog();
        }
    }

    //фоновый контроль остатка времени, пока vpn работает без открытого ui
    private static void StartBackgroundBalanceWatchdog()
    {
        StopBackgroundBalanceWatchdog();
        var cts = new CancellationTokenSource();
        s_backgroundBalanceCts = cts;
        _ = Task.Run(async () => await BackgroundBalanceLoopAsync(cts.Token).ConfigureAwait(false));
    }

    //остановка фонового цикла учета времени
    private static void StopBackgroundBalanceWatchdog()
    {
        var prev = Interlocked.Exchange(ref s_backgroundBalanceCts, null);
        if (prev is null)
        {
            return;
        }

        using (prev)
        {
            try
            {
                prev.Cancel();
            }
            catch (ObjectDisposedException)
            {
                //игнорируем уже освобожденный токен
            }
        }
    }

    //периодическое обновление локального баланса и авто-стоп при исчерпании часов в фоне
    private static async Task BackgroundBalanceLoopAsync(CancellationToken ct)
    {
        var syncCounter = 0;
        while (!ct.IsCancellationRequested && AndroidVpnService.Orchestrator.CurrentState == AppVpnState.Connected)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);

                //когда главное окно открыто, списание и синхронизацию ведет MainPage.ConsumeTimeLoopAsync
                if (MainPage.Current is not null)
                {
                    syncCounter = 0;
                    continue;
                }

                var currentSec = PlatformServices.Preferences.Get(AppPreferenceKeys.UserBalanceSec, 0L);
                if (currentSec > 0)
                {
                    currentSec = Math.Max(0L, currentSec - 1);
                    PlatformServices.Preferences.Set(AppPreferenceKeys.UserBalanceSec, currentSec);
                }

                syncCounter++;
                if (syncCounter >= 30)
                {
                    syncCounter = 0;
                    var (ok, profile, _) = await s_backgroundApiService.Value.GetProfileAsync(ct: ct).ConfigureAwait(false);
                    if (ok && profile is not null)
                    {
                        currentSec = profile.BalanceSeconds > 0
                            ? profile.BalanceSeconds
                            : profile.SubscriptionUntil is { } liveUntil
                                ? Math.Max(0L, (long)(liveUntil.ToUniversalTime() - DateTime.UtcNow).TotalSeconds)
                                : 0L;
                        PlatformServices.Preferences.Set(AppPreferenceKeys.UserBalanceSec, currentSec);
                    }
                }

                if (currentSec <= 0)
                {
                    AppLogger.Log("[ANDROID-BG] Баланс времени исчерпан в фоне — останавливаем VPN");
                    await StopVpnCoreAsync().ConfigureAwait(false);
                    break;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                //продолжаем цикл при разовом сетевом сбое
            }
        }
    }
}
