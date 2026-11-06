namespace obxodka.Core;

public sealed partial class OctopusEngine
{
    //проверка двусторонней проходимости канала передачи данных
    public async Task<bool> VerifyDownlinkAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (!IsConnected || _transport is null)
        {
            AppLogger.LogWarning("[PROBE FAIL] Engine is not connected or transport is null.");
            return false;
        }

        var initialReceived = TotalBytesReceived;
        var initialSent = TotalBytesSent;

        if (initialReceived > 0)
        {
            AppLogger.Log($"[PROBE SUCCESS] Downlink already active with {initialReceived}B received.");
            _ = _transport.SendMtuSyncAsync(SmartMtuEngine.CurrentMtu);
            return true;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts?.Token ?? CancellationToken.None);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnPacket(byte[] pkt, int len)
        {
            if (len > 0)
            {
                var proto = len >= 20 ? ((pkt[0] >> 4) == 4 ? $"IPv4(proto={pkt[9]})" : "IPv6") : "NonIP";
                AppLogger.Log($"[PROBE RX] Received verified downlink packet ({proto}, {len}B). Total RX={TotalBytesReceived}B");
                _ = tcs.TrySetResult(true);
            }
        }

        void OnPing(long rtt)
        {
            EmitStatus(VpnStatusMessages.PingReceived(rtt));
            _ = tcs.TrySetResult(true);
        }

        OnProbePacketInspect += OnPacket;
        OnPingUpdated += OnPing;

        var probeCycle = 0;
        try
        {
            async Task SendProbesAsync()
            {
                var cycle = Interlocked.Increment(ref probeCycle);
                try
                {
                    await (_transport?.SendPingProbeAsync() ?? Task.CompletedTask).ConfigureAwait(false);
                }
                catch { }

                try
                {
                    if (BuildIcmpProbePacket(AssignedIp, "100.64.0.1", 28) is { } pIcmpSelf)
                    {
                        _ = SendPacketAsync(pIcmpSelf);
                    }
                    if (cycle % 2 == 1 && BuildDnsProbePacket(AssignedIp, 1) is { } p1)
                    {
                        _ = SendPacketAsync(p1);
                    }
                }
                catch { }

                AppLogger.Log($"[PROBE TX #{cycle}] Sent lightweight Ping + ICMP(28B). TotalSent={TotalBytesSent}B, TotalRecv={TotalBytesReceived}B");
            }

            _ = Task.Run(SendProbesAsync, linkedCts.Token);

            var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (Stopwatch.GetTimestamp() < deadline && !linkedCts.IsCancellationRequested)
            {
                if (TotalBytesReceived > initialReceived)
                {
                    var curTx = TotalBytesSent - initialSent;
                    var curRx = TotalBytesReceived - initialReceived;
                    _ = _transport?.SendMtuSyncAsync(SmartMtuEngine.CurrentMtu);
                    EmitStatus(VpnStatusMessages.MtuLocked(SmartMtuEngine.CurrentMtu));
                    EmitStatus(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));
                    return true;
                }

                var delayTask = Task.Delay(400, linkedCts.Token);
                var completed = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
                if (completed == tcs.Task && await tcs.Task.ConfigureAwait(false))
                {
                    var curTx = TotalBytesSent - initialSent;
                    var curRx = TotalBytesReceived - initialReceived;
                    _ = _transport?.SendMtuSyncAsync(SmartMtuEngine.CurrentMtu);
                    EmitStatus(VpnStatusMessages.MtuLocked(SmartMtuEngine.CurrentMtu));
                    EmitStatus(VpnStatusMessages.ChannelVerifiedDuplex(curTx, curRx));
                    return true;
                }

                _ = Task.Run(SendProbesAsync, linkedCts.Token);
            }

