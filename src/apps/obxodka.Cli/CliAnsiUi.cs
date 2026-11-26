using System.Text.RegularExpressions;

namespace obxodka.Cli;

//движок красивого ansi-интерфейса в терминале (truecolor, спиннеры, таблицы, карточки, sparkline)
internal static partial class CliAnsiUi
{
    private const string Esc = "\x1b[";
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[2m";

    //палитра fluent 2 / obxodka (24-bit truecolor)
    public const string Cyan = "\x1b[38;2;96;205;255m";     // #60CDFF
    public const string Blue = "\x1b[38;2;59;130;246m";     // #3B82F6
    public const string Violet = "\x1b[38;2;167;139;250m";  // #A78BFA
    public const string Green = "\x1b[38;2;52;211;153m";    // #34D399
    public const string Amber = "\x1b[38;2;251;191;36m";    // #FBBF24
    public const string Red = "\x1b[38;2;248;113;113m";     // #F87171
    public const string Muted = "\x1b[38;2;148;163;184m";   // #94A3B8
    public const string Border = "\x1b[38;2;71;85;105m";    // #475569
    public const string White = "\x1b[38;2;241;245;249m";   // #F1F5F9

    private static readonly string[] s_spinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly char[] s_sparkBlocks = ['▁', '▂', '▃', '▄', '▅', '▆', '▇', '█'];

    private static bool s_initialized;
    private static bool s_ansiEnabled;

    public static bool IsInteractive => !Console.IsOutputRedirected && s_ansiEnabled;

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiEscapeRegex();

    //инициализация utf-8 и виртуального терминала windows (truecolor ansi)
    public static void Initialize()
    {
        if (s_initialized)
        {
            return;
        }

        s_initialized = true;
        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
            Console.InputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // ignored
        }

        var noColor = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
        if (noColor || Console.IsOutputRedirected)
        {
            s_ansiEnabled = false;
            return;
        }

        s_ansiEnabled = !OperatingSystem.IsWindows() || EnableWindowsVirtualTerminal();
    }

    public static string Paint(string text, string color) =>
        s_ansiEnabled ? $"{color}{text}{Reset}" : text;

    public static string PaintBold(string text, string color) =>
        s_ansiEnabled ? $"{Bold}{color}{text}{Reset}" : text;

    //градиентная раскраска строки между двумя rgb цветами
    public static string Gradient(string text, (int R, int G, int B) from, (int R, int G, int B) to, bool bold = true)
    {
        if (!s_ansiEnabled || text.Length == 0)
        {
            return text;
        }

        var sb = new StringBuilder(text.Length * 24);
        if (bold)
        {
            _ = sb.Append(Bold);
        }

        var max = Math.Max(1, text.Length - 1);
        for (var i = 0; i < text.Length; i++)
        {
            var t = (double)i / max;
            var r = (int)Math.Round(from.R + (to.R - from.R) * t);
            var g = (int)Math.Round(from.G + (to.G - from.G) * t);
            var b = (int)Math.Round(from.B + (to.B - from.B) * t);
            _ = sb.Append(CultureInfo.InvariantCulture, $"{Esc}38;2;{r};{g};{b}m{text[i]}");
        }

        _ = sb.Append(Reset);
        return sb.ToString();
    }

