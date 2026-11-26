namespace obxodka.Client.Telemetry;

//сервис сбора ошибок и отправки отчетов о падениях
public interface ITelemetryService
{
    //включена ли отправка отчетов в настройках
    bool IsTelemetryEnabled { get; }

    //отправка лога сбоев прошлой сессии при запуске
    ValueTask FlushPreviousSessionCrashLogAsync(CancellationToken ct = default);

    //запись ошибки в очередь отправки
    ValueTask TrackErrorAsync(string message, Exception? ex = null, CancellationToken ct = default);

    //запись фатального падения приложения
    ValueTask TrackFatalCrashAsync(string message, string? stackTrace = null, CancellationToken ct = default);
}
