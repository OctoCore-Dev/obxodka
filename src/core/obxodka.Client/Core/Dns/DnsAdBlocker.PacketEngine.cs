namespace obxodka.Core;

public static partial class DnsAdBlocker
{
    //проверка сетевого ip пакета на dns запрос и подмена ответа при блокировке
    public static byte[]? ProcessPacket(byte[] packet, int length, bool? useAdblock = null)
    {
        var enabled = useAdblock ?? IsAdBlockEnabled;
        if (!enabled || length < 20)
        {
            return null;
        }

        try
        {
            var version = packet[0] >> 4;
            var ipHeaderLen = 0;
            var protocol = 0;

            if (version == 4)
            {
                ipHeaderLen = (packet[0] & 0x0F) * 4;
                if (length < ipHeaderLen + 8)
                {
                    return null;
                }
                protocol = packet[9];
            }
            else if (version == 6)
            {
                ipHeaderLen = 40;
                if (length < ipHeaderLen + 8)
                {
                    return null;
                }
                protocol = packet[6];
            }
            else
            {
                return null;
            }

            if (protocol != 17)
            {
                return null;
            }

            var udpHeaderOffset = ipHeaderLen;
            var dstPort = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(udpHeaderOffset + 2, 2));
            if (dstPort != 53)
            {
                return null;
            }

            var dnsOffset = udpHeaderOffset + 8;
            if (length < dnsOffset + 12)
            {
                return null;
            }

            var dnsSpan = packet.AsSpan(dnsOffset, length - dnsOffset);
            var flags = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(2, 2));
            var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));

            if ((flags & 0x8000) != 0 || qdCount == 0)
            {
                return null;
            }

            Span<char> domainBuf = stackalloc char[256];
            if (!TryParseDnsName(dnsSpan, 12, domainBuf, out var charsWritten, out var bytesRead))
            {
                return null;
            }

            var domainSpan = domainBuf[..charsWritten];
            if (IsBlocked(domainSpan))
            {
                _ = Interlocked.Increment(ref s_blockedQueriesCount);
                var isCanary = domainSpan.SequenceEqual("use-application-dns.net");
                return CreateDnsResponse(packet, ipHeaderLen, udpHeaderOffset, dnsOffset, bytesRead, version == 4, isCanary);
            }
        }
        catch
        {
        }

        return null;
    }

    //парсинг доменного имени из формата dns меток в span символов
    private static bool TryParseDnsName(ReadOnlySpan<byte> dnsSpan, int offset, Span<char> dest, out int charsWritten, out int bytesRead)
    {
        charsWritten = 0;
        bytesRead = 0;
        var currentOffset = offset;

        while (currentOffset < dnsSpan.Length)
        {
            int len = dnsSpan[currentOffset++];
            if (len == 0)
            {
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                currentOffset++;
                break;
            }

            if (charsWritten > 0)
            {
                if (charsWritten >= dest.Length)
                {
                    return false;
                }
                dest[charsWritten++] = '.';
            }

            if (currentOffset + len > dnsSpan.Length || charsWritten + len > dest.Length)
            {
                return false;
            }

            for (var i = 0; i < len; i++)
            {
                var b = dnsSpan[currentOffset + i];
                dest[charsWritten++] = (char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b);
            }

            currentOffset += len;
        }

        bytesRead = currentOffset - offset;
        return charsWritten > 0;
    }

    //парсинг доменного имени в строку
    public static string ParseDnsName(ReadOnlySpan<byte> dnsSpan, int offset, out int bytesRead)
    {
        Span<char> dest = stackalloc char[256];
        if (TryParseDnsName(dnsSpan, offset, dest, out var charsWritten, out bytesRead))
        {
            return new string(dest[..charsWritten]);
        }

        bytesRead = 0;
        return string.Empty;
    }

    //формирование поддельного dns ответа (0.0.0.0 или nxdomain)
    private static byte[] CreateDnsResponse(byte[] req, int ipHdrLen, int udpOffset, int dnsOffset, int qNameLen, bool isIpv4, bool isNxDomain = false)
    {
        var qType = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(dnsOffset + 12 + qNameLen, 2));
        var isAaaa = qType == 28;
        var ansLen = (isAaaa || isNxDomain) ? 0 : 16;
        var totalLen = dnsOffset + 12 + qNameLen + 4 + ansLen;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, ipHdrLen);
        if (isIpv4)
        {
            Buffer.BlockCopy(req, 12, resp, 16, 4);
            Buffer.BlockCopy(req, 16, resp, 12, 4);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)totalLen);
            resp[10] = 0;
            resp[11] = 0;
        }
        else
        {
            Buffer.BlockCopy(req, 8, resp, 24, 16);
            Buffer.BlockCopy(req, 24, resp, 8, 16);
            BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), (ushort)(totalLen - 40));
        }

        Buffer.BlockCopy(req, udpOffset, resp, udpOffset + 2, 2);
        Buffer.BlockCopy(req, udpOffset + 2, resp, udpOffset, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(udpOffset + 4, 2), (ushort)(totalLen - udpOffset));
        resp[udpOffset + 6] = 0;
        resp[udpOffset + 7] = 0;

        Buffer.BlockCopy(req, dnsOffset, resp, dnsOffset, 12 + qNameLen + 4);
        var dnsSpan = resp.AsSpan(dnsOffset);

        var flags = isNxDomain ? (ushort)0x8183 : (ushort)0x8180;
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(6, 2), (isAaaa || isNxDomain) ? (ushort)0 : (ushort)1);

        if (!isAaaa && !isNxDomain)
        {
            var ansOffset = dnsOffset + 12 + qNameLen + 4;
            var ansSpan = resp.AsSpan(ansOffset);

            BinaryPrimitives.WriteUInt16BigEndian(ansSpan[..2], 0xC00C);
            BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(2, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(4, 2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(ansSpan.Slice(6, 4), 300);
            BinaryPrimitives.WriteUInt16BigEndian(ansSpan.Slice(10, 2), 4);

            ansSpan[12] = 0;
            ansSpan[13] = 0;
            ansSpan[14] = 0;
            ansSpan[15] = 0;
        }

        ComputeChecksums(resp, ipHdrLen, udpOffset, totalLen, isIpv4);
        return resp;
    }

    //пересчет контрольных сумм заголовков ip и udp
    private static void ComputeChecksums(byte[] resp, int ipHdrLen, int udpOffset, int totalLen, bool isIpv4)
    {
        if (isIpv4)
        {
            uint sum = 0;
            for (var i = 0; i < ipHdrLen; i += 2)
            {
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            sum = ~sum;
            resp[10] = (byte)(sum >> 8);
            resp[11] = (byte)(sum & 0xFF);
        }
        else
        {
            uint sum = 0;
            for (var i = 8; i < 40; i += 2)
            {
                sum += (uint)((resp[i] << 8) + resp[i + 1]);
            }

            var udpLen = totalLen - udpOffset;
            sum += (uint)udpLen;
            sum += 17;

            for (var i = 0; i < udpLen; i += 2)
            {
                if (i + 1 < udpLen)
                {
                    sum += (uint)((resp[udpOffset + i] << 8) + resp[udpOffset + i + 1]);
                }
                else
                {
                    sum += (uint)(resp[udpOffset + i] << 8);
                }
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }

            sum = ~sum;
            if (sum == 0)
            {
                sum = 0xFFFF;
            }

            resp[udpOffset + 6] = (byte)(sum >> 8);
            resp[udpOffset + 7] = (byte)(sum & 0xFF);
        }
    }
}
