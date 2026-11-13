using System.Buffers.Binary;
using System.Text;
using obxodka.Core.Models;
using obxodka.Shared.Geo;

namespace obxodka.Platforms.Windows;

//l7 инспекция сетевых протоколов: парсинг tls sni, http host и dns rr записей
internal static partial class WindowsSplitTunnelEngine
{
    //извлечение sni (server name indication) из пакета tls client hello
    private static bool TryExtractTlsSni(ReadOnlySpan<byte> payload, out string sniHost)
    {
        sniHost = string.Empty;
        if (payload.Length < 43 || payload[0] != 0x16 || payload[1] != 0x03)
        {
            return false;
        }

        var handshakeLen = (payload[3] << 8) | payload[4];
        if (payload.Length < 5 + handshakeLen || payload[5] != 0x01)
        {
            return false;
        }

        var offset = 5 + 4 + 2 + 32;
        if (offset >= payload.Length)
        {
            return false;
        }

        var sessIdLen = payload[offset];
        offset += 1 + sessIdLen;
        if (offset + 2 > payload.Length)
        {
            return false;
        }

        var cipherSuiteLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
        offset += 2 + cipherSuiteLen;
        if (offset + 1 > payload.Length)
        {
            return false;
        }

        var compMethodLen = payload[offset];
        offset += 1 + compMethodLen;
        if (offset + 2 > payload.Length)
        {
            return false;
        }

        var extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
        offset += 2;
        var extEnd = Math.Min(offset + extTotalLen, payload.Length);

        while (offset + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset + 2, 2));
            offset += 4;

