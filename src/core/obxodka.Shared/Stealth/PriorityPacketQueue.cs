namespace obxodka.Shared.Stealth;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Specialized network priority packet queue")]
public sealed class PriorityPacketQueue(int maxCapacity = 2000) : IDisposable
{
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _high = new();
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _low = new();
    private readonly SemaphoreSlim _semaphore = new(0, 50000);
    private int _count;
    private int _highStreak;
    private readonly int _maxCount = maxCapacity;
    private readonly int _controlHeadroom = Math.Min(maxCapacity + 64, 49900);

    public bool TryEnqueue(byte[] buffer, int length)
    {
        var isControl = length < 20 || (length <= 32 && buffer.Length >= 9 && buffer[8] == 0x99);
        var limit = isControl ? _controlHeadroom : _maxCount;

        if (Interlocked.Increment(ref _count) > limit)
        {
            _ = Interlocked.Decrement(ref _count);
            return false;
        }

        if (isControl || IsHighPriority(buffer, length))
        {
            _high.Enqueue((buffer, length));
        }
        else
        {
            _low.Enqueue((buffer, length));
        }

        _ = _semaphore.Release();
        return true;
    }

    public async ValueTask<(byte[] buffer, int length)> DequeueAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _semaphore.WaitAsync(ct).ConfigureAwait(false);

                if (TryPopFair(out var item))
                {
                    return item;
                }

                _ = _semaphore.Release();
            }
        }
        catch (ObjectDisposedException) { }

        return ([], 0);
    }

    public bool TryDequeue(out (byte[] buffer, int length) item)
    {
        try
        {
            if (_semaphore.Wait(0))
            {
                if (TryPopFair(out item))
                {
                    return true;
                }

                _ = _semaphore.Release();
            }
        }
        catch (ObjectDisposedException) { }

        item = default;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool TryPopFair(out (byte[] buffer, int length) item)
    {
        var streak = Interlocked.Increment(ref _highStreak);
        if ((streak & 7) == 0 && _low.TryDequeue(out item))
        {
            _ = Interlocked.Decrement(ref _count);
            return true;
        }

        if (_high.TryDequeue(out item))
        {
            _ = Interlocked.Decrement(ref _count);
            return true;
        }

        if (_low.TryDequeue(out item))
        {
            _ = Interlocked.Exchange(ref _highStreak, 0);
            _ = Interlocked.Decrement(ref _count);
            return true;
        }

        item = default;
        return false;
    }

    public void DrainAndReturn(Action<byte[]>? returnBuffer = null)
    {
        while (_semaphore.Wait(0))
        {
        }

        while (_high.TryDequeue(out var item))
        {
            _ = Interlocked.Decrement(ref _count);
            if (item.buffer is { Length: > 0 })
            {
                returnBuffer?.Invoke(item.buffer);
            }
        }

        while (_low.TryDequeue(out var item))
        {
            _ = Interlocked.Decrement(ref _count);
            if (item.buffer is { Length: > 0 })
            {
                returnBuffer?.Invoke(item.buffer);
            }
        }

        Volatile.Write(ref _count, 0);
    }

    public int Count => Volatile.Read(ref _count);

    private static bool IsHighPriority(byte[] packet, int length)
    {
        var offset = 0;
        var innerLen = length;
        if (length >= 8 && Obfuscator.TryDecodeLengths(packet.AsSpan(0, 8), out var totalLen, out var realLen) && totalLen == length && realLen <= length - 8)
        {
            offset = 8;
            innerLen = realLen;
        }

        if (innerLen == 9 && packet[offset] == 0x99)
        {
            return true;
        }

        if (innerLen < 20)
        {
            return true;
        }

        var version = packet[offset] >> 4;
        if (version == 4)
        {
            var ihl = (packet[offset] & 0x0F) * 4;
            if (ihl < 20 || innerLen < ihl)
            {
                return false;
            }

            var protocol = packet[offset + 9];
            if (protocol == 1 || (protocol == 17 && innerLen <= 1400))
            {
                return true;
            }

            if (protocol == 6)
            {
                if (innerLen < ihl + 20)
                {
                    return false;
                }

                var dataOffset = (packet[offset + ihl + 12] >> 4) * 4;
                var payloadLength = innerLen - ihl - dataOffset;
                return payloadLength <= 0;
            }
        }
        else if (version == 6)
        {
            if (innerLen < 40)
            {
                return false;
            }

            var nextHeader = packet[offset + 6];
            if (nextHeader == 58 || (nextHeader == 17 && innerLen <= 1400))
            {
                return true;
            }

            if (nextHeader == 6)
            {
                if (innerLen < 60)
                {
                    return false;
                }

                var dataOffset = (packet[offset + 40 + 12] >> 4) * 4;
                var payloadLength = innerLen - 40 - dataOffset;
                return payloadLength <= 0;
            }
        }

        return false;
    }

    private int _disposed;
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }
}
