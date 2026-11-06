namespace obxodka.Client.Telemetry;

//сервис сбора ошибок и отправки отчетов о падениях
public interface ITelemetryService
{
    //включена ли отправка отчетов в настройках
    public bool IsTelemetryEnabled { get; }

    //отправка лога сбоев прошлой сессии при запуске
    public ValueTask FlushPreviousSessionCrashLogAsync(CancellationToken ct = default);

    //запись ошибки в очередь отправки
    public ValueTask TrackErrorAsync(string message, Exception? ex = null, CancellationToken ct = default);

    //запись фатального падения приложения
    public ValueTask TrackFatalCrashAsync(string message, string? stackTrace = null, CancellationToken ct = default);
}
