namespace obxodka.Client.Platforms;

public static class PlatformThrottler
{
    private static readonly ConcurrentDictionary<string, long> t_lastTicks = new();

    public static void Post(string key, TimeSpan minInterval, Action action)
    {
        if (action is null)
        {
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        var prev = t_lastTicks.GetOrAdd(key, 0L);
        var elapsed = TimeSpan.FromTicks(now - prev);
        if (prev == 0L || elapsed >= minInterval)
        {
            t_lastTicks[key] = now;
            try
            {
                PlatformServices.MainThread.BeginInvokeOnMainThread(action);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PlatformThrottler] Error invoking action for key '{key}': {ex}");
            }
        }
    }

    public static void Reset(string key) => _ = t_lastTicks.TryRemove(key, out _);
}