            if (extType == 0x0000 && offset + extLen <= extEnd && extLen >= 5)
            {
                _ = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(offset, 2));
                var sniOffset = offset + 2;
                if (sniOffset < extEnd && payload[sniOffset] == 0x00)
                {
                    var nameLen = BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(sniOffset + 1, 2));
                    var nameOffset = sniOffset + 3;
                    if (nameOffset + nameLen <= extEnd)
                    {
                        sniHost = Encoding.ASCII.GetString(payload.Slice(nameOffset, nameLen)).TrimEnd('.');
                        return true;
                    }
                }
            }

            offset += extLen;
        }

        return false;
    }

    //извлечение значения заголовка host: из входящего http 1.1 запроса
    private static bool TryExtractHttpHost(ReadOnlySpan<byte> payload, out string host)
    {
        host = string.Empty;
        var text = Encoding.ASCII.GetString(payload[..Math.Min(payload.Length, 1024)]);
        var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
            {
                var val = line[5..].Trim();
                var colon = val.IndexOf(':');
                if (colon > 0)
                {
                    val = val[..colon];
                }
                host = val.TrimEnd('.');
                return !string.IsNullOrEmpty(host);
            }
        }

        return false;
    }

    //чтение секции вопроса dns пакета (имя домена и тип rr)
    private static bool TryReadDnsQuestion(ReadOnlySpan<byte> dnsSpan, out string qName, out ushort qType, out int qNameLen)
    {
        qName = string.Empty;
        qType = 0;
        qNameLen = 0;

        if (dnsSpan.Length < 12)
        {
            return false;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));
        if (qdCount < 1)
        {
            return false;
        }

        var offset = 12;
        var sb = new StringBuilder(64);

        while (offset < dnsSpan.Length)
        {
            var len = dnsSpan[offset];
            if (len == 0)
            {
                offset++;
                break;
            }

            if ((len & 0xC0) != 0)
            {
                return false;
            }

            offset++;
            if (offset + len > dnsSpan.Length)
            {
                return false;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append('.');
            }

            _ = sb.Append(Encoding.ASCII.GetString(dnsSpan.Slice(offset, len)));
            offset += len;
        }

        if (offset + 4 > dnsSpan.Length)
        {
            return false;
        }

        qType = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(offset, 2));
        qNameLen = offset - 12;
        qName = sb.ToString();
        return !string.IsNullOrEmpty(qName);
    }

    //закрепление физических маршрутов для всех ip адресов из ответа dns
    private static void PinBypassedDnsAnswerIps(
        ReadOnlySpan<byte> dnsSpan,
        SplitTunnelPolicy policy,
        string? knownQueryName = null,
        bool forcePinAllAnswers = false,
        bool notify = true)
    {
        if (dnsSpan.Length < 12)
        {
            return;
        }

        var flags = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(2, 2));
        var isResponse = (flags & 0x8000) != 0;
        var rcode = flags & 0x000F;
        if (!isResponse || rcode != 0)
        {
            return;
        }

        var anCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(6, 2));
        if (anCount == 0)
        {
            return;
        }

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(4, 2));
        var offset = 12;

        var queriedDomain = knownQueryName ?? string.Empty;
        for (var i = 0; i < qdCount; i++)
        {
            var name = ReadDnsDomainName(dnsSpan, offset, out var consumed);
            if (consumed == 0)
            {
                return;
            }
            if (string.IsNullOrEmpty(queriedDomain))
            {
                queriedDomain = name;
            }
            offset += consumed + 4;
        }

        var shouldPin = forcePinAllAnswers || policy.IsDomainBypassed(queriedDomain);
        if (!shouldPin && !policy.BypassAllRuTlds)
        {
            return;
        }

        var reason = string.IsNullOrEmpty(queriedDomain) ? "dns_answer" : queriedDomain;

        for (var i = 0; i < anCount && offset + 10 <= dnsSpan.Length; i++)
        {
            _ = ReadDnsDomainName(dnsSpan, offset, out var nameConsumed);
            if (nameConsumed == 0)
            {
                break;
            }
            offset += nameConsumed;

            if (offset + 10 > dnsSpan.Length)
            {
                break;
            }

            var type = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(offset, 2));
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(dnsSpan.Slice(offset + 8, 2));
            offset += 10;

            if (offset + rdLength > dnsSpan.Length)
            {
                break;
            }

            if (type == 1 && rdLength == 4)
            {
                var isRuIp = policy.BypassAllRuTlds && RuSubnetRegistry.IsRuIpv4(dnsSpan.Slice(offset, 4));
                if (shouldPin || isRuIp)
                {
                    var ipUint = BinaryPrimitives.ReadUInt32LittleEndian(dnsSpan.Slice(offset, 4));
                    PinPhysicalHostRoute(ipUint, shouldPin ? reason : "Auto-Bypass RU (DNS)", notify);
                }
            }

            offset += rdLength;
        }
    }

    //чтение доменного имени из dns пакета с поддержкой сжатия меток (компрессии)
    private static string ReadDnsDomainName(ReadOnlySpan<byte> dnsSpan, int startOffset, out int bytesConsumed)
    {
        bytesConsumed = 0;
        if (startOffset >= dnsSpan.Length)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(64);
        var current = startOffset;
        var jumped = false;
        var jumps = 0;

        while (current < dnsSpan.Length && jumps < 10)
        {
            var len = dnsSpan[current];
            if (len == 0)
            {
                if (!jumped)
                {
                    bytesConsumed = current - startOffset + 1;
                }
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (current + 1 >= dnsSpan.Length)
                {
                    return string.Empty;
                }

                if (!jumped)
                {
                    bytesConsumed = current - startOffset + 2;
                    jumped = true;
                }

                current = ((len & 0x3F) << 8) | dnsSpan[current + 1];
                jumps++;
                continue;
            }

            current++;
            if (current + len > dnsSpan.Length)
            {
                return string.Empty;
            }

            if (sb.Length > 0)
            {
                _ = sb.Append('.');
            }

            _ = sb.Append(Encoding.ASCII.GetString(dnsSpan.Slice(current, len)));
            current += len;
        }

        if (!jumped && bytesConsumed == 0)
        {
            bytesConsumed = current - startOffset;
        }

        return sb.ToString();
    }
}