            var deltaRx = TotalBytesReceived - initialReceived;
            var deltaTx = TotalBytesSent - initialSent;
            if (deltaTx <= 0)
            {
                EmitStatus(VpnStatusMessages.TxTransmissionFailed);
                AppLogger.LogError("[PROBE TIMEOUT] Outgoing transmission failed (TX=0). Sockets or local firewall blocked packets.");
            }
            else if (deltaRx <= 0)
            {
                EmitStatus(VpnStatusMessages.RxResponseTimeout(deltaTx));
                AppLogger.LogError($"[PROBE TIMEOUT] Server downlink verification failed after {timeout.TotalSeconds:F1}s! Sent {deltaTx}B across {probeCycle} probe cycles, but received 0 bytes from server.");
            }
            else
            {
                _ = _transport?.SendMtuSyncAsync(SmartMtuEngine.CurrentMtu);
                EmitStatus(VpnStatusMessages.MtuLocked(SmartMtuEngine.CurrentMtu));
            }
            return deltaRx > 0 && deltaTx > 0;
        }
        catch (OperationCanceledException)
        {
            var deltaRx = TotalBytesReceived - initialReceived;
            var deltaTx = TotalBytesSent - initialSent;
            if (deltaRx > 0)
            {
                _ = _transport?.SendMtuSyncAsync(SmartMtuEngine.CurrentMtu);
                EmitStatus(VpnStatusMessages.MtuLocked(SmartMtuEngine.CurrentMtu));
            }
            return deltaRx > 0 && deltaTx > 0;
        }
        finally
        {
            OnProbePacketInspect -= OnPacket;
            OnPingUpdated -= OnPing;
        }
    }

    //сборка легковесного icmp ping пакета для зондирования
    private static byte[]? BuildIcmpProbePacket(string assignedIp, string targetIp = "100.64.0.1", int totalLen = 28)
    {
        if (totalLen < 28)
        {
            totalLen = 28;
        }

        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork ||
            !IPAddress.TryParse(targetIp, out var dstIp) || dstIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        var packet = new byte[totalLen];

        packet[0] = 0x45;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)totalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x7788);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
        packet[8] = 64;
        packet[9] = 1;
        srcIp.GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        dstIp.GetAddressBytes().CopyTo(packet.AsSpan(16, 4));

        var ipChecksum = ComputeChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        packet[20] = 8;
        packet[21] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 0);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), 0x1337);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 1);

        for (var i = 28; i < totalLen; i++)
        {
            packet[i] = (byte)(i & 0xFF);
        }

        var icmpChecksum = ComputeChecksum(packet.AsSpan(20, totalLen - 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), icmpChecksum);

        return packet;
    }

    //сборка легковесного dns query пакета для зондирования
    private static byte[]? BuildDnsProbePacket(string assignedIp, byte dstOctet = 1)
    {
        if (!IPAddress.TryParse(assignedIp, out var srcIp) || srcIp.AddressFamily != AddressFamily.InterNetwork)
        {
            return null;
        }

        byte[] dns = [
            0x1A, 0x2B, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x03, 0x64, 0x6e, 0x73, 0x06, 0x67, 0x6f, 0x6f, 0x67, 0x6c, 0x65, 0x00,
            0x00, 0x01, 0x00, 0x01
        ];

        var udpLen = 8 + dns.Length;
        var totalLen = 20 + udpLen;
        var packet = new byte[totalLen];

        packet[0] = 0x45;
        packet[1] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2, 2), (ushort)totalLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 0x5432);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 0x4000);
        packet[8] = 64;
        packet[9] = 17;
        srcIp.GetAddressBytes().CopyTo(packet.AsSpan(12, 4));
        packet[16] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[17] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[18] = dstOctet == 8 ? (byte)8 : (byte)1;
        packet[19] = dstOctet == 8 ? (byte)8 : (byte)1;

        var ipChecksum = ComputeChecksum(packet.AsSpan(0, 20));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), ipChecksum);

        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 53535);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(24, 2), (ushort)udpLen);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(26, 2), 0);

        dns.CopyTo(packet.AsSpan(28));
        return packet;
    }

    //вычисление контрольной суммы заголовка
    private static ushort ComputeChecksum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 2)
        {
            if (i + 1 < data.Length)
            {
                sum += (uint)((data[i] << 8) + data[i + 1]);
            }
            else
            {
                sum += (uint)(data[i] << 8);
            }
        }
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }
}
