namespace obxodka.Core.Mesh;

public sealed class MeshStats
{
    private long _bytesRelayed;
    private int _activeClients;
    private long _lastThroughputBytes;
    private long _lastThroughputTimestamp = Stopwatch.GetTimestamp();
    private double _currentMbps;
    private readonly Lock _sampleLock = new();

    public long BytesRelayedTotal => Interlocked.Read(ref _bytesRelayed);
    public int ActiveClients => Volatile.Read(ref _activeClients);
    public double CurrentMbps => Volatile.Read(ref _currentMbps);

    public event Action? OnStatsUpdated;

    public void AddBytes(long bytes)
    {
        if (bytes > 0)
        {
            _ = Interlocked.Add(ref _bytesRelayed, bytes);
        }
    }

    public void IncrementClients()
    {
        _ = Interlocked.Increment(ref _activeClients);
        OnStatsUpdated?.Invoke();
    }

    public void DecrementClients()
    {
        int initial, updated;
        do
        {
            initial = Volatile.Read(ref _activeClients);
            updated = Math.Max(0, initial - 1);
        } while (initial > 0 && Interlocked.CompareExchange(ref _activeClients, updated, initial) != initial);

        OnStatsUpdated?.Invoke();
    }

    public void SampleThroughput()
    {
        lock (_sampleLock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastThroughputTimestamp, now).TotalSeconds;
            if (elapsed >= 0.5)
            {
                var currentTotal = BytesRelayedTotal;
                var deltaBytes = currentTotal - _lastThroughputBytes;
                var mbps = deltaBytes * 8.0 / (elapsed * 1_000_000.0);
                Volatile.Write(ref _currentMbps, Math.Max(0.0, mbps));
                _lastThroughputBytes = currentTotal;
                _lastThroughputTimestamp = now;
                OnStatsUpdated?.Invoke();
            }
        }
    }

    public void Reset()
    {
        lock (_sampleLock)
        {
            _ = Interlocked.Exchange(ref _bytesRelayed, 0);
            Volatile.Write(ref _activeClients, 0);
            Volatile.Write(ref _currentMbps, 0.0);
            _lastThroughputBytes = 0;
            _lastThroughputTimestamp = Stopwatch.GetTimestamp();
            OnStatsUpdated?.Invoke();
        }
    }
}

