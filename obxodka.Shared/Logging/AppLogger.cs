#pragma warning disable CA1716, CA2255

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace obxodka.Shared.Logging;

public static class AppLogger
{
    private readonly record struct LogItem(DateTime Timestamp, string Message, bool IsError);

    private static readonly Channel<LogItem> t_channel =
        Channel.CreateUnbounded<LogItem>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public static string LogFilePath { get; private set; } = string.Empty;
    public static string HtmlLogFilePath { get; private set; } = string.Empty;
    public static string BackupLogFilePath { get; private set; } = string.Empty;

    private static StreamWriter? t_txtWriter;
    private static StreamWriter? t_htmlWriter;
    private static StreamWriter? t_backupWriter;
    private static bool t_isInitialized;
    private static readonly Lock t_lock = new();

    [ModuleInitializer]
    public static void AutoInitialize() => Initialize();

    public static void Initialize()
    {
        lock (t_lock)
        {
            if (t_isInitialized)
            {
                return;
            }
            t_isInitialized = true;

            SetupPaths();
            StartBackgroundWriter();
            RegisterDiagnostics();

            Log($"[AppLogger] Session started at {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)}");
            Log($"[AppLogger] Debug txt path:  {LogFilePath}");
            Log($"[AppLogger] Debug html path: {HtmlLogFilePath}");
            if (!string.IsNullOrEmpty(BackupLogFilePath))
            {
                Log($"[AppLogger] Backup log path: {BackupLogFilePath}");
            }
        }
    }

    private static void SetupPaths()
    {
        try
        {
            var baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
            {
                LogFilePath = Path.Combine(baseDir, "debug.txt");
                HtmlLogFilePath = Path.Combine(baseDir, "debug.html");

                var fsTxt = new FileStream(LogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                t_txtWriter = new StreamWriter(fsTxt, Encoding.UTF8) { AutoFlush = true };

                var exists = File.Exists(HtmlLogFilePath);
                var fsHtml = new FileStream(HtmlLogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                t_htmlWriter = new StreamWriter(fsHtml, Encoding.UTF8) { AutoFlush = true };
                if (!exists || fsHtml.Length == 0)
                {
                    t_htmlWriter.WriteLine("<!DOCTYPE html><html><head><meta charset='utf-8'><title>Obxodka Debug Log</title>");
                    t_htmlWriter.WriteLine("<style>");
                    t_htmlWriter.WriteLine("body { background: #121212; color: #e0e0e0; font-family: 'Consolas', 'Courier New', monospace; font-size: 13px; margin: 15px; }");
                    t_htmlWriter.WriteLine(".entry { padding: 3px 8px; border-bottom: 1px solid #222; }");
                    t_htmlWriter.WriteLine(".ts { color: #888; font-weight: normal; margin-right: 8px; }");
                    t_htmlWriter.WriteLine(".info { color: #cfcfcf; }");
                    t_htmlWriter.WriteLine(".error { color: #ff3333; font-weight: bold; background: #3b0505; border-left: 6px solid #ff1111; padding: 6px 12px; margin: 4px 0; border-radius: 4px; box-shadow: 0 0 8px rgba(255,0,0,0.3); }");
                    t_htmlWriter.WriteLine("</style></head><body>");
                    t_htmlWriter.WriteLine($"<h2 style='color:#4caf50;'>Obxodka Debug Session - {DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}</h2>");
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Failed to initialize in BaseDirectory: {ex.Message}");
        }

        try
        {
            var localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "obxodka", "logs");
            _ = Directory.CreateDirectory(localAppData);
            BackupLogFilePath = Path.Combine(localAppData, "debug.txt");

            if (t_txtWriter is null)
            {
                LogFilePath = BackupLogFilePath;
                HtmlLogFilePath = Path.Combine(localAppData, "debug.html");
            }

            var fsBackup = new FileStream(BackupLogFilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            t_backupWriter = new StreamWriter(fsBackup, Encoding.UTF8) { AutoFlush = true };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AppLogger] Failed to initialize backup logger: {ex.Message}");
        }
    }

    private static void StartBackgroundWriter()
    {
        var thread = new Thread(ProcessLogQueue)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "AppLoggerWriter"
        };
        thread.Start();
    }

    private static void ProcessLogQueue()
    {
        var reader = t_channel.Reader;
        while (true)
        {
            while (reader.TryRead(out var item))
            {
                WriteToOutputs(item);
            }

            try
            {
                if (!reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }
    }

    private static void WriteToOutputs(LogItem item)
    {
        try
        {
            var ts = item.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            if (item.IsError)
            {
                var ansiErrorHeader = "\u001b[1;31m================================================================================";
                var ansiErrorMsg = $"[{ts}] >>> [ERROR] {item.Message}";
                var ansiErrorFooter = "================================================================================\u001b[0m";

                t_txtWriter?.WriteLine(ansiErrorHeader);
                t_txtWriter?.WriteLine(ansiErrorMsg);
                t_txtWriter?.WriteLine(ansiErrorFooter);

                t_backupWriter?.WriteLine(ansiErrorHeader);
                t_backupWriter?.WriteLine(ansiErrorMsg);
                t_backupWriter?.WriteLine(ansiErrorFooter);

                if (t_htmlWriter != null)
                {
                    var htmlEncoded = WebUtility.HtmlEncode(item.Message);
                    t_htmlWriter.WriteLine($"<div class='entry error'><span class='ts'>[{ts}]</span><b>[ERROR]</b> {htmlEncoded}</div>");
                }
            }
            else
            {
                var txtLine = $"[{ts}] {item.Message}";
                t_txtWriter?.WriteLine(txtLine);
                t_backupWriter?.WriteLine(txtLine);

                if (t_htmlWriter != null)
                {
                    var htmlEncoded = WebUtility.HtmlEncode(item.Message);
                    t_htmlWriter.WriteLine($"<div class='entry info'><span class='ts'>[{ts}]</span>{htmlEncoded}</div>");
                }
            }
        }
        catch { }
    }

    private static void RegisterDiagnostics()
    {
        try
        {
            _ = Trace.Listeners.Add(new AppLoggerTraceListener());

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                LogError($"[UNHANDLED DOMAIN EXCEPTION] {e.ExceptionObject}");

            TaskScheduler.UnobservedTaskException += (_, e) =>
                LogError($"[UNOBSERVED TASK EXCEPTION] {e.Exception}");
        }
        catch { }
    }

    public static void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var isError = message.Contains("[ERROR]", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains(" FATAL ", StringComparison.OrdinalIgnoreCase) ||
                      message.StartsWith("error:", StringComparison.OrdinalIgnoreCase) ||
                      message.Contains(" Ошибка:", StringComparison.OrdinalIgnoreCase);

        _ = t_channel.Writer.TryWrite(new LogItem(DateTime.Now, message, isError));
    }

    public static void LogError(string message, Exception? ex = null)
    {
        var fullMessage = ex != null
            ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}"
            : message;
        _ = t_channel.Writer.TryWrite(new LogItem(DateTime.Now, fullMessage, IsError: true));
    }

    public static void LogWarning(string message) =>
        _ = t_channel.Writer.TryWrite(new LogItem(DateTime.Now, $"[WARN] {message}", IsError: false));

    private sealed class AppLoggerTraceListener : TraceListener
    {
        public override void Write(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Log(message);
            }
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrEmpty(message))
            {
                Log(message);
            }
        }
    }
}