    //фирменный компактный заголовок obxodka с градиентом
    public static void PrintBanner(string version)
    {
        if (!s_ansiEnabled)
        {
            Console.WriteLine($"OBXODKA CLI v{version}");
            Console.WriteLine();
            return;
        }

        string[] logo =
        [
            "  ██████╗ ██████╗ ██╗  ██╗ ██████╗ ██████╗ ██╗  ██╗ █████╗ ",
            " ██╔═══██╗██╔══██╗╚██╗██╔╝██╔═══██╗██╔══██╗██║ ██╔╝██╔══██╗",
            " ██║   ██║██████╔╝ ╚███╔╝ ██║   ██║██║  ██║█████╔╝ ███████║",
            " ██║   ██║██╔══██╗ ██╔██╗ ██║   ██║██║  ██║██╔═██╗ ██╔══██║",
            " ╚██████╔╝██████╔╝██╔╝ ██╗╚██████╔╝██████╔╝██║  ██╗██║  ██║",
            "  ╚═════╝ ╚═════╝ ╚═╝  ╚═╝ ╚═════╝ ╚═════╝ ╚═╝  ╚═╝╚═╝  ╚═╝"
        ];

        Console.WriteLine();
        foreach (var line in logo)
        {
            Console.WriteLine(Gradient(line, (96, 205, 255), (167, 139, 250), bold: true));
        }

        var badge = $"  {Paint("●", Green)} {PaintBold($"v{version}", White)}  {Paint("│", Border)}  {Paint("Windows Wintun / Linux TUN", Muted)}  {Paint("│", Border)}  {Paint("Octopus Stealth Core", Cyan)}";
        Console.WriteLine(badge);
        Console.WriteLine();
    }

    public static void PrintSuccess(string message) =>
        Console.WriteLine($"  {PaintBold("✔", Green)} {Paint(message, White)}");

    public static void PrintWarn(string message) =>
        Console.WriteLine($"  {PaintBold("▲", Amber)} {Paint(message, Amber)}");

    public static void PrintError(string message) =>
        Console.Error.WriteLine($"  {PaintBold("✖", Red)} {Paint(message, Red)}");

    public static void PrintInfo(string message) =>
        Console.WriteLine($"  {PaintBold("ℹ", Cyan)} {Paint(message, Muted)}");

