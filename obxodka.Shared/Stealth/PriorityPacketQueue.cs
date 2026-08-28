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
        }

        item = default;
        return false;
    }

    public int Count => Volatile.Read(ref _count);

    private static bool IsHighPriority(byte[] packet, int length)
    {
        if (length == 9 && packet[0] == 0x99)
        {
            return true;
        }

        if (length < 20)
        {
            return true;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var ihl = (packet[0] & 0x0F) * 4;
            if (length < ihl)
            {
                return false;
            }

            var protocol = packet[9];
            if (protocol is 17 or 1)
            {
                return true;
            }

            if (protocol == 6)
            {
                if (length < ihl + 20)
                {
                    return false;
                }

                var dataOffset = (packet[ihl + 12] >> 4) * 4;
                var payloadLength = length - ihl - dataOffset;
                return payloadLength <= 0;
            }
        }
        else if (version == 6)
        {
            if (length < 40)
            {
                return false;
            }

            var nextHeader = packet[6];
            if (nextHeader is 17 or 58)
            {
                return true;
            }

            if (nextHeader == 6)
            {
                if (length < 60)
                {
                    return false;
                }

                var dataOffset = (packet[40 + 12] >> 4) * 4;
                var payloadLength = length - 40 - dataOffset;
                return payloadLength <= 0;
            }
        }

        return false;
    }

    public void Dispose() => _semaphore.Dispose();
}
