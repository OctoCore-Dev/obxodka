namespace obxodka.Shared.Stealth;

//приоритетная очередь сетевых пакетов для устранения bufferbloat и мгновенного пропуска пингов, dns и tcp ack
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Specialized network priority packet queue")]
public sealed class PriorityPacketQueue(int maxCapacity = 2000) : IDisposable
{
    //высокоприоритетная очередь для управляющих кадров, пингов, dns, icmp и пустых tcp ack
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _high = new();

    //низкоприоритетная очередь для тяжелых пакетных данных
    private readonly ConcurrentQueue<(byte[] buffer, int length)> _low = new();

    //семафор уведомления о наличии пакетов в очереди
    private readonly SemaphoreSlim _semaphore = new(0, 50000);

    //счетчики текущего заполнения и полосы приоритета
    private int _count;
    private int _highStreak;
    private int _disposed;

    //лимиты обычных и управляющих пакетов
    private readonly int _maxCount = maxCapacity;
    private readonly int _controlHeadroom = Math.Min(maxCapacity + 64, 49900);

    //текущее количество пакетов в очереди
    public int Count => Math.Max(0, Volatile.Read(ref _count));

    //добавление пакета в очередь с автоматической классификацией приоритета
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryEnqueue(byte[] buffer, int length)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        var isControl = IsControlFrame(buffer, length);
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

        try
        {
            _ = _semaphore.Release();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (SemaphoreFullException)
        {
        }

        return true;
    }

    //асинхронное извлечение следующего пакета с учетом приоритета и защиты от голодания
    public async ValueTask<(byte[] buffer, int length)> DequeueAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
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

    //неблокирующее извлечение пакета из очереди
    public bool TryDequeue(out (byte[] buffer, int length) item)
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && _semaphore.Wait(0))
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

    //справедливая выборка между высокоприоритетной и низкоприоритетной очередями (7:1)
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

    //чистка памяти при отключении: сдать все буферы обратно в пул
    public void DrainAndReturn(Action<byte[]>? returnBuffer = null)
    {
        try
        {
            while (Volatile.Read(ref _disposed) == 0 && _semaphore.Wait(0))
            {
            }
        }
        catch (ObjectDisposedException) { }

        while (_high.TryDequeue(out var highItem))
        {
            _ = Interlocked.Decrement(ref _count);
            if (highItem.buffer is { Length: > 0 })
            {
                returnBuffer?.Invoke(highItem.buffer);
            }
        }

        while (_low.TryDequeue(out var lowItem))
        {
            _ = Interlocked.Decrement(ref _count);
            if (lowItem.buffer is { Length: > 0 })
            {
                returnBuffer?.Invoke(lowItem.buffer);
            }
        }

        _ = Interlocked.Exchange(ref _count, 0);
    }

    //определение управляющих и зондирующих кадров протокола (ping, pong, mtu sync, burst, probes)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsControlFrame(byte[] buffer, int length)
    {
        if (length < 20)
        {
            return true;
        }

        if (buffer.Length < 2)
        {
            return false;
        }

        var b0 = buffer[0];
        var b1 = buffer[1];
        return (b0 == 0xAA && b1 == 0xBB) ||
               (b0 == 0xCC && b1 == 0xDD) ||
               (b0 == 0xEE && (b1 == 0x77 || b1 == 0x55)) ||
               (length <= 32 && b0 == 0x99);
    }

    //классификация приоритетных сетевых пакетов l3/l4 (icmp, dns/ntp малые udp, чистые tcp ack/syn/fin)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsHighPriority(byte[] packet, int length)
    {
        if (length < 20 || packet.Length < length)
        {
            return true;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var ihl = (packet[0] & 0x0F) * 4;
            if (ihl < 20 || length < ihl)
            {
                return false;
            }

            var protocol = packet[9];
            if (protocol == 1 || (protocol == 17 && length <= 320))
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
            if (nextHeader == 58 || (nextHeader == 17 && length <= 320))
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

    //освобождение ресурсов семафора
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _semaphore.Dispose();
        }
    }
}
