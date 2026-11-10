namespace obxodka.Shared.Logging;

//запись логов в один файл client.log с лимитом 1 мб
public static class AppLogger
{
    private static readonly Channel<LogItem> s_channel =
        Channel.CreateBounded<LogItem>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    private static readonly Lock s_lock = new();
    private static readonly List<string> s_recentLogs = new(250);
    private static StreamWriter? s_writer;
    private static bool s_isInitialized;
    private static long s_currentFileBytes;

    private const long MaxLogFileBytes = 1 * 1024 * 1024; //1 мб максимум на диске

    public static string LogFilePath { get; private set; } = string.Empty;
    public static string CrashLogFilePath { get; private set; } = string.Empty;

    //старые свойства для совместимости
    [Obsolete("Используйте LogFilePath. HTML-логи ликвидированы.")]
    public static string HtmlLogFilePath => string.Empty;

    [Obsolete("Используйте LogFilePath. Дублирующие бэкап-логи ликвидированы.")]
    public static string BackupLogFilePath => LogFilePath;

    private readonly record struct LogItem(DateTime Timestamp, string Level, string Message);

    static AppLogger() => Initialize();

    public static void AutoInitialize() => Initialize();

    public static void Initialize()
    {
        lock (s_lock)
        {
            if (s_isInitialized)
            {
                return;
            }
            s_isInitialized = true;

            SetupPaths();
            _ = Task.Run(ProcessLogQueueAsync);
            RegisterGlobalExceptionHooks();
        }
    }

    private static void SetupPaths()
    {
        try
        {
            var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "obxodka", "logs");
            _ = Directory.CreateDirectory(logDir);

            LogFilePath = Path.Combine(logDir, "client.log");
            CrashLogFilePath = Path.Combine(logDir, "crash.log");

            //проверка размера файла и чистка если больше 1 мб
            if (File.Exists(LogFilePath))
            {
                var fi = new FileInfo(LogFilePath);
                if (fi.Length > MaxLogFileBytes)
                {
                    File.Delete(LogFilePath);
                }
            }

            var fs = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            s_currentFileBytes = fs.Length;
            s_writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };

            LogPass($"[SESSION] Новая сессия запущена: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[AppLogger] Ошибка инициализации логгера: {ex.Message}");
        }
    }

    //фоновая обработка очереди записей
    private static async Task ProcessLogQueueAsync()
    {
        var reader = s_channel.Reader;
        var flushCounter = 0;

        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                WriteEntry(item);
                flushCounter++;
            }

            if (flushCounter > 0)
            {
                try
                {
                    if (s_writer != null)
                    {
                        await s_writer.FlushAsync().ConfigureAwait(false);
                    }
                }
                catch { }
                flushCounter = 0;
            }
        }
    }

    //запись строки в файл логов
    private static void WriteEntry(LogItem item)
    {
        try
        {
            var line = $"[{item.Timestamp:HH:mm:ss.fff}] [{item.Level}] {item.Message}";

            if (s_writer != null)
            {
                s_writer.WriteLine(line);
                s_currentFileBytes += Encoding.UTF8.GetByteCount(line) + 2;

                //сброс буфера если размер файла превысил 1 мб
                if (s_currentFileBytes > MaxLogFileBytes)
                {
                    s_writer.Flush();
                    s_writer.Dispose();
                    try
                    {
                        if (File.Exists(LogFilePath))
                        {
                            File.Delete(LogFilePath);
                        }
                        var fs = new FileStream(LogFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
                        s_currentFileBytes = 0;
                        s_writer = new StreamWriter(fs, Encoding.UTF8) { AutoFlush = false };
                    }
                    catch { }
                }
            }

        }
        catch { }
    }

    //добавление в кольцевой буфер последних логов для экрана
    private static void AddToRecentLogs(string line)
    {
        lock (s_lock)
        {
            if (s_recentLogs.Count >= 250)
            {
                s_recentLogs.RemoveAt(0);
            }
            s_recentLogs.Add(line);
        }
    }

    //запись обычной строки в лог
    public static void Log(string message) => LogPass(message);

    //запись успешных операций
    public static void LogPass(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var item = new LogItem(DateTime.Now, "PASS", message);
        AddToRecentLogs($"[{item.Timestamp:HH:mm:ss.fff}] [PASS] {message}");
        _ = s_channel.Writer.TryWrite(item);
    }

    //запись отладочных сообщений
    public static void LogDebug(string message, Exception? ex = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var item = new LogItem(DateTime.Now, "DEBUG", ex != null ? $"{message} | {ex.Message}" : message);
        AddToRecentLogs($"[{item.Timestamp:HH:mm:ss.fff}] [DEBUG] {item.Message}");
        _ = s_channel.Writer.TryWrite(item);
    }

    //запись предупреждений
    public static void LogWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var item = new LogItem(DateTime.Now, "WARN", message);
        AddToRecentLogs($"[{item.Timestamp:HH:mm:ss.fff}] [WARN] {message}");
        _ = s_channel.Writer.TryWrite(item);
    }

    //запись ошибок в журнал логов
    public static void LogError(string message, Exception? ex = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var fullMessage = ex != null
            ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
            : message;

        var item = new LogItem(DateTime.Now, "ERROR", fullMessage);
        AddToRecentLogs($"[{item.Timestamp:HH:mm:ss.fff}] [ERROR] {fullMessage}");
        _ = s_channel.Writer.TryWrite(item);
    }

    //запись фатального падения процесса в crash.log
    public static void LogCrash(string message, Exception? ex = null)
    {
        LogError(message, ex);
        try
        {
            var fullMessage = ex != null
                ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
                : message;
            var crashEntry = $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff UTC}] [FATAL CRASH] {fullMessage}\n";
            File.AppendAllText(CrashLogFilePath, crashEntry);
        }
        catch { }
    }

    //получение последних строк для интерфейса
    public static string GetRecentLogs(int maxLines = 150)
    {
        lock (s_lock)
        {
            if (s_recentLogs.Count == 0)
            {
                return "Журнал логов пока пуст.";
            }

            var start = Math.Max(0, s_recentLogs.Count - maxLines);
            var count = s_recentLogs.Count - start;
            return string.Join(Environment.NewLine, s_recentLogs.Skip(start).Take(count));
        }
    }

    //принудительный сброс буфера на диск
    public static void FlushRecentLogsToFile()
    {
        try
        {
            s_writer?.Flush();
        }
        catch { }
    }

    //перехват необработанных сбоев приложения
    private static void RegisterGlobalExceptionHooks()
    {
        try
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                var ex = e.ExceptionObject as Exception;
                LogCrash($"[UNHANDLED DOMAIN EXCEPTION] {ex?.Message ?? e.ExceptionObject?.ToString()}", ex);
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
                LogCrash($"[UNOBSERVED TASK EXCEPTION] {e.Exception.Message}", e.Exception);
        }
        catch { }
    }

    //открытие файла лога в системе
    public static void OpenLogFile()
    {
        try
        {
            FlushRecentLogsToFile();
            if (!string.IsNullOrEmpty(LogFilePath) && File.Exists(LogFilePath) && OperatingSystem.IsWindows())
            {
                _ = Process.Start(new ProcessStartInfo
                {
                    FileName = LogFilePath,
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }
}
