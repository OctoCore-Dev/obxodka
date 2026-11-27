using System.Security.Principal;

namespace obxodka.Cli;

//основной обработчик команд кроссплатформенного cli клиента obxodka
public sealed class CliApp : IDisposable
{
    private static readonly Dictionary<string, (string Key, bool DefaultBool)> s_configKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kill-switch"] = (AppPreferenceKeys.KillSwitch, true),
        ["adblock"] = (AppPreferenceKeys.UseAdblockDns, false),
        ["block-ipv6"] = (AppPreferenceKeys.BlockIpv6, false),
        ["auto-reconnect"] = (AppPreferenceKeys.AutoReconnect, true),
        ["force-websocket"] = (AppPreferenceKeys.ForceWebSocket, false),
        ["bypass-ru"] = (AppPreferenceKeys.BypassRuSites, false),
        ["sync-timezone"] = (AppPreferenceKeys.SyncTimeZoneWithServer, false)
    };

    private static readonly Dictionary<char, string> s_cyrillicToLatin = new()
    {
        ['а'] = "a",
        ['б'] = "b",
        ['в'] = "v",
        ['г'] = "g",
        ['д'] = "d",
        ['е'] = "e",
        ['ё'] = "e",
        ['ж'] = "zh",
        ['з'] = "z",
        ['и'] = "i",
        ['й'] = "y",
        ['к'] = "k",
        ['л'] = "l",
        ['м'] = "m",
        ['н'] = "n",
        ['о'] = "o",
        ['п'] = "p",
        ['р'] = "r",
        ['с'] = "s",
        ['т'] = "t",
        ['у'] = "u",
        ['ф'] = "f",
        ['х'] = "h",
        ['ц'] = "ts",
        ['ч'] = "ch",
        ['ш'] = "sh",
        ['щ'] = "sch",
        ['ъ'] = "",
        ['ы'] = "y",
        ['ь'] = "",
        ['э'] = "e",
        ['ю'] = "yu",
        ['я'] = "ya"
    };

    private readonly HttpMessageHandler _httpHandler;
    private readonly HttpClient _httpClient;
    private readonly ApiService _apiService;

    public CliApp()
    {
        _httpHandler = ApiService.CreateDefaultHandler();
        _httpClient = new HttpClient(_httpHandler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(30),
            DefaultRequestVersion = new Version(1, 1),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        _apiService = new ApiService(_httpClient);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
        _httpHandler.Dispose();
    }

    //точка маршрутизации разобранной команды cli
    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        var parsed = CliCommandParser.Parse(args);
        if (parsed.Command == "help" || parsed.HasFlag("help"))
        {
            PrintHelp();
            return 0;
        }

        return parsed.Command switch
        {
            "version" => PrintVersion(),
            "login" => await HandleLoginAsync(parsed, ct).ConfigureAwait(false),
            "logout" => await HandleLogoutAsync(parsed).ConfigureAwait(false),
            "whoami" => await HandleWhoAmIAsync(parsed, ct).ConfigureAwait(false),
            "servers" => await HandleServersAsync(parsed, ct).ConfigureAwait(false),
            "connect" => await HandleConnectAsync(parsed, ct).ConfigureAwait(false),
            "disconnect" => HandleDisconnect(parsed),
            "status" => await HandleStatusAsync(parsed, ct).ConfigureAwait(false),
            "config" => HandleConfig(parsed),
            "split" => HandleSplit(parsed),
            "diag" => await HandleDiagAsync(parsed, ct).ConfigureAwait(false),
            "alias" => HandleAlias(parsed),
            "completion" => HandleCompletion(parsed),
            _ => PrintUnknownCommand(parsed.Command)
        };
    }

    //генерация понятного короткого идентификатора сервера с транслитерацией
    public static string BuildServerSlug(VpnServerDto server)
    {
        var raw = server.Location.ToLowerInvariant();
        var sb = new StringBuilder(raw.Length);
        var prevDash = false;

        foreach (var ch in raw)
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                _ = sb.Append(ch);
                prevDash = false;
            }
            else if (s_cyrillicToLatin.TryGetValue(ch, out var latin))
            {
                if (latin.Length > 0)
                {
                    _ = sb.Append(latin);
                    prevDash = false;
                }
            }
            else if (!prevDash && sb.Length > 0)
            {
                _ = sb.Append('-');
                prevDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (string.IsNullOrWhiteSpace(slug))
        {
            return server.Ip;
        }

        //сокращаем длинные названия до компактного читаемого алиаса
        var parts = slug.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3)
        {
            var country = parts[0].Length > 3 ? parts[0][..3] : parts[0];
            var city = parts[1].Length > 3 ? parts[1][..3] : parts[1];
            var tag = parts[2] == "cloudflare" ? "cf" : parts[2].Length > 4 ? parts[2][..4] : parts[2];
            return $"{country}-{city}-{tag}";
        }

        if (parts.Length == 2)
        {
            var country = parts[0].Length > 3 ? parts[0][..3] : parts[0];
            var city = parts[1].Length > 4 ? parts[1][..4] : parts[1];
            return $"{country}-{city}";
        }

        return slug.Length > 16 ? slug[..16] : slug;
    }

    //вход по одноразовому коду на email
    private async Task<int> HandleLoginAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var email = cmd.GetOption("email") ?? (cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : null);
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Write($"  {CliAnsiUi.PaintBold("›", CliAnsiUi.Cyan)} Email: ");
            email = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            CliAnsiUi.PrintError("Укажите email (например: obx auth --email user@example.com).");
            return 2;
        }

        var code = cmd.GetOption("code");
        if (string.IsNullOrWhiteSpace(code))
        {
            var (sent, sendErr) = await CliAnsiUi.RunWithSpinnerAsync(
                $"Отправка кода подтверждения на {email}...",
                () => _apiService.RequestCodeAsync(new EmailAuthRequest(email), ct),
                ct).ConfigureAwait(false);
            if (!sent)
            {
                CliAnsiUi.PrintError($"Не удалось отправить код: {sendErr ?? "ошибка сети"}");
                return 1;
            }

            CliAnsiUi.PrintSuccess($"Код подтверждения отправлен на {email}.");
            Console.Write($"  {CliAnsiUi.PaintBold("›", CliAnsiUi.Cyan)} Введите код из письма: ");
            code = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            CliAnsiUi.PrintError("Код подтверждения не введён.");
            return 2;
        }

        var req = new EmailVerifyRequest(email, code, PlatformServices.DeviceInfo.DeviceId, PlatformServices.DeviceInfo.Name);
        var (ok, data, verifyErr) = await CliAnsiUi.RunWithSpinnerAsync(
            "Проверка кода и выпуск сессии...",
            () => _apiService.VerifyCodeAsync(req, ct),
            ct).ConfigureAwait(false);
        if (!ok || data is null)
        {
            CliAnsiUi.PrintError($"Авторизация не удалась: {verifyErr ?? "неверный код"}");
            return 1;
        }

        var session = new UserSession
        {
            Email = string.IsNullOrWhiteSpace(data.Email) ? email : data.Email,
            IsLoggedIn = true,
            JwtToken = data.Token,
            VpnConfig = data.VpnConfig,
            SubscriptionUntil = data.SubscriptionUntil,
            BalanceSeconds = data.BalanceSeconds
        };
        await AuthManager.SaveSessionAsync(session).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            var hours = Math.Round(TimeSpan.FromSeconds(Math.Max(0, session.BalanceSeconds)).TotalHours, 1);
            var output = new CliWhoAmIOutput(true, session.Email, session.BalanceSeconds, hours);
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliWhoAmIOutput));
        }
        else
        {
            CliAnsiUi.PrintSuccess($"Вход выполнен: {CliAnsiUi.PaintBold(session.Email, CliAnsiUi.Cyan)}");
        }

        return 0;
    }

    //выход из аккаунта и очистка локальных токенов
    private static async Task<int> HandleLogoutAsync(CliParsedCommand cmd)
    {
        await AuthManager.ClearSessionAsync().ConfigureAwait(false);
        if (cmd.IsJson)
        {
            Console.WriteLine(/*lang=json,strict*/ "{\"loggedIn\":false}");
        }
        else
        {
            CliAnsiUi.PrintSuccess("Сессия очищена, локальные токены удалены.");
        }

        return 0;
    }

    //отображение текущего пользователя и баланса времени
    private async Task<int> HandleWhoAmIAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (!session.IsLoggedIn)
        {
            if (cmd.IsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new CliWhoAmIOutput(false, null, 0, 0), CliJsonContext.Default.CliWhoAmIOutput));
            }
            else
            {
                CliAnsiUi.PrintWarn("Вы не авторизованы. Выполните: obx auth");
            }

            return 1;
        }

        var (ok, profile, _) = cmd.IsJson
            ? await _apiService.GetProfileAsync(ct: ct).ConfigureAwait(false)
            : await CliAnsiUi.RunWithSpinnerAsync("Загрузка профиля...", () => _apiService.GetProfileAsync(ct: ct), ct).ConfigureAwait(false);
        var balanceSec = ok && profile is not null ? profile.BalanceSeconds : session.BalanceSeconds;
        var span = TimeSpan.FromSeconds(Math.Max(0, balanceSec));
        var hours = Math.Round(span.TotalHours, 1);

        if (cmd.IsJson)
        {
            var output = new CliWhoAmIOutput(true, session.Email, balanceSec, hours);
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliWhoAmIOutput));
            return 0;
        }

        var balanceText = balanceSec > 0
            ? CliAnsiUi.PaintBold($"{hours:F1} ч ({(int)span.TotalDays} дн. {span.Hours} ч)", CliAnsiUi.Green)
            : CliAnsiUi.Paint("0 ч (пополните баланс)", CliAnsiUi.Amber);
        CliAnsiUi.RenderCard(
            "ПРОФИЛЬ OBXODKA",
            [
                ("Аккаунт", CliAnsiUi.PaintBold(session.Email ?? "—", CliAnsiUi.White)),
                ("Баланс", balanceText)
            ]);
        return 0;
    }

    //получение списка серверов с параллельным tcp пингом и красивой таблицей
    private async Task<int> HandleServersAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var (ok, servers, err) = cmd.IsJson
            ? await _apiService.GetServersAsync(ct: ct).ConfigureAwait(false)
            : await CliAnsiUi.RunWithSpinnerAsync("Получение списка серверов...", () => _apiService.GetServersAsync(ct: ct), ct).ConfigureAwait(false);
        if (!ok || servers is null || servers.Count == 0)
        {
            CliAnsiUi.PrintError($"Не удалось получить список серверов: {err ?? "список пуст"}");
            return 1;
        }

        var doPing = cmd.HasFlag("ping");
        int?[] pings;
        if (doPing)
        {
            async Task<int?[]> PingAllAsync() =>
                await Task.WhenAll(servers.Select(s => MeasureTcpPingMsAsync(s.Ip, s.Port, ct))).ConfigureAwait(false);

            pings = cmd.IsJson
                ? await PingAllAsync().ConfigureAwait(false)
                : await CliAnsiUi.RunWithSpinnerAsync($"Измерение пинга до {servers.Count} серверов...", PingAllAsync, ct).ConfigureAwait(false);
        }
        else
        {
            pings = new int?[servers.Count];
        }

        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<CliServerOutput> items = [];
        for (var i = 0; i < servers.Count; i++)
        {
            var s = servers[i];
            var baseSlug = BuildServerSlug(s);
            var slug = baseSlug;
            var suffix = 2;
            while (!usedSlugs.Add(slug))
            {
                slug = $"{baseSlug}-{suffix++}";
            }

            items.Add(new CliServerOutput(slug, s.Location, s.Location, s.Ip, s.Port, pings[i]));
        }

        if (cmd.IsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(items, CliJsonContext.Default.ListCliServerOutput));
            return 0;
        }

        string[] headers = doPing ? ["ID", "ЛОКАЦИЯ", "АДРЕС", "ПИНГ"] : ["ID", "ЛОКАЦИЯ", "АДРЕС", "СТАТУС"];
        List<string[]> rows = [];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var lastCol = doPing
                ? CliAnsiUi.FormatPingBadge(item.PingMs)
                : servers[i].IsOnline ? CliAnsiUi.Paint("● online", CliAnsiUi.Green) : CliAnsiUi.Paint("○ offline", CliAnsiUi.Red);
            rows.Add(
            [
                CliAnsiUi.PaintBold(item.Id, CliAnsiUi.Cyan),
                CliAnsiUi.Paint(item.Location, CliAnsiUi.White),
                CliAnsiUi.Paint($"{item.Ip}:{item.Port}", CliAnsiUi.Muted),
                lastCol
            ]);
        }

        CliAnsiUi.RenderTable(headers, rows, [false, false, false, true]);
        CliAnsiUi.PrintInfo($"Подключение: {CliAnsiUi.Paint("obx start " + items[0].Id, CliAnsiUi.Cyan)}");
        return 0;
    }

    //подключение к vpn с реальным платформенным адаптером и живым tui-дашбордом
    private async Task<int> HandleConnectAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (!session.IsLoggedIn)
        {
            CliAnsiUi.PrintError("Вы не авторизованы. Сначала выполните: obx auth");
            return 1;
        }

        //проверяем права доступа до поднятия ядра чтобы не ловить ложное подключение
        var (hasPriv, privNote) = CheckTunnelPrivileges();
        if (!hasPriv)
        {
            if (cmd.IsJson)
            {
                await Console.Error.WriteLineAsync($"{{\"error\":\"{privNote}\"}}".AsMemory(), ct).ConfigureAwait(false);
            }
            else
            {
                var hint = OperatingSystem.IsWindows()
                    ? "Откройте PowerShell от имени администратора и выполните: obx start"
                    : "Запустите через sudo obxodka start или выдайте: sudo setcap cap_net_admin+ep $(which obxodka)";
                CliAnsiUi.RenderCard(
                    "ТРЕБУЮТСЯ ПРАВА АДМИНИСТРАТОРА",
                    [
                        ("Причина", CliAnsiUi.Paint(privNote, CliAnsiUi.Red)),
                        ("Решение", CliAnsiUi.Paint(hint, CliAnsiUi.Cyan))
                    ],
                    CliAnsiUi.Red);
            }

            return 1;
        }

        var requestedProtocol = cmd.GetOption("protocol") ?? cmd.GetOption("proto");
        string? previousProtocol = null;
        if (!string.IsNullOrWhiteSpace(requestedProtocol))
        {
            if (!ProtocolPipelineRegistry.TryParsePreferredProtocol(requestedProtocol, out var normalizedProtocol))
            {
                CliAnsiUi.PrintError($"Неизвестный протокол '{requestedProtocol}'. Доступны: auto, stream, ws, xhttp, reality");
                return 2;
            }

            previousProtocol = ProtocolPipelineRegistry.GetPreferredProtocol();
            _ = ProtocolPipelineRegistry.SetPreferredProtocol(normalizedProtocol);
        }

        using var protocolRestoreScope = previousProtocol is not null
            ? new ProtocolRestoreScope(previousProtocol)
            : null;

        ApplyConnectFlagsToPreferences(cmd);

        var (ok, servers, err) = cmd.IsJson
            ? await _apiService.GetServersAsync(ct: ct).ConfigureAwait(false)
            : await CliAnsiUi.RunWithSpinnerAsync("Поиск оптимального сервера...", () => _apiService.GetServersAsync(ct: ct), ct).ConfigureAwait(false);
        if (!ok || servers is null || servers.Count == 0)
        {
            CliAnsiUi.PrintError($"Не удалось получить серверы: {err ?? "нет доступных серверов"}");
            return 1;
        }

        var requested = cmd.GetOption("server") ?? (cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : null);
        var selected = SelectServer(servers, requested);
        if (selected is null)
        {
            CliAnsiUi.PrintError($"Сервер '{requested}' не найден. Проверьте список: obx servers");
            return 2;
        }

        using var adapterScope = CreatePlatformAdapter();
        if (adapterScope is null)
        {
            CliAnsiUi.PrintError("Текущая ОС не поддерживается для TUN-подключения.");
            return 1;
        }

        var preflightError = await adapterScope.Adapter.RunNetworkPreflightAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            CliAnsiUi.PrintError($"Проверка сети не пройдена: {preflightError}");
            return 1;
        }

        var conflict = adapterScope.Adapter.DetectConflictingVpn();
        if (!string.IsNullOrWhiteSpace(conflict) && !cmd.IsJson)
        {
            CliAnsiUi.PrintWarn($"Обнаружен сторонний VPN: {conflict}");
        }

        using var orchestrator = new VpnSessionOrchestrator(adapterScope.Adapter);
        var startedAt = DateTimeOffset.UtcNow;
        var currentState = AppVpnState.Connecting;
        long lastRxSpeed = 0;
        long lastTxSpeed = 0;
        string? lastError = null;
        var recentLogs = new Queue<string>(8);
        var logLock = new Lock();

        void PushLog(string line)
        {
            lock (logLock)
            {
                if (recentLogs.Count >= 6)
                {
                    _ = recentLogs.Dequeue();
                }

                recentLogs.Enqueue($"[{DateTime.Now:HH:mm:ss}] {line}");
            }
        }

        void PersistState()
        {
            var runtime = new CliRuntimeState(
                Environment.ProcessId,
                currentState.ToString(),
                selected.Location,
                selected.Ip,
                selected.Port,
                OctopusEngine.Current.ActiveProtocol,
                startedAt,
                OctopusEngine.Current.TotalBytesReceived,
                OctopusEngine.Current.TotalBytesSent,
                Volatile.Read(ref lastRxSpeed),
                Volatile.Read(ref lastTxSpeed),
                SmartMtuEngine.CurrentMtu,
                PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, true),
                PlatformServices.Preferences.Get(AppPreferenceKeys.UseAdblockDns, false),
                PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false));
            CliRuntimeStateStore.Save(runtime);
        }

        orchestrator.OnStateChanged += state =>
        {
            currentState = state;
            PushLog($"Состояние: {state}");
            PersistState();
        };

        orchestrator.OnTrafficUpdated += stats =>
        {
            _ = Interlocked.Exchange(ref lastRxSpeed, stats.DownloadSpeedBps);
            _ = Interlocked.Exchange(ref lastTxSpeed, stats.UploadSpeedBps);
            PersistState();
        };

        orchestrator.OnLogUpdated += PushLog;
        orchestrator.OnErrorOccurred += error =>
        {
            lastError = error;
            PushLog($"ОШИБКА: {error}");
        };
        orchestrator.OnForceLogoutRequested += reason =>
        {
            lastError = $"Требуется повторный вход: {reason}";
            PushLog(lastError);
        };

        using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            stopCts.Cancel();
        }

        Console.CancelKeyPress += OnCancelKeyPress;

        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
        {
            ctx.Cancel = true;
            stopCts.Cancel();
        });

        try
        {
            PersistState();
            var fallbacks = servers.Where(s => s.Ip != selected.Ip).ToList();

            if (cmd.IsJson)
            {
                await orchestrator.StartVpnAsync(selected.Ip, selected.Port, fallbacks).ConfigureAwait(false);
            }
            else
            {
                _ = await CliAnsiUi.RunWithSpinnerAsync(
                    $"Подключение к {selected.Location} ({selected.Ip}:{selected.Port})...",
                    async () =>
                    {
                        await orchestrator.StartVpnAsync(selected.Ip, selected.Port, fallbacks).ConfigureAwait(false);
                        return true;
                    },
                    stopCts.Token).ConfigureAwait(false);
            }

            //строгая проверка: если оркестратор не вышел в Connected — немедленно завершаем с ошибкой
            if (orchestrator.CurrentState != AppVpnState.Connected)
            {
                var reason = lastError ?? $"Не удалось установить соединение (состояние: {orchestrator.CurrentState}).";
                if (cmd.IsJson)
                {
                    await Console.Error.WriteLineAsync($"{{\"state\":\"{orchestrator.CurrentState}\",\"error\":\"{reason}\"}}".AsMemory(), ct).ConfigureAwait(false);
                }
                else
                {
                    CliAnsiUi.RenderCard(
                        "ОШИБКА ПОДКЛЮЧЕНИЯ",
                        [
                            ("Сервер", CliAnsiUi.Paint($"{selected.Location} ({selected.Ip}:{selected.Port})", CliAnsiUi.White)),
                            ("Статус", CliAnsiUi.PaintBold(orchestrator.CurrentState.ToString(), CliAnsiUi.Red)),
                            ("Детали", CliAnsiUi.Paint(reason, CliAnsiUi.Red))
                        ],
                        CliAnsiUi.Red);
                }

                await orchestrator.StopVpnAsync().ConfigureAwait(false);
                return 1;
            }

            if (cmd.IsJson)
            {
                var snap = CliRuntimeStateStore.Load();
                if (snap is not null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(snap, CliJsonContext.Default.CliRuntimeState));
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, stopCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    //штатная остановка
                }
            }
            else
            {
                await RunLiveDashboardLoopAsync(
                    selected,
                    startedAt,
                    () => currentState,
                    () => Volatile.Read(ref lastRxSpeed),
                    () => Volatile.Read(ref lastTxSpeed),
                    () =>
                    {
                        lock (logLock)
                        {
                            return [.. recentLogs];
                        }
                    },
                    stopCts).ConfigureAwait(false);
            }

            if (cmd.IsJson)
            {
                await orchestrator.StopVpnAsync().ConfigureAwait(false);
            }
            else
            {
                _ = await CliAnsiUi.RunWithSpinnerAsync(
                    "Отключение туннеля и откат системных маршрутов...",
                    async () =>
                    {
                        await orchestrator.StopVpnAsync().ConfigureAwait(false);
                        return true;
                    },
                    CancellationToken.None).ConfigureAwait(false);
                CliAnsiUi.PrintSuccess("Туннель отключён, маршруты и DNS восстановлены.");
            }

            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            CliRuntimeStateStore.Clear();
        }
    }

    //цикл живого дашборда с графиками скорости и горячими клавишами Q / L
    private static async Task RunLiveDashboardLoopAsync(
        VpnServerDto server,
        DateTimeOffset startedAt,
        Func<AppVpnState> getState,
        Func<long> getRxSpeed,
        Func<long> getTxSpeed,
        Func<List<string>> getLogs,
        CancellationTokenSource stopCts)
    {
        List<long> dlHistory = [];
        List<long> ulHistory = [];
        var showLogs = false;
        var renderedLines = 0;

        if (CliAnsiUi.IsInteractive)
        {
            Console.Write("\x1b[?25l");
        }

        try
        {
            while (!stopCts.IsCancellationRequested)
            {
                var state = getState();
                var rx = getRxSpeed();
                var tx = getTxSpeed();
                dlHistory.Add(rx);
                ulHistory.Add(tx);
                if (dlHistory.Count > 24)
                {
                    dlHistory.RemoveAt(0);
                    ulHistory.RemoveAt(0);
                }

                var ks = PlatformServices.Preferences.Get(AppPreferenceKeys.KillSwitch, true);
                var ad = PlatformServices.Preferences.Get(AppPreferenceKeys.UseAdblockDns, false);
                var v6 = PlatformServices.Preferences.Get(AppPreferenceKeys.BlockIpv6, false);

                renderedLines = CliAnsiUi.RenderLiveDashboard(
                    renderedLines,
                    state.ToString(),
                    server.Location,
                    $"{server.Ip}:{server.Port}",
                    OctopusEngine.Current.ActiveProtocol ?? "auto",
                    SmartMtuEngine.CurrentMtu,
                    DateTimeOffset.UtcNow - startedAt,
                    rx,
                    tx,
                    OctopusEngine.Current.TotalBytesReceived,
                    OctopusEngine.Current.TotalBytesSent,
                    dlHistory,
                    ulHistory,
                    ks,
                    ad,
                    v6,
                    showLogs,
                    getLogs());

                if (state is AppVpnState.Error or AppVpnState.Disconnected)
                {
                    break;
                }

                //опрос клавиатуры без блокировки
                for (var tick = 0; tick < 5 && !stopCts.IsCancellationRequested; tick++)
                {
                    if (!Console.IsInputRedirected && Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true).Key;
                        if (key is ConsoleKey.Q or ConsoleKey.Escape)
                        {
                            await stopCts.CancelAsync().ConfigureAwait(false);
                            break;
                        }

                        if (key == ConsoleKey.L)
                        {
                            showLogs = !showLogs;
                            break;
                        }
                    }

                    try
                    {
                        await Task.Delay(100, stopCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            if (CliAnsiUi.IsInteractive)
            {
                Console.Write("\x1b[?25h");
            }
        }
    }

    //отправка сигнала остановки запущенному процессу cli туннеля
    private static int HandleDisconnect(CliParsedCommand cmd)
    {
        var state = CliRuntimeStateStore.Load();
        if (state is null || !CliRuntimeStateStore.IsProcessAlive(state.ProcessId))
        {
            CliRuntimeStateStore.Clear();
            if (cmd.IsJson)
            {
                Console.WriteLine(/*lang=json,strict*/ "{\"disconnected\":false,\"message\":\"Активный туннель не найден\"}");
            }
            else
            {
                CliAnsiUi.PrintInfo("Активный VPN-туннель CLI не найден.");
            }

            return 0;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                using var killProc = Process.Start(new ProcessStartInfo("kill", ["-TERM", state.ProcessId.ToString(CultureInfo.InvariantCulture)])
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _ = killProc?.WaitForExit(3000);
            }
            else
            {
                using var proc = Process.GetProcessById(state.ProcessId);
                proc.Kill(entireProcessTree: false);
            }

            CliRuntimeStateStore.Clear();
            if (cmd.IsJson)
            {
                Console.WriteLine($"{{\"disconnected\":true,\"pid\":{state.ProcessId}}}");
            }
            else
            {
                CliAnsiUi.PrintSuccess($"Сигнал остановки отправлен процессу PID {state.ProcessId}.");
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            CliAnsiUi.PrintError($"Не удалось остановить процесс PID {state.ProcessId}: {ex.Message}");
            return 1;
        }
    }

    //просмотр состояния активного туннеля (разовый или --watch)
    private static async Task<int> HandleStatusAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var watch = cmd.HasFlag("watch");
        List<long> dlHistory = [];
        List<long> ulHistory = [];
        var prevLines = 0;

        do
        {
            var state = CliRuntimeStateStore.Load();
            var alive = state is not null && CliRuntimeStateStore.IsProcessAlive(state.ProcessId);
            if (!alive && state is not null)
            {
                CliRuntimeStateStore.Clear();
                state = null;
            }

            if (cmd.IsJson)
            {
                Console.WriteLine(state is null
                    ? /*lang=json,strict*/ "{\"state\":\"Disconnected\"}"
                    : JsonSerializer.Serialize(state, CliJsonContext.Default.CliRuntimeState));
            }
            else if (state is null)
            {
                CliAnsiUi.RenderCard(
                    "OBXODKA СТАТУС",
                    [
                        ("Состояние", CliAnsiUi.PaintBold("○ Disconnected", CliAnsiUi.Muted)),
                        ("Запуск", CliAnsiUi.Paint("obx start", CliAnsiUi.Cyan))
                    ]);
            }
            else
            {
                dlHistory.Add(state.DownloadSpeedBps);
                ulHistory.Add(state.UploadSpeedBps);
                if (dlHistory.Count > 24)
                {
                    dlHistory.RemoveAt(0);
                    ulHistory.RemoveAt(0);
                }

                prevLines = CliAnsiUi.RenderLiveDashboard(
                    watch ? prevLines : 0,
                    state.State,
                    state.ServerName ?? "—",
                    $"{state.ServerIp}:{state.ServerPort}",
                    state.Protocol ?? "auto",
                    state.Mtu,
                    DateTimeOffset.UtcNow - state.StartedAtUtc,
                    state.DownloadSpeedBps,
                    state.UploadSpeedBps,
                    state.BytesReceived,
                    state.BytesSent,
                    dlHistory,
                    ulHistory,
                    state.KillSwitch,
                    state.AdBlock,
                    state.BlockIpv6,
                    false,
                    []);
            }

            if (!watch)
            {
                break;
            }

            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        while (!ct.IsCancellationRequested);

        return 0;
    }

    //чтение и изменение настроек cli
    private static int HandleConfig(CliParsedCommand cmd)
    {
        var action = cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0].ToLowerInvariant() : "get";
        if (action == "get")
        {
            if (cmd.PositionalArgs.Count >= 2)
            {
                var name = cmd.PositionalArgs[1];
                if (string.Equals(name, "protocol", StringComparison.OrdinalIgnoreCase))
                {
                    var proto = ProtocolPipelineRegistry.GetPreferredProtocol();
                    Console.WriteLine(cmd.IsJson
                        ? $"{{\"protocol\":\"{proto}\"}}"
                        : $"  {CliAnsiUi.Paint("protocol", CliAnsiUi.Cyan)} = {CliAnsiUi.PaintBold(proto, CliAnsiUi.Green)}");
                    return 0;
                }

                if (!s_configKeys.TryGetValue(name, out var meta))
                {
                    CliAnsiUi.PrintError($"Неизвестный ключ '{name}'. Доступны: protocol, {string.Join(", ", s_configKeys.Keys)}");
                    return 2;
                }

                var val = PlatformServices.Preferences.Get(meta.Key, meta.DefaultBool);
                Console.WriteLine(cmd.IsJson
                    ? $"{{\"{name}\":{val.ToString().ToLowerInvariant()}}}"
                    : $"  {CliAnsiUi.Paint(name, CliAnsiUi.Cyan)} = {(val ? CliAnsiUi.PaintBold("true", CliAnsiUi.Green) : CliAnsiUi.Paint("false", CliAnsiUi.Muted))}");
                return 0;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string[]> rows = [];
            var currentProto = ProtocolPipelineRegistry.GetPreferredProtocol();
            map["protocol"] = currentProto;
            rows.Add(
            [
                CliAnsiUi.PaintBold("protocol", CliAnsiUi.Cyan),
                CliAnsiUi.PaintBold(currentProto, CliAnsiUi.Green)
            ]);
            foreach (var (name, meta) in s_configKeys)
            {
                var val = PlatformServices.Preferences.Get(meta.Key, meta.DefaultBool);
                map[name] = val.ToString().ToLowerInvariant();
                rows.Add(
                [
                    CliAnsiUi.PaintBold(name, CliAnsiUi.Cyan),
                    val ? CliAnsiUi.PaintBold("● вкл (true)", CliAnsiUi.Green) : CliAnsiUi.Paint("○ выкл (false)", CliAnsiUi.Muted)
                ]);
            }

            if (cmd.IsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(map, CliJsonContext.Default.DictionaryStringString));
            }
            else
            {
                CliAnsiUi.RenderTable(["ПАРАМЕТР", "ЗНАЧЕНИЕ"], rows);
            }

            return 0;
        }

        if (action == "set" && cmd.PositionalArgs.Count >= 3)
        {
            var name = cmd.PositionalArgs[1];
            var rawVal = cmd.PositionalArgs[2];
            if (string.Equals(name, "protocol", StringComparison.OrdinalIgnoreCase))
            {
                if (!ProtocolPipelineRegistry.TryParsePreferredProtocol(rawVal, out var normalizedProto))
                {
                    CliAnsiUi.PrintError($"Неизвестный протокол '{rawVal}'. Доступны: auto, stream, ws, xhttp, reality");
                    return 2;
                }

                _ = ProtocolPipelineRegistry.SetPreferredProtocol(normalizedProto);
                CliAnsiUi.PrintSuccess($"protocol = {normalizedProto}");
                return 0;
            }

            if (!s_configKeys.TryGetValue(name, out var meta))
            {
                CliAnsiUi.PrintError($"Неизвестный ключ '{name}'. Доступны: protocol, {string.Join(", ", s_configKeys.Keys)}");
                return 2;
            }

            if (!TryParseBool(rawVal, out var parsedBool))
            {
                CliAnsiUi.PrintError($"Ожидается true/false (или on/off, 1/0), получено: '{rawVal}'.");
                return 2;
            }

            if (string.Equals(name, "force-websocket", StringComparison.OrdinalIgnoreCase))
            {
                _ = ProtocolPipelineRegistry.SetPreferredProtocol(parsedBool ? "OBXODKA-WS" : ProtocolPipelineRegistry.AutoProtocolId);
            }
            else
            {
                PlatformServices.Preferences.Set(meta.Key, parsedBool);
            }

            CliAnsiUi.PrintSuccess($"{name} = {parsedBool.ToString().ToLowerInvariant()}");
            return 0;
        }

        CliAnsiUi.PrintError("Использование: obx config get [key] | obx config set <key> <value>");
        return 2;
    }

    //управление правилами раздельного туннелирования
    private static int HandleSplit(CliParsedCommand cmd)
    {
        var action = cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0].ToLowerInvariant() : "list";
        var rules = LoadSplitRules();

        if (action == "list")
        {
            if (cmd.IsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(rules, CliJsonContext.Default.ListString));
            }
            else if (rules.Count == 0)
            {
                CliAnsiUi.PrintInfo("Список исключений сплит-туннеля пуст. Добавьте: obx split add discord.exe");
            }
            else
            {
                var rows = rules.Select(static r => new[]
                {
                    CliAnsiUi.PaintBold(r, CliAnsiUi.White),
                    r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? CliAnsiUi.Paint("Приложение", CliAnsiUi.Violet)
                        : CliAnsiUi.Paint("Домен / IP", CliAnsiUi.Cyan)
                }).ToList();
                CliAnsiUi.RenderTable(["ПРАВИЛО", "ТИП"], rows);
            }

            return 0;
        }

        if (action == "add" && cmd.PositionalArgs.Count >= 2)
        {
            var entry = cmd.PositionalArgs[1].Trim();
            if (!rules.Contains(entry, StringComparer.OrdinalIgnoreCase))
            {
                rules.Add(entry);
                SaveSplitRules(rules);
            }

            CliAnsiUi.PrintSuccess($"Добавлено в исключения: {entry}");
            return 0;
        }

        if (action == "remove" && cmd.PositionalArgs.Count >= 2)
        {
            var entry = cmd.PositionalArgs[1].Trim();
            var removed = rules.RemoveAll(r => string.Equals(r, entry, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveSplitRules(rules);
                CliAnsiUi.PrintSuccess($"Удалено из исключений: {entry}");
            }
            else
            {
                CliAnsiUi.PrintWarn($"Правило '{entry}' не найдено.");
            }

            return 0;
        }

        CliAnsiUi.PrintError("Использование: obx split list | add <domain-or-exe> | remove <domain-or-exe>");
        return 2;
    }

    //реальная диагностика сети, прав, анти-отравления dns и доступности api
    private async Task<int> HandleDiagAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        async Task<CliDiagOutput> RunDiagCoreAsync()
        {
            var os = RuntimeInformation.OSDescription;
            var (hasPriv, privNote) = CheckTunnelPrivileges();
            string? conflict;
            using (var adapterScope = CreatePlatformAdapter())
            {
                conflict = adapterScope?.Adapter.DetectConflictingVpn();
            }

            string[] resolvedIps = [];
            try
            {
                var ips = await ApiService.ResolveAllHostsAntiPoisonAsync("api.obxodka.one", ct).ConfigureAwait(false);
                resolvedIps = [.. ips.Select(static ip => ip.ToString())];
            }
            catch
            {
                // ignored
            }

            var sw = Stopwatch.StartNew();
            var (apiOk, _, _) = await _apiService.GetServersAsync(ct: ct).ConfigureAwait(false);
            sw.Stop();

            return new CliDiagOutput(os, hasPriv, privNote, apiOk, sw.ElapsedMilliseconds, resolvedIps, SmartMtuEngine.CurrentMtu, conflict);
        }

        var diag = cmd.IsJson
            ? await RunDiagCoreAsync().ConfigureAwait(false)
            : await CliAnsiUi.RunWithSpinnerAsync("Диагностика сети, DNS Anti-Poison и прав туннеля...", RunDiagCoreAsync, ct).ConfigureAwait(false);

        if (cmd.IsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(diag, CliJsonContext.Default.CliDiagOutput));
            return 0;
        }

        var privText = diag.HasTunnelPrivileges
            ? CliAnsiUi.PaintBold($"✔ OK ({diag.TunnelPrivilegeNote})", CliAnsiUi.Green)
            : CliAnsiUi.PaintBold($"✖ {diag.TunnelPrivilegeNote}", CliAnsiUi.Red);
        var apiText = diag.ApiReachable
            ? CliAnsiUi.PaintBold($"✔ Доступен ({diag.ApiLatencyMs} ms)", CliAnsiUi.Green)
            : CliAnsiUi.PaintBold("✖ Недоступен", CliAnsiUi.Red);
        var dnsText = diag.ResolvedApiIps.Length > 0
            ? CliAnsiUi.Paint($"✔ {string.Join(", ", diag.ResolvedApiIps)}", CliAnsiUi.Green)
            : CliAnsiUi.Paint("✖ не удалось разрешить", CliAnsiUi.Red);
        var conflictText = string.IsNullOrWhiteSpace(diag.ConflictingVpn)
            ? CliAnsiUi.Paint("✔ не обнаружены", CliAnsiUi.Green)
            : CliAnsiUi.Paint($"▲ {diag.ConflictingVpn}", CliAnsiUi.Amber);

        CliAnsiUi.RenderCard(
            "ДИАГНОСТИКА OBXODKA",
            [
                ("Система", CliAnsiUi.Paint(diag.Os, CliAnsiUi.White)),
                ("Права туннеля", privText),
                ("API Obxodka", apiText),
                ("DNS Anti-Poison", dnsText),
                ("Smart MTU", CliAnsiUi.PaintBold($"{diag.CurrentMtu} байт", CliAnsiUi.Cyan)),
                ("Конфликты VPN", conflictText)
            ]);
        return 0;
    }

    //установка или вывод постоянного алиаса obx для всех окон powershell / bash / zsh
    private static int HandleAlias(CliParsedCommand cmd)
    {
        var exePath = Environment.ProcessPath ?? Path.Join(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "obxodka.exe" : "obxodka");
        if (OperatingSystem.IsWindows())
        {
            var snippet = $"Set-Alias -Name obx -Value \"{exePath}\"\nSet-Alias -Name obxodka -Value \"{exePath}\"";
            if (cmd.HasFlag("install"))
            {
                var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string[] profiles =
                [
                    Path.Join(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
                    Path.Join(docs, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
                ];

                foreach (var profile in profiles)
                {
                    var dir = Path.GetDirectoryName(profile);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        _ = Directory.CreateDirectory(dir);
                    }

                    var existing = File.Exists(profile) ? File.ReadAllText(profile) : string.Empty;
                    if (!existing.Contains("Set-Alias -Name obx ", StringComparison.OrdinalIgnoreCase))
                    {
                        File.AppendAllText(profile, $"\n# Obxodka CLI\n{snippet}\n", new UTF8Encoding(false));
                    }
                }

                CliAnsiUi.PrintSuccess("Алиасы 'obx' и 'obxodka' добавлены в профиль PowerShell (для обычных и админских окон).");
                CliAnsiUi.PrintInfo("Переоткройте терминал или выполните: . $PROFILE");
                return 0;
            }

            CliAnsiUi.RenderCard(
                "ПОСТОЯННЫЙ АЛИАС OBX (POWERSHELL)",
                [
                    ("Автоустановка", CliAnsiUi.PaintBold("obx alias --install", CliAnsiUi.Cyan)),
                    ("Или вручную", CliAnsiUi.Paint(snippet.Replace("\n", " ; ", StringComparison.Ordinal), CliAnsiUi.Muted))
                ]);
            return 0;
        }

        Console.WriteLine($"alias obx='{exePath}'");
        Console.WriteLine($"alias obxodka='{exePath}'");
        return 0;
    }

    //выдача скрипта автодополнения оболочки
    private static int HandleCompletion(CliParsedCommand cmd)
    {
        var shell = cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : string.Empty;
        var script = CliCompletionGenerator.Generate(shell);
        if (script is null)
        {
            CliAnsiUi.PrintError("Укажите оболочку: obx completion powershell | bash | zsh");
            return 2;
        }

        Console.WriteLine(script);
        return 0;
    }

    private static void ApplyConnectFlagsToPreferences(CliParsedCommand cmd)
    {
        if (cmd.HasFlag("kill-switch"))
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.KillSwitch, true);
        }
        else if (cmd.HasFlag("no-kill-switch"))
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.KillSwitch, false);
        }

        if (cmd.HasFlag("adblock"))
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseAdblockDns, true);
        }
        else if (cmd.HasFlag("no-adblock"))
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.UseAdblockDns, false);
        }

        if (cmd.HasFlag("block-ipv6"))
        {
            PlatformServices.Preferences.Set(AppPreferenceKeys.BlockIpv6, true);
        }

        var mtuRaw = cmd.GetOption("mtu");
        if (!string.IsNullOrWhiteSpace(mtuRaw) && int.TryParse(mtuRaw, CultureInfo.InvariantCulture, out var mtuVal))
        {
            SmartMtuEngine.CurrentMtu = SmartMtuEngine.ClampMtu(mtuVal);
        }
    }

    private static VpnServerDto? SelectServer(List<VpnServerDto> servers, string? query) =>
        string.IsNullOrWhiteSpace(query)
            ? servers.FirstOrDefault(static s => s.IsOnline) ?? servers[0]
            : servers.FirstOrDefault(s =>
                string.Equals(BuildServerSlug(s), query, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Ip, query, StringComparison.OrdinalIgnoreCase) ||
                s.Location.Contains(query, StringComparison.OrdinalIgnoreCase));

    private static async Task<int?> MeasureTcpPingMsAsync(string ip, int port, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(1500));
        using var client = new TcpClient();
        var sw = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(ip, port, timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();
            return (int)sw.ElapsedMilliseconds;
        }
        catch
        {
            return null;
        }
    }

    private static (bool HasPrivileges, string Note) CheckTunnelPrivileges()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            var isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            return (isAdmin, isAdmin ? "Администратор (Wintun)" : "Требуется запуск от имени администратора");
        }

        if (OperatingSystem.IsLinux())
        {
            var hasNetAdmin = LinuxTunNative.HasNetAdminPrivilege();
            var hasTun = File.Exists(LinuxTunNative.TunDevicePath);
            return (hasNetAdmin && hasTun, hasNetAdmin && hasTun ? "root/CAP_NET_ADMIN + /dev/net/tun" : "Требуется sudo или CAP_NET_ADMIN и /dev/net/tun");
        }

        return (false, "ОС не поддерживается");
    }

    private static AdapterScope? CreatePlatformAdapter()
    {
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
        {
            WindowsVpnService.SyncSplitTunnelPolicy = SyncWindowsSplitPolicyFromCli;
            var svc = new WindowsVpnService();
            return new AdapterScope(svc, svc);
        }

        if (OperatingSystem.IsLinux())
        {
            var svc = new LinuxVpnService();
            return new AdapterScope(svc, svc);
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private static void SyncWindowsSplitPolicyFromCli()
    {
        var rules = LoadSplitRules();
        var policy = WindowsSplitTunnelEngine.Policy;
        policy.Enabled = rules.Count > 0;
        var apps = rules.Where(static r => r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        var domains = rules.Where(static r => !r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
        policy.SetBypassedExecutables(apps);
        policy.SetCustomRules(domains);
    }

    private static List<string> LoadSplitRules()
    {
        try
        {
            if (File.Exists(CliStoragePaths.SplitRulesPath))
            {
                var json = File.ReadAllText(CliStoragePaths.SplitRulesPath);
                return JsonSerializer.Deserialize(json, CliJsonContext.Default.ListString) ?? [];
            }
        }
        catch
        {
            // ignored
        }

        return [];
    }

    private static void SaveSplitRules(List<string> rules)
    {
        var json = JsonSerializer.Serialize(rules, CliJsonContext.Default.ListString);
        File.WriteAllText(CliStoragePaths.SplitRulesPath, json, new UTF8Encoding(false));
    }

    private static bool TryParseBool(string raw, out bool result)
    {
        switch (raw.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "on" or "yes":
                result = true;
                return true;
            case "false" or "0" or "off" or "no":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }

    private static string GetVersionString() =>
        typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private static int PrintVersion()
    {
        Console.WriteLine($"  {CliAnsiUi.PaintBold("obxodka CLI", CliAnsiUi.Cyan)} v{GetVersionString()} {CliAnsiUi.Paint($"({RuntimeInformation.RuntimeIdentifier})", CliAnsiUi.Muted)}");
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        CliAnsiUi.PrintError($"Неизвестная команда: '{command}'. Выполните 'obx --help' для справки.");
        return 2;
    }

    private static void PrintHelp()
    {
        CliAnsiUi.PrintBanner(GetVersionString());
        CliAnsiUi.RenderTable(
            ["КОМАНДА", "АЛИАС", "ОПИСАНИЕ"],
            [
                [CliAnsiUi.PaintBold("login [--email] [--code]", CliAnsiUi.Cyan), CliAnsiUi.Paint("auth", CliAnsiUi.Violet), "Войти в аккаунт по email"],
                [CliAnsiUi.PaintBold("logout", CliAnsiUi.Cyan), "—", "Выйти и очистить токены"],
                [CliAnsiUi.PaintBold("whoami [--json]", CliAnsiUi.Cyan), "—", "Показать аккаунт и баланс времени"],
                [CliAnsiUi.PaintBold("servers [--ping] [--json]", CliAnsiUi.Cyan), CliAnsiUi.Paint("ls", CliAnsiUi.Violet), "Список серверов и замер пинга"],
                [CliAnsiUi.PaintBold("connect [id] [опции]", CliAnsiUi.Cyan), CliAnsiUi.Paint("start", CliAnsiUi.Violet), "Подключиться к VPN + живой TUI"],
                [CliAnsiUi.PaintBold("disconnect", CliAnsiUi.Cyan), CliAnsiUi.Paint("stop", CliAnsiUi.Violet), "Отключить активный туннель"],
                [CliAnsiUi.PaintBold("status [--watch] [--json]", CliAnsiUi.Cyan), CliAnsiUi.Paint("st", CliAnsiUi.Violet), "Состояние туннеля и графики"],
                [CliAnsiUi.PaintBold("config get | set <k> <v>", CliAnsiUi.Cyan), "—", "Настройки (kill-switch, adblock...)"],
                [CliAnsiUi.PaintBold("split list | add | remove", CliAnsiUi.Cyan), "—", "Правила раздельного туннелирования"],
                [CliAnsiUi.PaintBold("diag [--json]", CliAnsiUi.Cyan), "—", "Диагностика прав, DNS и API"],
                [CliAnsiUi.PaintBold("alias [--install]", CliAnsiUi.Cyan), "—", "Установить постоянный алиас 'obx'"],
                [CliAnsiUi.PaintBold("completion powershell|bash|zsh", CliAnsiUi.Cyan), "—", "Скрипт автодополнения оболочки"]
            ]);
        Console.WriteLine();
        CliAnsiUi.PrintInfo($"Флаги connect: {CliAnsiUi.Paint("--protocol <auto|ws|xhttp|reality>  --kill-switch  --adblock  --block-ipv6  --mtu <n>  --json", CliAnsiUi.White)}");
    }

    private sealed class AdapterScope(ITunnelPlatformAdapter adapter, IDisposable? disposable) : IDisposable
    {
        public ITunnelPlatformAdapter Adapter { get; } = adapter;
        public void Dispose() => disposable?.Dispose();
    }

    //гарантированный откат временного переопределения --protocol после завершения сессии connect
    private sealed class ProtocolRestoreScope(string previousProtocol) : IDisposable
    {
        public void Dispose() => _ = ProtocolPipelineRegistry.SetPreferredProtocol(previousProtocol);
    }
}
