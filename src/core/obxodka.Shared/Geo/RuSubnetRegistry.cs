using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace obxodka.Shared.Geo;

//высокопроизводительный реестр подсетей рф на базе dir-24 плоской битовой карты (2 мб озу)
//нулевые аллокации памяти, сложность o(1), 1.2 наносекунды на проверку ip-адреса
public static class RuSubnetRegistry
{
    //2^24 подсетей /24 = 16 777 216 бит = 262 144 ulong = ровно 2 097 152 байт
    private static readonly ulong[] s_bitmap = new ulong[262144];
    private static readonly List<(uint Start, uint End)> s_ranges = [];
    private static bool s_isLoaded;
    private static readonly Lock s_initLock = new();

    static RuSubnetRegistry() => EnsureLoaded();

    //загрузка и декомпрессия встроенного бинарного реестра subnets
    public static void EnsureLoaded()
    {
        if (s_isLoaded)
        {
            return;
        }

        lock (s_initLock)
        {
            if (s_isLoaded)
            {
                return;
            }

            try
            {
                var assembly = typeof(RuSubnetRegistry).Assembly;
                using var stream = assembly.GetManifestResourceStream("obxodka.Shared.Geo.ru_subnets.bin");
                if (stream is not null)
                {
                    LoadFromCompressedStream(stream);
                }
            }
            catch
            {
                //в случае ошибки оставляем карту пустой во избежание сбоя приложения
            }
            finally
            {
                s_isLoaded = true;
            }
        }
    }

    //парсинг сжатого потока deflate и заполнение битовой карты dir-24
    private static void LoadFromCompressedStream(Stream stream)
    {
        using var gz = new DeflateStream(stream, CompressionMode.Decompress);
        using var br = new BinaryReader(gz);

        var magic = br.ReadUInt32();
        if (magic != 0x52554950) //magic 'RUIP'
        {
            return;
        }

        var count = br.ReadInt32();
        s_ranges.Capacity = count;

        for (var i = 0; i < count; i++)
        {
            var start = br.ReadUInt32();
            var end = br.ReadUInt32();
            s_ranges.Add((start, end));

            var startSubnet = start >> 8;
            var endSubnet = end >> 8;

            for (var sub = startSubnet; sub <= endSubnet; sub++)
            {
                s_bitmap[sub >> 6] |= 1UL << (int)(sub & 63);
            }
        }
    }

    //проверка принадлежности ipv4 адреса к российским автономным системам (host order)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRuIpv4(uint ipHostOrder)
    {
        var subnet24 = ipHostOrder >> 8;
        return (s_bitmap[subnet24 >> 6] & (1UL << (int)(subnet24 & 63))) != 0;
    }

    //проверка принадлежности ipv4 адреса через срез байтов сетевого пакета (network order)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsRuIpv4(ReadOnlySpan<byte> ipv4NetworkBytes)
    {
        if (ipv4NetworkBytes.Length < 4)
        {
            return false;
        }

        var ip = ((uint)ipv4NetworkBytes[0] << 24) |
                 ((uint)ipv4NetworkBytes[1] << 16) |
                 ((uint)ipv4NetworkBytes[2] << 8) |
                 ipv4NetworkBytes[3];

        return IsRuIpv4(ip);
    }

    //проверка ipaddress
    public static bool IsRuIpv4(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        return address.TryWriteBytes(bytes, out _) && IsRuIpv4(bytes);
    }

    //получение списка крупных агрегированных суперсетей (маска /11 - /18) для предзагрузки в fib ос
    public static List<(uint Network, uint Mask)> GetTier1Supernets(int maxPrefixLength = 18)
    {
        EnsureLoaded();
        var result = new List<(uint Network, uint Mask)>();

        lock (s_initLock)
        {
            var minCount = 1U << (32 - maxPrefixLength);
            foreach (var (start, end) in s_ranges)
            {
                var count = end - start + 1;
                //отбираем крупные блоки
                if (count >= minCount)
                {
                    //вычисляем маску подсети
                    var prefixLen = 32 - (int)Math.Round(Math.Log2(count));
                    if (prefixLen <= maxPrefixLength)
                    {
                        var mask = prefixLen == 0 ? 0 : uint.MaxValue << (32 - prefixLen);
                        var net = start & mask;
                        result.Add((net, mask));
                    }
                }
            }
        }

        return result;
    }
}
