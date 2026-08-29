namespace obxodka.Core.Mesh;

public sealed class BandwidthLimiter(int maxMbps)
{
    private long _maxBytesPerSecond = Math.Max(125_000, (long)maxMbps * 125_000);
    private long _availableTokens = Math.Max(125_000, (long)maxMbps * 125_000);
    private long _lastRefillTimestamp = Stopwatch.GetTimestamp();
    private readonly Lock _lock = new();

    public void UpdateLimit(int maxMbps)
    {
        lock (_lock)
        {
            _maxBytesPerSecond = Math.Max(125_000, (long)maxMbps * 125_000);
            _availableTokens = Math.Min(_availableTokens, _maxBytesPerSecond * 2);
        }
    }

    public ValueTask ConsumeAsync(int bytes, CancellationToken ct = default)
    {
        if (bytes <= 0)
        {
            return ValueTask.CompletedTask;
        }

        if (ct.IsCancellationRequested)
        {
            return ValueTask.FromCanceled(ct);
        }

        lock (_lock)
        {
            Refill();

            var effectiveBytes = Math.Min(bytes, _maxBytesPerSecond * 2);
            if (_availableTokens >= effectiveBytes)
            {
                _availableTokens -= effectiveBytes;
                return ValueTask.CompletedTask;
            }
        }

        return ConsumeSlowAsync(bytes, ct);
    }

    private async ValueTask ConsumeSlowAsync(int bytes, CancellationToken ct)
    {
        var effectiveBytes = Math.Min(bytes, _maxBytesPerSecond * 2);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(5, ct).ConfigureAwait(false);

            lock (_lock)
            {
                Refill();
                if (_availableTokens >= effectiveBytes)
                {
                    _availableTokens -= effectiveBytes;
                    return;
                }
            }
        }

        ct.ThrowIfCancellationRequested();
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastRefillTimestamp, now);
        if (elapsed.TotalSeconds > 0.001)
        {
            var newTokens = (long)(elapsed.TotalSeconds * _maxBytesPerSecond);
            if (newTokens > 0)
            {
                _availableTokens = Math.Min(_maxBytesPerSecond * 2, _availableTokens + newTokens);
                _lastRefillTimestamp = now;
            }
        }
    }
}
