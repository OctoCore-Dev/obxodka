namespace obxodka.Client.Platforms;

//ограничение частоты вызова действий в главном потоке интерфейса
public static class PlatformThrottler
{
    private static readonly ConcurrentDictionary<string, long> s_lastTimestamps = new();

    //вызов действия не чаще указанного интервала
    public static void Post(string key, TimeSpan minInterval, Action action)
    {
        if (action is null)
        {
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var prev = s_lastTimestamps.GetOrAdd(key, 0L);
        var elapsed = prev == 0L ? TimeSpan.MaxValue : Stopwatch.GetElapsedTime(prev, now);
        if (prev == 0L || elapsed >= minInterval)
        {
            s_lastTimestamps[key] = now;
            try
            {
                PlatformServices.MainThread.BeginInvokeOnMainThread(action);
            }
            catch
            {
            }
        }
    }

    //сброс таймера для ключа
    public static void Reset(string key) => _ = s_lastTimestamps.TryRemove(key, out _);
}
