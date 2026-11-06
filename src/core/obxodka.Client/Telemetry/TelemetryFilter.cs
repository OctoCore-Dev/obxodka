namespace obxodka.Client.Telemetry;

//фильтрация шума и защита сервера от спама
public static class TelemetryFilter
{
    private static readonly ConcurrentDictionary<int, long> s_recentErrorSignatures = new();
    private static readonly TimeSpan s_deduplicationWindow = TimeSpan.FromMinutes(5);

    //проверка нужно ли отправлять ошибку на сервер
    public static bool ShouldSendToServer(string message, Exception? ex = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        //отсеиваем отмену пользователем
        if (ex is OperationCanceledException or TaskCanceledException)
        {
            return false;
        }

        if (message.Contains("OperationCanceledException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TaskCanceledException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("The operation was canceled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        //отсеиваем частые клики и 429
        if (message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("слишком много запросов", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        //отсеиваем 401 при выходе из аккаунта
        if (message.Contains("401", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        //отсеиваем штатный разрыв сокета
        if (ex is SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted })
        {
            return false;
        }

        //не шлем одну и ту же ошибку чаще раза в 5 минут
        var signature = ComputeErrorSignature(message, ex);
        var nowTicks = Stopwatch.GetTimestamp();

        if (s_recentErrorSignatures.TryGetValue(signature, out var lastSentTicks))
        {
            var elapsed = Stopwatch.GetElapsedTime(lastSentTicks, nowTicks);
            if (elapsed < s_deduplicationWindow)
            {
                return false;
            }
        }

        s_recentErrorSignatures[signature] = nowTicks;
        CleanupStaleSignatures(nowTicks);
        return true;
    }

    //расчет сигнатуры ошибки для сравнения
    private static int ComputeErrorSignature(string message, Exception? ex)
    {
        var exName = ex?.GetType().Name ?? string.Empty;
        var normalizedMessage = message.Length > 200 ? message[..200] : message;
        return HashCode.Combine(exName, normalizedMessage);
    }

    //чистка старых сигнатур из памяти
    private static void CleanupStaleSignatures(long nowTicks)
    {
        if (s_recentErrorSignatures.Count <= 50)
        {
            return;
        }

        foreach (var (sig, lastTicks) in s_recentErrorSignatures)
        {
            if (Stopwatch.GetElapsedTime(lastTicks, nowTicks) > s_deduplicationWindow)
            {
                _ = s_recentErrorSignatures.TryRemove(sig, out _);
            }
        }
    }

    //сброс истории для тестов
    public static void ResetHistory() => s_recentErrorSignatures.Clear();
}
