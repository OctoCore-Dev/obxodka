namespace obxodka.Shared.Stealth;

[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Specialized network priority packet queue")]
public sealed class PriorityPacketQueue(int maxCapacity = 2000) : IDisposable
{
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _high = new();
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _low = new();
    private readonly SemaphoreSlim _semaphore = new(0, 50000);
    private int _count;
    private readonly int _maxCount = maxCapacity;

    public bool TryEnqueue(byte[] buffer, int length)
    {
        if (Interlocked.Increment(ref _count) > _maxCount)
        {
            _ = Interlocked.Decrement(ref _count);
            return false;
        }

        if (IsHighPriority(buffer, length))
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
        while (!ct.IsCancellationRequested)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);

            if (_high.TryDequeue(out var highItem))
            {
                _ = Interlocked.Decrement(ref _count);
                return highItem;
            }

            if (_low.TryDequeue(out var lowItem))
            {
                _ = Interlocked.Decrement(ref _count);
                return lowItem;
            }

            _ = _semaphore.Release();
        }

        return ([], 0);
    }

    public bool TryDequeue(out (byte[] buffer, int length) item)
    {
        if (_semaphore.Wait(0))
        {
            if (_high.TryDequeue(out item))
            {
                _ = Interlocked.Decrement(ref _count);
                return true;
            }

            if (_low.TryDequeue(out item))
            {
                _ = Interlocked.Decrement(ref _count);
                return true;
            }

            _ = _semaphore.Release();
        }

        item = default;
        return false;
    }

    public void DrainAndReturn(Action<byte[]>? returnBuffer = null)
    {
        while (TryDequeue(out var item))
        {
            if (item.buffer != null && item.buffer.Length > 0)
            {
                returnBuffer?.Invoke(item.buffer);
            }
        }
    }

    public int Count => Volatile.Read(ref _count);

    private static bool IsHighPriority(byte[] packet, int length)
    {
        if (length == 9 && packet[0] == 0x99)
        {
            return true;
        }

        var offset = 0;
        var innerLen = length;

        if (length >= 17 && packet.Length >= length)
        {
            var totalLen = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, 4));
            var realLen = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4));
            if (totalLen == length && realLen > 0 && realLen <= length - 8)
            {
                offset = 8;
                innerLen = realLen;
            }
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
            if (innerLen < ihl)
            {
                return false;
            }

            var protocol = packet[offset + 9];
            if (protocol is 17 or 1)
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
            if (nextHeader is 17 or 58)
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

    public void Dispose() => _semaphore.Dispose();
}
