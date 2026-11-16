using System.Buffers;

namespace obxodka.Client.Telemetry;

//фильтрация шума и защита сервера от спама
public static class TelemetryFilter
{
    private static readonly ConcurrentDictionary<int, long> s_recentErrorSignatures = new();
    private static readonly TimeSpan s_deduplicationWindow = TimeSpan.FromMinutes(5);

    //simd-поисковик шумовых маркеров и отмененных операций без повторного прохода по строке
    private static readonly SearchValues<string> s_ignoredMessageMarkers = SearchValues.Create(
        [
            "FORGED_HASH",
            "FAKE_PIN",
            "MOCK_CERT_PIN",
            "[TEST-ONLY]",
            "[TELEMETRY",
            "api/Vpn/telemetry",
            "OperationCanceledException",
            "TaskCanceledException",
            "The operation was canceled",
            "429",
            "TooManyRequests",
            "слишком много запросов",
            "401",
            "Unauthorized",
            "VERSION CHECK",
            "UPDATE CHECK",
            "PLAY STORE",
            "WINDOWS STORE"
        ],
        StringComparison.OrdinalIgnoreCase);

    //определение запуска внутри тестового раннера для блокировки отправки моков в продакшен
    public static bool IsTestEnvironment { get; } = DetectTestEnvironment();

    private static bool DetectTestEnvironment()
    {
        try
        {
            foreach (var name in AppDomain.CurrentDomain.GetAssemblies().Select(asm => asm.GetName().Name))
            {
                if (name is not null &&
                    (name.StartsWith("xunit.", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("testhost", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Microsoft.TestPlatform", StringComparison.OrdinalIgnoreCase) ||
                     name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }
        catch (AppDomainUnloadedException)
        {
            //игнорируем выгрузку домена при проверке тестовых сборок
        }
        catch (InvalidOperationException)
        {
            //игнорируем ошибку инспекции сборок текущего домена
        }

        return false;
    }

    //проверка нужно ли отправлять ошибку на сервер
    public static bool ShouldSendToServer(string message, Exception? ex = null)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        //отсеиваем отмену пользователем и штатный разрыв сокета
        if (ex is OperationCanceledException or TaskCanceledException or SocketException { SocketErrorCode: SocketError.OperationAborted or SocketError.Interrupted })
        {
            return false;
        }

        //отсеиваем синтетические маркеры, рекурсию, отмены, 429/401 и фоновые проверки обновлений за один simd-проход
        if (message.AsSpan().ContainsAny(s_ignoredMessageMarkers))
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
