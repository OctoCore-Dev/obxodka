namespace obxodka.Shared.Stealth;

public sealed class PacketDeduplicator
{
    //массив для хранения хешей пакетов и их временных меток, используемый для обнаружения дубликатов пакетов. Размер кольца фиксирован и равен 32768, что позволяет эффективно использовать память и обеспечивать быстрый доступ к элементам.
    private const int RingSize = 32768;
    private const int RingMask = RingSize - 1;
    private const ulong TimeMask = 0xFFFFFUL; //маска для извлечения временной метки из хеша пакета, которая позволяет хранить только младшие 20 битов времени, что обеспечивает достаточную точность для обнаружения дубликатов в пределах 80 миллисекунд.
    private const ulong MaxDuplicateAgeMs = 80UL; //окно дедупликации в мл сек

    private readonly ulong[] _entries = new ulong[RingSize];

    // IsDuplicate проверяет, является ли данный пакет дубликатом, используя хеширование и временные метки. Метод сначала проверяет длину пакета и его версию (IPv4 или IPv6), затем вычисляет хеш пакета с учетом определенных полей заголовка, которые могут изменяться между фрагментами. После этого метод проверяет, был ли пакет уже обработан в пределах окна дедупликации (80 миллисекунд) и возвращает true, если пакет является дубликатом, или false в противном случае.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsDuplicate(ReadOnlySpan<byte> packet)
    {
        var length = packet.Length;
        if (length < 20)
        {
            return false;
        }

        var version = packet[0] >> 4;
        if (version == 4)
        {
            var proto = packet[9];
            var isDeduplicatable = proto == 1 ||
                                   (proto == 6 && length <= 256) ||
                                   (proto == 17 && length <= 1500);
            if (!isDeduplicatable)
            {
                return false;
            }
        }
        else if (version == 6 && length >= 40)
        {
            var nextHdr = packet[6];
            var isDeduplicatable = nextHdr == 58 ||
                                   (nextHdr == 6 && length <= 256) ||
                                   (nextHdr == 17 && length <= 1500);
            if (!isDeduplicatable)
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        var key = FastHash64(packet, version);
        var slot = (int)(key & RingMask);
        var keyTag = key & ~TimeMask;
        if (keyTag == 0)
        {
            keyTag = 1UL << 20;
        }

        var now = (ulong)Environment.TickCount64 & TimeMask;
        var newEntry = keyTag | now;
        var prevEntry = Interlocked.Exchange(ref _entries[slot], newEntry);

        if ((prevEntry & ~TimeMask) == keyTag)
        {
            var prevTime = prevEntry & TimeMask;
            var age = (now - prevTime) & TimeMask;
            if (age < MaxDuplicateAgeMs)
            {
                return true;
            }
        }

        return false;
    }

    //читает память пачками по 8 байт и вычисляет хеш пакета, используя алгоритм FNV-1a. Метод также учитывает определенные поля заголовка IPv4 и IPv6, которые могут изменяться между фрагментами, чтобы обеспечить корректное сравнение пакетов. Хеширование выполняется быстро и эффективно, что позволяет обрабатывать большое количество пакетов в реальном времени.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong FastHash64(ReadOnlySpan<byte> packet, int version)
    {
        unchecked
        {
            var length = packet.Length;
            var hash = 0xCBF29CE484222325UL ^ (ulong)length;
            var longsCount = length / 8;

            for (var i = 0; i < longsCount; i++)
            {
                var offset = i * 8;
                var val = BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(offset, 8));
                if (version == 4)
                {
                    if (offset == 8)
                    {
                        val &= ~0x00000000FFFF00FFUL;
                    }
                }
                else if (version == 6)
                {
                    if (offset == 0)
                    {
                        val &= ~0xFF00000000000000UL;
                    }
                }

                hash = (hash ^ val) * 0x100000001B3UL;
                hash = (hash << 13) | (hash >> 51);
            }

            var remainderOffset = longsCount * 8;
            for (var i = remainderOffset; i < length; i++)
            {
                hash = (hash ^ packet[i]) * 0x100000001B3UL;
            }

            return hash;
        }
    }
}
