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
            "completion" => HandleCompletion(parsed),
            _ => PrintUnknownCommand(parsed.Command)
        };
    }

    //вход по одноразовому коду на email
    private async Task<int> HandleLoginAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var email = cmd.GetOption("email") ?? (cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : null);
        if (string.IsNullOrWhiteSpace(email))
        {
            Console.Write("Email: ");
            email = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(email))
        {
            await Console.Error.WriteLineAsync("Ошибка: укажите email (например: obxodka auth --email user@example.com).".AsMemory(), ct).ConfigureAwait(false);
            return 2;
        }

        var code = cmd.GetOption("code");
        if (string.IsNullOrWhiteSpace(code))
        {
            var (sent, sendErr) = await _apiService.RequestCodeAsync(new EmailAuthRequest(email), ct).ConfigureAwait(false);
            if (!sent)
            {
                await Console.Error.WriteLineAsync($"Не удалось отправить код: {sendErr ?? "ошибка сети"}".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            Console.WriteLine($"Код подтверждения отправлен на {email}.");
            Console.Write("Введите код из письма: ");
            code = Console.ReadLine()?.Trim();
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            await Console.Error.WriteLineAsync("Ошибка: код подтверждения не введён.".AsMemory(), ct).ConfigureAwait(false);
            return 2;
        }

        var req = new EmailVerifyRequest(email, code, PlatformServices.DeviceInfo.DeviceId, PlatformServices.DeviceInfo.Name);
        var (ok, data, verifyErr) = await _apiService.VerifyCodeAsync(req, ct).ConfigureAwait(false);
        if (!ok || data is null)
        {
            await Console.Error.WriteLineAsync($"Авторизация не удалась: {verifyErr ?? "неверный код"}".AsMemory(), ct).ConfigureAwait(false);
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
            var output = new CliWhoAmIOutput(true, session.Email, session.SubscriptionUntil, session.BalanceSeconds);
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliWhoAmIOutput));
        }
        else
        {
            Console.WriteLine($"Вход выполнен: {session.Email}");
        }

        return 0;
    }

    //выход из аккаунта и очистка локальных токенов
    private static async Task<int> HandleLogoutAsync(CliParsedCommand cmd)
    {
        await AuthManager.ClearSessionAsync().ConfigureAwait(false);
        if (cmd.IsJson)
        {
            Console.WriteLine("{\"loggedIn\":false}");
        }
        else
        {
            Console.WriteLine("Сессия очищена.");
        }

        return 0;
    }

    //отображение текущего пользователя и статуса подписки
    private async Task<int> HandleWhoAmIAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (!session.IsLoggedIn)
        {
            if (cmd.IsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(new CliWhoAmIOutput(false, null, null, 0), CliJsonContext.Default.CliWhoAmIOutput));
            }
            else
            {
                Console.WriteLine("Вы не авторизованы. Выполните: obxodka auth");
            }

            return 1;
        }

        var (ok, profile, _) = await _apiService.GetProfileAsync(ct: ct).ConfigureAwait(false);
        var subUntil = ok && profile is not null ? profile.SubscriptionUntil : session.SubscriptionUntil;
        var balanceSec = ok && profile is not null ? profile.BalanceSeconds : session.BalanceSeconds;

        if (cmd.IsJson)
        {
            var output = new CliWhoAmIOutput(true, session.Email, subUntil, balanceSec);
            Console.WriteLine(JsonSerializer.Serialize(output, CliJsonContext.Default.CliWhoAmIOutput));
            return 0;
        }

        Console.WriteLine($"Аккаунт:   {session.Email}");
        Console.WriteLine($"Подписка:  {(subUntil.HasValue ? subUntil.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) : "нет")}");
        Console.WriteLine($"Баланс:    {TimeSpan.FromSeconds(Math.Max(0, balanceSec)).TotalHours:F1} ч");
        return 0;
    }

    //получение списка серверов с опциональным tcp пингом
    private async Task<int> HandleServersAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var (ok, servers, err) = await _apiService.GetServersAsync(ct: ct).ConfigureAwait(false);
        if (!ok || servers is null || servers.Count == 0)
        {
            await Console.Error.WriteLineAsync($"Не удалось получить список серверов: {err ?? "список пуст"}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var doPing = cmd.HasFlag("ping");
        List<CliServerOutput> items = [];
        foreach (var s in servers)
        {
            var rtt = doPing ? await MeasureTcpPingMsAsync(s.Ip, s.Port, ct).ConfigureAwait(false) : null;
            var id = BuildServerSlug(s);
            items.Add(new CliServerOutput(id, s.Location, s.Location, s.Ip, s.Port, rtt));
        }

        if (cmd.IsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(items, CliJsonContext.Default.ListCliServerOutput));
            return 0;
        }

        Console.WriteLine($"{"ID",-18} {"ЛОКАЦИЯ",-22} {"АДРЕС",-22} {(doPing ? "ПИНГ" : "ПОРТ")}");
        Console.WriteLine(new string('-', 74));
        foreach (var item in items)
        {
            var tail = doPing ? item.PingMs.HasValue ? $"{item.PingMs.Value} ms" : "timeout" : item.Port.ToString(CultureInfo.InvariantCulture);
            Console.WriteLine($"{item.Id,-18} {item.Location,-22} {$"{item.Ip}:{item.Port}",-22} {tail}");
        }

        return 0;
    }

    //подключение к vpn с реальным платформенным адаптером (wintun / linux tun)
    private async Task<int> HandleConnectAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var session = await AuthManager.LoadSessionAsync().ConfigureAwait(false);
        if (!session.IsLoggedIn)
        {
            await Console.Error.WriteLineAsync("Вы не авторизованы. Сначала выполните: obxodka auth".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        ApplyConnectFlagsToPreferences(cmd);

        var (ok, servers, err) = await _apiService.GetServersAsync(ct: ct).ConfigureAwait(false);
        if (!ok || servers is null || servers.Count == 0)
        {
            await Console.Error.WriteLineAsync($"Не удалось получить серверы: {err ?? "нет доступных серверов"}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var requested = cmd.GetOption("server") ?? (cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : null);
        var selected = SelectServer(servers, requested);
        if (selected is null)
        {
            await Console.Error.WriteLineAsync($"Сервер '{requested}' не найден. Проверьте список через: obxodka servers".AsMemory(), ct).ConfigureAwait(false);
            return 2;
        }

        using var adapterScope = CreatePlatformAdapter();
        if (adapterScope is null)
        {
            await Console.Error.WriteLineAsync("Текущая ОС не поддерживается для TUN-подключения.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var preflightError = await adapterScope.Adapter.RunNetworkPreflightAsync().ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(preflightError))
        {
            await Console.Error.WriteLineAsync($"Проверка сети не пройдена: {preflightError}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var conflict = adapterScope.Adapter.DetectConflictingVpn();
        if (!string.IsNullOrWhiteSpace(conflict))
        {
            await Console.Error.WriteLineAsync($"Предупреждение: {conflict}".AsMemory(), ct).ConfigureAwait(false);
        }

        using var orchestrator = new VpnSessionOrchestrator(adapterScope.Adapter);
        var startedAt = DateTimeOffset.UtcNow;
        var currentState = AppVpnState.Connecting;
        long lastRxSpeed = 0;
        long lastTxSpeed = 0;

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
                0,
                0,
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
            PersistState();
            if (!cmd.IsJson)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Состояние: {state}");
            }
        };

        orchestrator.OnTrafficUpdated += stats =>
        {
            _ = Interlocked.Exchange(ref lastRxSpeed, stats.DownloadSpeedBps);
            _ = Interlocked.Exchange(ref lastTxSpeed, stats.UploadSpeedBps);
            PersistState();
        };

        orchestrator.OnErrorOccurred += error => Console.Error.WriteLine($"[ОШИБКА] {error}");
        orchestrator.OnForceLogoutRequested += reason => Console.Error.WriteLine($"[СЕССИЯ] Требуется повторный вход: {reason}");

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
            if (!cmd.IsJson)
            {
                Console.WriteLine($"Подключение к {selected.Location} ({selected.Ip}:{selected.Port})...");
            }

            PersistState();
            var fallbacks = servers.Where(s => s.Ip != selected.Ip).ToList();
            await orchestrator.StartVpnAsync(selected.Ip, selected.Port, fallbacks).ConfigureAwait(false);

            if (cmd.IsJson)
            {
                var snap = CliRuntimeStateStore.Load();
                if (snap is not null)
                {
                    Console.WriteLine(JsonSerializer.Serialize(snap, CliJsonContext.Default.CliRuntimeState));
                }
            }
            else
            {
                Console.WriteLine("Туннель активен. Нажмите Ctrl+C или выполните 'obxodka disconnect' для отключения.");
            }

            try
            {
                await Task.Delay(Timeout.Infinite, stopCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                //штатная остановка по сигналу пользователя или системы
            }

            if (!cmd.IsJson)
            {
                Console.WriteLine("Отключение туннеля и откат маршрутов...");
            }

            await orchestrator.StopVpnAsync().ConfigureAwait(false);
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            CliRuntimeStateStore.Clear();
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
                Console.WriteLine("{\"disconnected\":false,\"message\":\"Активный туннель не найден\"}");
            }
            else
            {
                Console.WriteLine("Активный VPN-туннель CLI не найден.");
            }

            return 0;
        }

        try
        {
            if (!OperatingSystem.IsWindows())
            {
                //на linux отправляем мягкий sigterm чтобы процесс успел откатить маршруты и resolv.conf
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
                Console.WriteLine($"Сигнал остановки отправлен процессу PID {state.ProcessId}.");
            }

            return 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Не удалось остановить процесс PID {state.ProcessId}: {ex.Message}");
            return 1;
        }
    }

    //просмотр состояния активного туннеля (разовый или --watch)
    private static async Task<int> HandleStatusAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var watch = cmd.HasFlag("watch");
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
                if (state is null)
                {
                    Console.WriteLine("{\"state\":\"Disconnected\"}");
                }
                else
                {
                    Console.WriteLine(JsonSerializer.Serialize(state, CliJsonContext.Default.CliRuntimeState));
                }
            }
            else
            {
                if (watch)
                {
                    Console.Clear();
                }

                if (state is null)
                {
                    Console.WriteLine("Статус: Disconnected");
                }
                else
                {
                    var uptime = DateTimeOffset.UtcNow - state.StartedAtUtc;
                    Console.WriteLine($"Статус:     {state.State} (PID {state.ProcessId})");
                    Console.WriteLine($"Сервер:     {state.ServerName} ({state.ServerIp}:{state.ServerPort})");
                    Console.WriteLine($"Протокол:   {state.Protocol ?? "auto"} | MTU: {state.Mtu}");
                    Console.WriteLine($"Время:      {uptime:hh\\:mm\\:ss}");
                    Console.WriteLine($"Скорость:   DL {FormatSpeed(state.DownloadSpeedBps)} | UL {FormatSpeed(state.UploadSpeedBps)}");
                    Console.WriteLine($"Опции:      KillSwitch={state.KillSwitch}, AdBlock={state.AdBlock}, BlockIPv6={state.BlockIpv6}");
                }
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
                if (!s_configKeys.TryGetValue(name, out var meta))
                {
                    Console.Error.WriteLine($"Неизвестный ключ '{name}'. Доступны: {string.Join(", ", s_configKeys.Keys)}");
                    return 2;
                }

                var val = PlatformServices.Preferences.Get(meta.Key, meta.DefaultBool);
                Console.WriteLine(cmd.IsJson ? $"{{\"{name}\":{val.ToString().ToLowerInvariant()}}}" : $"{name} = {val}");
                return 0;
            }

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, meta) in s_configKeys)
            {
                var val = PlatformServices.Preferences.Get(meta.Key, meta.DefaultBool);
                map[name] = val.ToString().ToLowerInvariant();
            }

            if (cmd.IsJson)
            {
                Console.WriteLine(JsonSerializer.Serialize(map, CliJsonContext.Default.DictionaryStringString));
            }
            else
            {
                foreach (var (k, v) in map)
                {
                    Console.WriteLine($"{k,-18} = {v}");
                }
            }

            return 0;
        }

        if (action == "set" && cmd.PositionalArgs.Count >= 3)
        {
            var name = cmd.PositionalArgs[1];
            var rawVal = cmd.PositionalArgs[2];
            if (!s_configKeys.TryGetValue(name, out var meta))
            {
                Console.Error.WriteLine($"Неизвестный ключ '{name}'. Доступны: {string.Join(", ", s_configKeys.Keys)}");
                return 2;
            }

            if (!TryParseBool(rawVal, out var parsedBool))
            {
                Console.Error.WriteLine($"Ожидается true/false (или on/off, 1/0), получено: '{rawVal}'.");
                return 2;
            }

            PlatformServices.Preferences.Set(meta.Key, parsedBool);
            Console.WriteLine($"{name} = {parsedBool}");
            return 0;
        }

        Console.Error.WriteLine("Использование: obxodka config get [key] | obxodka config set <key> <true|false>");
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
                Console.WriteLine("Список исключений сплит-туннеля пуст.");
            }
            else
            {
                foreach (var r in rules)
                {
                    Console.WriteLine($"  - {r}");
                }
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

            Console.WriteLine($"Добавлено в исключения: {entry}");
            return 0;
        }

        if (action == "remove" && cmd.PositionalArgs.Count >= 2)
        {
            var entry = cmd.PositionalArgs[1].Trim();
            var removed = rules.RemoveAll(r => string.Equals(r, entry, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                SaveSplitRules(rules);
            }

            Console.WriteLine(removed ? $"Удалено из исключений: {entry}" : $"Правило '{entry}' не найдено.");
            return 0;
        }

        Console.Error.WriteLine("Использование: obxodka split list | add <domain-or-exe> | remove <domain-or-exe>");
        return 2;
    }

    //реальная диагностика сети, прав, анти-отравления dns и доступности api
    private async Task<int> HandleDiagAsync(CliParsedCommand cmd, CancellationToken ct)
    {
        var os = RuntimeInformation.OSDescription;
        var (hasPriv, privNote) = CheckTunnelPrivileges();
        string? conflict = null;
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

        var diag = new CliDiagOutput(
            os,
            hasPriv,
            privNote,
            apiOk,
            sw.ElapsedMilliseconds,
            resolvedIps,
            SmartMtuEngine.CurrentMtu,
            conflict);

        if (cmd.IsJson)
        {
            Console.WriteLine(JsonSerializer.Serialize(diag, CliJsonContext.Default.CliDiagOutput));
            return 0;
        }

        Console.WriteLine($"ОС:              {diag.Os}");
        Console.WriteLine($"Права туннеля:   {(diag.HasTunnelPrivileges ? "OK" : "НЕДОСТАТОЧНО")} ({diag.TunnelPrivilegeNote})");
        Console.WriteLine($"API доступен:    {(diag.ApiReachable ? $"OK ({diag.ApiLatencyMs} ms)" : "НЕДОСТУПЕН")}");
        Console.WriteLine($"DNS Anti-Poison: {(diag.ResolvedApiIps.Length > 0 ? string.Join(", ", diag.ResolvedApiIps) : "не удалось разрешить")}");
        Console.WriteLine($"Smart MTU:       {diag.CurrentMtu} байт");
        Console.WriteLine($"Конфликты VPN:   {diag.ConflictingVpn ?? "не обнаружены"}");
        return 0;
    }

    //выдача скрипта автодополнения оболочки
    private static int HandleCompletion(CliParsedCommand cmd)
    {
        var shell = cmd.PositionalArgs.Count > 0 ? cmd.PositionalArgs[0] : string.Empty;
        var script = CliCompletionGenerator.Generate(shell);
        if (script is null)
        {
            Console.Error.WriteLine("Укажите оболочку: obxodka completion powershell | bash | zsh");
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

    private static string BuildServerSlug(VpnServerDto server)
    {
        var clean = new string([.. server.Location.ToLowerInvariant().Where(static c => char.IsAsciiLetterOrDigit(c) || c == '-')]);
        return string.IsNullOrWhiteSpace(clean) ? server.Ip : clean;
    }

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

    private static string FormatSpeed(long bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s",
        >= 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        _ => $"{bytesPerSec} B/s"
    };

    private static int PrintVersion()
    {
        var ver = typeof(CliApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        Console.WriteLine($"obxodka CLI v{ver} ({RuntimeInformation.RuntimeIdentifier})");
        return 0;
    }

    private static int PrintUnknownCommand(string command)
    {
        Console.Error.WriteLine($"Неизвестная команда: '{command}'. Выполните 'obxodka --help' для справки.");
        return 2;
    }

    private static void PrintHelp() =>
        Console.WriteLine(
            """
            Obxodka CLI — консольный VPN-клиент (Windows Wintun / Linux /dev/net/tun)

            Использование:
              obxodka <команда> [аргументы] [опции]

            Команды (и короткие алиасы):
              login | auth [--email <e>] [--code <c>]    Войти в аккаунт
              logout                                     Выйти и очистить токены
              whoami [--json]                            Показать аккаунт и подписку
              servers | ls [--ping] [--json]             Список серверов
              connect | start [server] [опции]           Подключиться к VPN
                --kill-switch / --no-kill-switch         Включить/выключить Kill Switch
                --adblock / --no-adblock                 Включить/выключить DNS AdBlock
                --block-ipv6                             Блокировать IPv6 вне туннеля
                --mtu <n>                                Задать MTU вручную
              disconnect | stop                          Отключить активный туннель
              status | st [--watch] [--json]             Состояние туннеля и скорость
              config get [key] | set <key> <true|false>  Управление настройками
              split list | add <x> | remove <x>          Правила раздельного туннелирования
              diag [--json]                              Диагностика сети, прав, DNS и API
              completion powershell|bash|zsh             Скрипт автодополнения оболочки
              version                                    Показать версию CLI
            """);

    private sealed class AdapterScope(ITunnelPlatformAdapter adapter, IDisposable? disposable) : IDisposable
    {
        public ITunnelPlatformAdapter Adapter { get; } = adapter;
        public void Dispose() => disposable?.Dispose();
    }
}