    //запуск асинхронной операции с анимированным braille-спиннером и таймером
    public static async Task<T> RunWithSpinnerAsync<T>(string label, Func<Task<T>> action, CancellationToken ct)
    {
        if (!IsInteractive)
        {
            return await action().ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        using var spinnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var spinnerTask = Task.Run(async () =>
        {
            var frame = 0;
            Console.Write($"{Esc}?25l");
            try
            {
                while (!spinnerCts.IsCancellationRequested)
                {
                    var glyph = s_spinnerFrames[frame++ % s_spinnerFrames.Length];
                    var elapsed = $"{sw.Elapsed.TotalSeconds:0.0}s";
                    Console.Write($"\r  {PaintBold(glyph, Cyan)} {Paint(label, White)} {Paint($"({elapsed})", Muted)}{Esc}K");
                    await Task.Delay(75, spinnerCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                //штатная остановка спиннера
            }
            finally
            {
                Console.Write($"\r{Esc}K{Esc}?25h");
            }
        }, CancellationToken.None);

        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            await spinnerCts.CancelAsync().ConfigureAwait(false);
            await spinnerTask.ConfigureAwait(false);
        }
    }

    //отрисовка аккуратной рамки-карточки с ключами и значениями
    public static void RenderCard(string title, IReadOnlyList<(string Key, string Value)> rows, string? accentColor = null, string? footer = null)
    {
        var accent = accentColor ?? Cyan;
        var keyWidth = rows.Count > 0 ? rows.Max(static r => VisibleLength(r.Key)) : 8;
        var valWidth = rows.Count > 0 ? rows.Max(static r => VisibleLength(r.Value)) : 12;
        var contentWidth = Math.Max(keyWidth + valWidth + 5, VisibleLength(title) + 4);
        if (!string.IsNullOrEmpty(footer))
        {
            contentWidth = Math.Max(contentWidth, VisibleLength(footer) + 2);
        }

        var topTitle = $" {title} ";
        var dashRight = Math.Max(2, contentWidth - VisibleLength(topTitle));
        Console.WriteLine($"  {Paint("╭─", Border)}{PaintBold(topTitle, accent)}{Paint(new string('─', dashRight) + "╮", Border)}");

        foreach (var (k, v) in rows)
        {
            var padKey = PadRightVisible(Paint(k, Muted), keyWidth);
            var lineText = $"  {padKey}   {v}";
            var paddedLine = PadRightVisible(lineText, contentWidth);
            Console.WriteLine($"  {Paint("│", Border)}{paddedLine} {Paint("│", Border)}");
        }

        if (!string.IsNullOrEmpty(footer))
        {
            Console.WriteLine($"  {Paint("├" + new string('─', contentWidth + 1) + "┤", Border)}");
            var paddedFooter = PadRightVisible($"  {footer}", contentWidth);
            Console.WriteLine($"  {Paint("│", Border)}{paddedFooter} {Paint("│", Border)}");
        }

        Console.WriteLine($"  {Paint("╰" + new string('─', contentWidth + 1) + "╯", Border)}");
    }

    //отрисовка таблицы с авто-подбором ширины и поддержкой ansi-цветов
    public static void RenderTable(string[] headers, IReadOnlyList<string[]> rows, bool[]? rightAlign = null)
    {
        var cols = headers.Length;
        var widths = new int[cols];
        for (var c = 0; c < cols; c++)
        {
            widths[c] = VisibleLength(headers[c]);
        }

        foreach (var row in rows)
        {
            for (var c = 0; c < Math.Min(cols, row.Length); c++)
            {
                widths[c] = Math.Max(widths[c], VisibleLength(row[c]));
            }
        }

        Console.WriteLine("  " + Paint(BuildRule('╭', '┬', '╮', widths), Border));

        var headSb = new StringBuilder();
        _ = headSb.Append("  ").Append(Paint("│", Border));
        for (var c = 0; c < cols; c++)
        {
            var cell = PadRightVisible(PaintBold(headers[c], Cyan), widths[c]);
            _ = headSb.Append(' ').Append(cell).Append(' ').Append(Paint("│", Border));
        }

        Console.WriteLine(headSb);
        Console.WriteLine("  " + Paint(BuildRule('├', '┼', '┤', widths), Border));

        var rowSb = new StringBuilder();
        foreach (var row in rows)
        {
            _ = rowSb.Clear().Append("  ").Append(Paint("│", Border));
            for (var c = 0; c < cols; c++)
            {
                var raw = c < row.Length ? row[c] : string.Empty;
                var alignRight = rightAlign is not null && c < rightAlign.Length && rightAlign[c];
                var cell = alignRight ? PadLeftVisible(raw, widths[c]) : PadRightVisible(raw, widths[c]);
                _ = rowSb.Append(' ').Append(cell).Append(' ').Append(Paint("│", Border));
            }

            Console.WriteLine(rowSb);
        }

        Console.WriteLine("  " + Paint(BuildRule('╰', '┴', '╯', widths), Border));
    }

    //генерация мини-графика sparkline по последним замерам скорости
    public static string BuildSparkline(IReadOnlyList<long> samples, int width, string color)
    {
        if (samples.Count == 0)
        {
            return Paint(new string('▁', width), Muted);
        }

        var max = Math.Max(1024L, samples.Max());
        var chars = new char[width];
        var offset = Math.Max(0, samples.Count - width);
        var count = Math.Min(samples.Count, width);
        var leading = width - count;

        for (var i = 0; i < leading; i++)
        {
            chars[i] = '▁';
        }

        for (var i = 0; i < count; i++)
        {
            var val = Math.Max(0L, samples[offset + i]);
            var idx = (int)Math.Round((double)val / max * (s_sparkBlocks.Length - 1));
            chars[leading + i] = s_sparkBlocks[Math.Clamp(idx, 0, s_sparkBlocks.Length - 1)];
        }

        return Paint(new string(chars), color);
    }

    //отрисовка живой панели активного vpn подключения без мерцания и дублирования строк
    public static int RenderLiveDashboard(
        int previousLineCount,
        string state,
        string serverName,
        string serverEndpoint,
        string protocol,
        int mtu,
        TimeSpan uptime,
        long downloadBps,
        long uploadBps,
        long totalBytesReceived,
        long totalBytesSent,
        IReadOnlyList<long> dlHistory,
        IReadOnlyList<long> ulHistory,
        bool killSwitch,
        bool adBlock,
        bool blockIpv6,
        bool showLogs,
        IReadOnlyList<string> recentLogs)
    {
        if (IsInteractive && previousLineCount > 0)
        {
            Console.Write($"{Esc}{previousLineCount}F");
        }

        var (stateColor, stateBadge) = state switch
        {
            "Connected" => (Green, "● CONNECTED"),
            "Connecting" or "Reconnecting" => (Amber, "◐ CONNECTING"),
            "Disconnecting" => (Amber, "◐ DISCONNECTING"),
            "Error" => (Red, "✖ ERROR"),
            _ => (Muted, $"○ {state.ToUpperInvariant()}")
        };

        var ksBadge = killSwitch ? Paint("✔ Kill Switch", Green) : Paint("○ Kill Switch", Muted);
        var adBadge = adBlock ? Paint("✔ AdBlock DNS", Green) : Paint("○ AdBlock DNS", Muted);
        var v6Badge = blockIpv6 ? Paint("✔ Block IPv6", Green) : Paint("○ IPv6 Tunnel", Muted);

        var maxInner = 64;
        try
        {
            if (Console.WindowWidth > 20)
            {
                maxInner = Math.Clamp(Console.WindowWidth - 6, 48, 66);
            }
        }
        catch
        {
            // ignored
        }

        var innerWidth = maxInner;
        var sparkWidth = Math.Clamp(innerWidth - 40, 10, 22);
        var dlSpark = BuildSparkline(dlHistory, sparkWidth, Cyan);
        var ulSpark = BuildSparkline(ulHistory, sparkWidth, Violet);
        var dlText = PadLeftVisible(PaintBold(FormatSpeed(downloadBps), Cyan), 11);
        var ulText = PadLeftVisible(PaintBold(FormatSpeed(uploadBps), Violet), 11);
        var dlTotal = PadLeftVisible(Paint(FormatBytes(totalBytesReceived), Muted), 9);
        var ulTotal = PadLeftVisible(Paint(FormatBytes(totalBytesSent), Muted), 9);

        List<string> lines = [];

        var headerLeft = $" OBXODKA VPN  {PaintBold(stateBadge, stateColor)} ";
        var dashCount = Math.Max(2, innerWidth - VisibleLength(headerLeft));
        lines.Add($"  {Paint("╭─", Border)}{PaintBold(" OBXODKA VPN  ", White)}{PaintBold(stateBadge, stateColor)} {Paint(new string('─', dashCount) + "╮", Border)}");

        void AddRow(string content)
        {
            var padded = PadRightVisible(content, innerWidth);
            lines.Add($"  {Paint("│", Border)}{padded} {Paint("│", Border)}");
        }

        var maxServerLen = Math.Max(14, innerWidth - serverEndpoint.Length - 16);
        AddRow($"  {Paint("Сервер", Muted)}     {PaintBold(Truncate(serverName, maxServerLen), White)}  {Paint(serverEndpoint, Muted)}");
        AddRow($"  {Paint("Протокол", Muted)}   {Paint(protocol, Cyan)}   {Paint("MTU", Muted)} {PaintBold($"{mtu} B", White)}   {Paint("Аптайм", Muted)} {PaintBold($"{uptime:hh\\:mm\\:ss}", White)}");
        AddRow($"  {Paint("Защита", Muted)}     {ksBadge}   {adBadge}   {v6Badge}");
        lines.Add($"  {Paint("├" + new string('─', innerWidth + 1) + "┤", Border)}");
        AddRow($"  {PaintBold("↓", Cyan)} {Paint("Приём", Muted)}   {dlText} {dlTotal}  {dlSpark}");
        AddRow($"  {PaintBold("↑", Violet)} {Paint("Отдача", Muted)}  {ulText} {ulTotal}  {ulSpark}");

        if (showLogs && recentLogs.Count > 0)
        {
            lines.Add($"  {Paint("├" + new string('─', innerWidth + 1) + "┤", Border)}");
            foreach (var log in recentLogs.TakeLast(4))
            {
                AddRow($"  {Paint(Truncate(log, innerWidth - 3), Muted)}");
            }
        }

        lines.Add($"  {Paint("╰" + new string('─', innerWidth + 1) + "╯", Border)}");
        lines.Add($"  {Paint("[Q] / [Ctrl+C]", Cyan)} {Paint("Отключить и откатить маршруты", Muted)}   {Paint("[L]", Cyan)} {Paint(showLogs ? "Скрыть логи" : "Показать логи", Muted)}");

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            _ = sb.Append(line).Append(IsInteractive ? $"{Esc}K\n" : "\n");
        }

        //очистка хвоста при уменьшении высоты и возврат курсора точно под нижнюю границу
        var extraClear = Math.Max(0, previousLineCount - lines.Count);
        if (IsInteractive && extraClear > 0)
        {
            for (var i = 0; i < extraClear; i++)
            {
                _ = sb.Append($"{Esc}K\n");
            }

            _ = sb.Append(CultureInfo.InvariantCulture, $"{Esc}{extraClear}F");
        }

        Console.Write(sb);
        return lines.Count;
    }

    //форматирование объема трафика
    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB",
        >= 1024L * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        >= 1024L => $"{bytes / 1024.0:F1} KB",
        _ => $"{bytes} B"
    };

