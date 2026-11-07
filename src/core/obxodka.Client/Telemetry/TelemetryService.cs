namespace obxodka.Client.Telemetry;

//сервис отправки отчетов об ошибках и падениях на сервер
public sealed class TelemetryService : ITelemetryService, IDisposable
{
    public static TelemetryService Instance { get; } = new();

    private readonly Channel<TelemetryDto> _channel =
        Channel.CreateBounded<TelemetryDto>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _lastSendTicks;
    private static readonly TimeSpan s_minSendInterval = TimeSpan.FromSeconds(30);

    //включена ли отправка отчетов в настройках
    public bool IsTelemetryEnabled =>
        PlatformServices.Preferences.Get(AppPreferenceKeys.UseTelemetry, true);

    public TelemetryService() => _ = Task.Run(ProcessQueueAsync);

    //отправка лога сбоев прошлой сессии при запуске
    public async ValueTask FlushPreviousSessionCrashLogAsync(CancellationToken ct = default)
    {
        try
        {
            var crashPath = AppLogger.CrashLogFilePath;
            if (!File.Exists(crashPath))
            {
                return;
            }

            var text = await File.ReadAllTextAsync(crashPath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(text))
            {
                File.Delete(crashPath);
                return;
            }

            //если отправка отключена в настройках просто стираем файл
            if (!IsTelemetryEnabled)
            {
                File.Delete(crashPath);
                return;
            }

            //если нет сети файл отправится при следующем запуске
            if (PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
            {
                return;
            }

            //отсеиваем шум спам и отмены
            if (!TelemetryFilter.ShouldSendToServer(text))
            {
                File.Delete(crashPath);
                return;
            }

            var firstLine = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Previous Session Crash";
            var message = firstLine.Length > 1024 ? firstLine[..1024] : firstLine;
            var stackTrace = text.Length > 4096 ? text[..4096] : text;

            var hwid = DeviceHelper.Hwid;
            var appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
            var email = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, string.Empty);

            var dto = new TelemetryDto(hwid, appVersion, message, stackTrace, string.IsNullOrWhiteSpace(email) ? null : email);

            var sent = await SendToServerDirectAsync(dto, ct).ConfigureAwait(false);
            if (sent)
            {
                File.Delete(crashPath);
            }
        }
        catch
        {
            //сбой отправки не должен ломать запуск программы
        }
    }

    //запись ошибки в очередь отправки
    public async ValueTask TrackErrorAsync(string message, Exception? ex = null, CancellationToken ct = default)
    {
        if (!IsTelemetryEnabled || !TelemetryFilter.ShouldSendToServer(message, ex))
        {
            return;
        }

        var hwid = DeviceHelper.Hwid;
        var appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
        var email = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, string.Empty);

        var safeMsg = message.Length > 1024 ? message[..1024] : message;
        var stack = ex?.ToString() ?? string.Empty;
        var safeStack = stack.Length > 4096 ? stack[..4096] : stack;

        var dto = new TelemetryDto(hwid, appVersion, safeMsg, safeStack, string.IsNullOrWhiteSpace(email) ? null : email);
        _ = _channel.Writer.TryWrite(dto);
        await ValueTask.CompletedTask;
    }

    //запись фатального падения приложения в crash.log
    public async ValueTask TrackFatalCrashAsync(string message, string? stackTrace = null, CancellationToken ct = default)
    {
        try
        {
            //запись в файл crash.log на случай падения процесса
            var entry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [FATAL CRASH]\nMessage: {message}\nStackTrace:\n{stackTrace}\n----------------------------------------\n";
            await File.AppendAllTextAsync(AppLogger.CrashLogFilePath, entry, ct).ConfigureAwait(false);

            //попытка отправить сразу если есть сеть
            if (IsTelemetryEnabled && PlatformServices.Connectivity.NetworkAccess != AppNetworkAccess.None && TelemetryFilter.ShouldSendToServer(message))
            {
                var hwid = DeviceHelper.Hwid;
                var appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
                var email = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, string.Empty);

                var safeMsg = message.Length > 1024 ? message[..1024] : message;
                var safeStack = stackTrace ?? string.Empty;
                if (safeStack.Length > 4096)
                {
                    safeStack = safeStack[..4096];
                }

                var dto = new TelemetryDto(hwid, appVersion, safeMsg, safeStack, string.IsNullOrWhiteSpace(email) ? null : email);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var sent = await SendToServerDirectAsync(dto, cts.Token).ConfigureAwait(false);
                if (sent && File.Exists(AppLogger.CrashLogFilePath))
                {
                    File.Delete(AppLogger.CrashLogFilePath);
                }
            }
        }
        catch { }
    }

    //фоновая обработка очереди отправки
    private async Task ProcessQueueAsync()
    {
        var reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var dto))
            {
                if (!IsTelemetryEnabled || PlatformServices.Connectivity.NetworkAccess == AppNetworkAccess.None)
                {
                    continue;
                }

                //не шлем чаще 1 запроса в 30 секунд
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(_lastSendTicks, now);
                if (elapsed < s_minSendInterval)
                {
                    var delay = s_minSendInterval - elapsed;
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                _ = await SendToServerDirectAsync(dto, CancellationToken.None).ConfigureAwait(false);
                _lastSendTicks = Stopwatch.GetTimestamp();
            }
        }
    }

    //прямая отправка отчета по http
    private async Task<bool> SendToServerDirectAsync(TelemetryDto dto, CancellationToken ct)
    {
        //блокируем сетевую отправку телеметрии на боевой сервер из юнит тестов
        if (TelemetryFilter.IsTestEnvironment)
        {
            return false;
        }

        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var endpoint = AppConfig.ApiUrl("api/vpn/telemetry");
            using var handler = ApiService.CreateDefaultHandler();
            using var client = new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(4)
            };

            var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.TelemetryDto);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }

    //освобождение ресурсов
    public void Dispose() => _sendLock.Dispose();
}
