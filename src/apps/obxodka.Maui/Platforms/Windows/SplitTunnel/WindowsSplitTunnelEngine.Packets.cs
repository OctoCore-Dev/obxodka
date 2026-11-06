using System.Buffers.Binary;

namespace obxodka.Platforms.Windows;

//генерация сырых пакетов tcp (syn-ack, rst) и синтез ответов dns для wintun
internal static partial class WindowsSplitTunnelEngine
{
    //отправка syn-ack ответа для завершения рукопожатия tcp
    private static void SendTcpSynAck(ReadOnlySpan<byte> origPacket, int ihl, WintunAdapter adapter)
    {
        var resp = new byte[40];
        resp[0] = 0x45;
        resp[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 40);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(6, 2), 0x4000);
        resp[8] = 128;
        resp[9] = 6;
        resp[10] = 0;
        resp[11] = 0;

        origPacket.Slice(16, 4).CopyTo(resp.AsSpan(12, 4));
        origPacket.Slice(12, 4).CopyTo(resp.AsSpan(16, 4));

        origPacket.Slice(ihl + 2, 2).CopyTo(resp.AsSpan(20, 2));
        origPacket.Slice(ihl, 2).CopyTo(resp.AsSpan(22, 2));

        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(24, 4), 0x10000000);

        var origSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 4, 4));
        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), origSeq + 1);

        resp[32] = 0x50;
        resp[33] = 0x12;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(34, 2), 64240);
        resp[36] = 0;
        resp[37] = 0;
        resp[38] = 0;
        resp[39] = 0;

        ComputeIpv4AndTcpChecksum(resp);
        adapter.SendPacket(resp);
    }

    //генерация и отправка tcp rst для сброса сессии исключенного приложения
    private static void SendTcpReset(ReadOnlySpan<byte> origPacket, int ihl, WintunAdapter adapter)
    {
        var resp = new byte[40];
        resp[0] = 0x45;
        resp[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 40);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(6, 2), 0x4000);
        resp[8] = 64;
        resp[9] = 6;
        resp[10] = 0;
        resp[11] = 0;

        origPacket.Slice(16, 4).CopyTo(resp.AsSpan(12, 4));
        origPacket.Slice(12, 4).CopyTo(resp.AsSpan(16, 4));

        origPacket.Slice(ihl + 2, 2).CopyTo(resp.AsSpan(20, 2));
        origPacket.Slice(ihl, 2).CopyTo(resp.AsSpan(22, 2));

        var origSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 4, 4));
        var origAck = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(ihl + 8, 4));
        var tcpFlags = origPacket[ihl + 13];
        var isSyn = (tcpFlags & 0x02) != 0;

        if (isSyn)
        {
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(24, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), origSeq + 1);
            resp[32] = 0x50;
            resp[33] = 0x14;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(24, 4), origAck);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(28, 4), 0);
            resp[32] = 0x50;
            resp[33] = 0x04;
        }

        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(34, 2), 0);
        resp[36] = 0;
        resp[37] = 0;
        resp[38] = 0;
        resp[39] = 0;

        ComputeIpv4AndTcpChecksum(resp);
        adapter.SendPacket(resp);
    }

    //генерация и отправка ipv6 tcp rst для сброса сессии исключенного приложения
    private static void SendIpv6TcpReset(ReadOnlySpan<byte> origPacket, WintunAdapter adapter)
    {
        var resp = new byte[60];
        resp[0] = 0x60;
        resp[1] = 0x00;
        resp[2] = 0x00;
        resp[3] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), 20);
        resp[6] = 6;
        resp[7] = 64;

        origPacket.Slice(24, 16).CopyTo(resp.AsSpan(8, 16));
        origPacket.Slice(8, 16).CopyTo(resp.AsSpan(24, 16));

        origPacket.Slice(42, 2).CopyTo(resp.AsSpan(40, 2));
        origPacket.Slice(40, 2).CopyTo(resp.AsSpan(42, 2));

        var origSeq = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(44, 4));
        var origAck = BinaryPrimitives.ReadUInt32BigEndian(origPacket.Slice(48, 4));
        var tcpFlags = origPacket[53];
        var isSyn = (tcpFlags & 0x02) != 0;

        if (isSyn)
        {
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(44, 4), 0);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(48, 4), origSeq + 1);
            resp[52] = 0x50;
            resp[53] = 0x14;
        }
        else
        {
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(44, 4), origAck);
            BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(48, 4), 0);
            resp[52] = 0x50;
            resp[53] = 0x04;
        }

        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(54, 2), 0);
        resp[56] = 0;
        resp[57] = 0;
        resp[58] = 0;
        resp[59] = 0;

        uint sum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
        }
        sum += 20u;
        sum += 6u;
        for (var i = 40; i < 60; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
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
        resp[56] = (byte)(sum >> 8);
        resp[57] = (byte)(sum & 0xFF);

        adapter.SendPacket(resp);
    }

    //пересчет контрольных сумм заголовков ipv4 и tcp
    private static void ComputeIpv4AndTcpChecksum(byte[] pkt)
    {
        uint ipSum = 0;
        for (var i = 0; i < 20; i += 2)
        {
            ipSum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        while ((ipSum >> 16) != 0)
        {
            ipSum = (ipSum & 0xFFFF) + (ipSum >> 16);
        }
        ipSum = ~ipSum;
        pkt[10] = (byte)(ipSum >> 8);
        pkt[11] = (byte)(ipSum & 0xFF);

        uint tcpSum = 0;
        tcpSum += (uint)((pkt[12] << 8) | pkt[13]);
        tcpSum += (uint)((pkt[14] << 8) | pkt[15]);
        tcpSum += (uint)((pkt[16] << 8) | pkt[17]);
        tcpSum += (uint)((pkt[18] << 8) | pkt[19]);
        tcpSum += 6u;
        tcpSum += 20u;

        for (var i = 20; i < 40; i += 2)
        {
            tcpSum += (uint)((pkt[i] << 8) | pkt[i + 1]);
        }
        while ((tcpSum >> 16) != 0)
        {
            tcpSum = (tcpSum & 0xFFFF) + (tcpSum >> 16);
        }
        tcpSum = ~tcpSum;
        pkt[36] = (byte)(tcpSum >> 8);
        pkt[37] = (byte)(tcpSum & 0xFF);
    }

    //обертывание ответа dns в ipv4/udp пакет для отправки в wintun
    private static byte[] WrapDnsResponsePacket(byte[] req, int ipHdrLen, ReadOnlySpan<byte> dnsResponsePayload)
    {
        var totalLen = ipHdrLen + 8 + dnsResponsePayload.Length;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, ipHdrLen);
        Buffer.BlockCopy(req, 12, resp, 16, 4);
        Buffer.BlockCopy(req, 16, resp, 12, 4);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)totalLen);
        resp[10] = 0;
        resp[11] = 0;

        Buffer.BlockCopy(req, ipHdrLen, resp, ipHdrLen + 2, 2);
        Buffer.BlockCopy(req, ipHdrLen + 2, resp, ipHdrLen, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(ipHdrLen + 4, 2), (ushort)(totalLen - ipHdrLen));
        resp[ipHdrLen + 6] = 0;
        resp[ipHdrLen + 7] = 0;

        dnsResponsePayload.CopyTo(resp.AsSpan(ipHdrLen + 8));

        uint sum = 0;
        for (var i = 0; i < ipHdrLen; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        sum = ~sum;
        resp[10] = (byte)(sum >> 8);
        resp[11] = (byte)(sum & 0xFF);

        return resp;
    }

    //обертывание ответа dns в ipv6/udp пакет для отправки в wintun
    private static byte[] WrapIpv6DnsResponsePacket(byte[] req, ReadOnlySpan<byte> dnsResponsePayload)
    {
        var udpLen = 8 + dnsResponsePayload.Length;
        var totalLen = 40 + udpLen;
        var resp = new byte[totalLen];

        Buffer.BlockCopy(req, 0, resp, 0, 40);
        Buffer.BlockCopy(req, 8, resp, 24, 16);
        Buffer.BlockCopy(req, 24, resp, 8, 16);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), (ushort)udpLen);
        resp[6] = 17;
        resp[7] = 64;

        Buffer.BlockCopy(req, 40, resp, 42, 2);
        Buffer.BlockCopy(req, 42, resp, 40, 2);
        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(44, 2), (ushort)udpLen);
        resp[46] = 0;
        resp[47] = 0;

        dnsResponsePayload.CopyTo(resp.AsSpan(48));

        uint sum = 0;
        for (var i = 8; i < 40; i += 2)
        {
            sum += (uint)((resp[i] << 8) | resp[i + 1]);
        }
        sum += (uint)udpLen;
        sum += 17u;
        for (var i = 0; i < udpLen; i += 2)
        {
            if (i + 1 < udpLen)
            {
                sum += (uint)((resp[40 + i] << 8) | resp[40 + i + 1]);
            }
            else
            {
                sum += (uint)(resp[40 + i] << 8);
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
        resp[46] = (byte)(sum >> 8);
        resp[47] = (byte)(sum & 0xFF);

        return resp;
    }

    //создание фиктивного ответа dns nodata для подавления aaaa запросов к ru-доменам
    private static byte[] CreateDnsNoDataResponse(byte[] req, int ipHdrLen, int udpOffset, int dnsOffset, int qNameLen, bool isIpv4)
    {
        var totalLen = dnsOffset + 12 + qNameLen + 4;
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
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(2, 2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(6, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(8, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(dnsSpan.Slice(10, 2), 0);

        if (isIpv4)
        {
            uint sum = 0;
            for (var i = 0; i < ipHdrLen; i += 2)
            {
                sum += (uint)((resp[i] << 8) | resp[i + 1]);
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
                sum += (uint)((resp[i] << 8) | resp[i + 1]);
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

        return resp;
    }
}