    //форматирование скорости трафика
    public static string FormatSpeed(long bytesPerSec) => bytesPerSec switch
    {
        >= 1024 * 1024 => $"{bytesPerSec / (1024.0 * 1024.0):F2} MB/s",
        >= 1024 => $"{bytesPerSec / 1024.0:F1} KB/s",
        _ => $"{bytesPerSec} B/s"
    };

    //раскраска пинга по порогам задержки
    public static string FormatPingBadge(int? pingMs) => pingMs switch
    {
        null => Paint("timeout", Red),
        < 80 => PaintBold($"{pingMs.Value} ms", Green),
        < 160 => PaintBold($"{pingMs.Value} ms", Amber),
        _ => PaintBold($"{pingMs.Value} ms", Red)
    };

    //расчет видимой длины строки без ansi-последовательностей
    public static int VisibleLength(string text) =>
        string.IsNullOrEmpty(text) ? 0 : AnsiEscapeRegex().Replace(text, string.Empty).Length;

    public static string PadRightVisible(string text, int totalWidth)
    {
        var pad = Math.Max(0, totalWidth - VisibleLength(text));
        return pad == 0 ? text : text + new string(' ', pad);
    }

    public static string PadLeftVisible(string text, int totalWidth)
    {
        var pad = Math.Max(0, totalWidth - VisibleLength(text));
        return pad == 0 ? text : new string(' ', pad) + text;
    }

    public static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : string.Concat(text.AsSpan(0, Math.Max(1, maxLen - 1)), "…");

    private static string BuildRule(char left, char mid, char right, int[] widths)
    {
        var sb = new StringBuilder();
        _ = sb.Append(left);
        for (var i = 0; i < widths.Length; i++)
        {
            _ = sb.Append('─', widths[i] + 2);
            _ = sb.Append(i == widths.Length - 1 ? right : mid);
        }

        return sb.ToString();
    }

    private static bool EnableWindowsVirtualTerminal()
    {
        try
        {
            const int stdOutputHandle = -11;
            const uint enableVirtualTerminalProcessing = 0x0004;
            var handle = GetStdHandle(stdOutputHandle);
            return handle != IntPtr.Zero &&
                   handle != new IntPtr(-1) &&
                   GetConsoleMode(handle, out var mode) &&
                   ((mode & enableVirtualTerminalProcessing) != 0 ||
                    SetConsoleMode(handle, mode | enableVirtualTerminalProcessing));
        }
        catch
        {
            return false;
        }
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
