namespace obxodka.Client.Telemetry;

//сервис отправки отчетов об ошибках и падениях на сервер
public sealed class TelemetryService : ITelemetryService, IDisposable
{
    private static readonly TimeSpan s_minSendInterval = TimeSpan.FromSeconds(30);

    private readonly Channel<TelemetryDto> _channel =
        Channel.CreateBounded<TelemetryDto>(new BoundedChannelOptions(50)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _lastSendTicks;

    public TelemetryService() => _ = Task.Run(ProcessQueueAsync);
    public static TelemetryService Instance { get; } = new();

    //освобождение ресурсов
    public void Dispose() => _sendLock.Dispose();

    //включена ли отправка отчетов в настройках
    public bool IsTelemetryEnabled =>
        PlatformServices.Preferences.Get(AppPreferenceKeys.UseTelemetry, true);

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

            var firstLine = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ??
                            "Previous Session Crash";
            var message = firstLine.Length > 1024 ? firstLine[..1024] : firstLine;
            var stackTrace = text.Length > 4096 ? text[..4096] : text;

            var hwid = DeviceHelper.Hwid;
            var appVersion = typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? "2.0.0";
            var email = PlatformServices.Preferences.Get(AppPreferenceKeys.UserEmail, string.Empty);

            var dto = new TelemetryDto(hwid, appVersion, message, stackTrace, string.IsNullOrWhiteSpace(email) ? null : email,
                AppConfig.EnvironmentName);

            var sent = await SendToServerDirectAsync(dto, ct).ConfigureAwait(false);
            if (sent)
            {
                File.Delete(crashPath);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            //сбой отправки не должен ломать запуск программы
        }
    }

    //запись ошибки в очередь отправки
    public ValueTask TrackErrorAsync(string message, Exception? ex = null, CancellationToken ct = default)
    {
        _ = ct;
        EnqueueError(message, ex);
        return ValueTask.CompletedTask;
    }

    //запись фатального падения приложения в crash.log
    public async ValueTask TrackFatalCrashAsync(string message, string? stackTrace = null, CancellationToken ct = default)
    {
        try
        {
            //запись в файл crash.log на случай падения процесса
            var entry =
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [FATAL CRASH]\nMessage: {message}\nStackTrace:\n{stackTrace}\n----------------------------------------\n";
            await File.AppendAllTextAsync(AppLogger.CrashLogFilePath, entry, ct).ConfigureAwait(false);

            //попытка отправить сразу если есть сеть
            if (IsTelemetryEnabled && PlatformServices.Connectivity.NetworkAccess != AppNetworkAccess.None &&
                TelemetryFilter.ShouldSendToServer(message))
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

                var dto = new TelemetryDto(hwid, appVersion, safeMsg, safeStack, string.IsNullOrWhiteSpace(email) ? null : email,
                    AppConfig.EnvironmentName);
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var sent = await SendToServerDirectAsync(dto, cts.Token).ConfigureAwait(false);
                if (sent && File.Exists(AppLogger.CrashLogFilePath))
                {
                    File.Delete(AppLogger.CrashLogFilePath);
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            //подавление ошибки отправки сохраненного аварийного лога
        }
    }

    //запись ошибки в очередь отправки без аллокаций valuetask
    internal void EnqueueError(string message, Exception? ex = null)
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

        var dto = new TelemetryDto(hwid, appVersion, safeMsg, safeStack, string.IsNullOrWhiteSpace(email) ? null : email,
            AppConfig.EnvironmentName);
        _ = _channel.Writer.TryWrite(dto);
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

    //прямая отправка отчета через единый сервис api с токеном и обходом dpi
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
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(6));
            return await ApiService.Shared.PostTelemetryAsync(dto, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException or TimeoutException
                                       or OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            _ = _sendLock.Release();
        }
    }
}
