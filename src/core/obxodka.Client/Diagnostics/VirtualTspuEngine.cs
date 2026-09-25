using System.Buffers.Binary;

namespace obxodka.Client.Diagnostics;

[Flags]
public enum TspuThreat
{
    None = 0,
    HighEntropyCryptoAnomaly = 1 << 0,
    WireGuardHandshake = 1 << 1,
    OpenVpnSignature = 1 << 2,
    QuicInitialThrottled = 1 << 3,
    FechsueStaticSessionLeak = 1 << 4,
    ObfuscatorStaticLengthHeader = 1 << 5,
    CleartextSniDetected = 1 << 6,
    DiscreteMachineSizing = 1 << 7,
    MonotonicTimingFlow = 1 << 8,
    ActiveProbeFailed = 1 << 9
}

public sealed record TspuPacketAudit(
    int PacketIndex,
    int PacketSize,
    bool IsUdp,
    double Entropy,
    TspuThreat DetectedThreats,
    string Details,
    string HexDump = "");

public sealed class VirtualTspuReport
{
    public int TotalPackets { get; set; }
    public double AverageEntropy { get; set; }
    public double MaxEntropy { get; set; }
    public int BlockedCount { get; set; }
    public int PassedCount { get; set; }
    public TspuThreat TotalFlags { get; set; }
    public List<TspuPacketAudit> Violations { get; } = [];
    public bool IsStealthPassed => TotalFlags == TspuThreat.None;
    public string Summary { get; set; } = string.Empty;
}

public sealed class VirtualTspuEngine
{
    private uint _lastObservedSessionId;
    private int _consecutiveSessionIdMatches;

    public static double CalculateShannonEntropy(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return 0.0;
        }

        Span<int> frequencies = stackalloc int[256];
        foreach (var b in data)
        {
            frequencies[b]++;
        }

        var entropy = 0.0;
        var len = (double)data.Length;

        for (var i = 0; i < 256; i++)
        {
            var count = frequencies[i];
            if (count > 0)
            {
                var p = count / len;
                entropy -= p * Math.Log2(p);
            }
        }

        return entropy;
    }

    public TspuPacketAudit InspectPacket(int index, ReadOnlySpan<byte> packet, bool isUdp)
    {
        var threats = TspuThreat.None;
        var detailsList = new List<string>();

        var entropy = CalculateShannonEntropy(packet);
        if (packet.Length >= 64 && entropy >= 7.45)
        {
            threats |= TspuThreat.HighEntropyCryptoAnomaly;
            detailsList.Add($"Энтропия {entropy:F2} близка к 8.0 (криптографический шум)");
        }

        if (isUdp)
        {
            if (packet.Length == 148 && packet[0] == 0x01)
            {
                threats |= TspuThreat.WireGuardHandshake;
                detailsList.Add("Сигнатура WireGuard Handshake (0x01, 148 байт)");
            }

            if (packet.Length >= 5 && (packet[0] & 0xC0) == 0xC0 && BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(1, 4)) == 0x00000001)
            {
                threats |= TspuThreat.QuicInitialThrottled;
                detailsList.Add("QUIC Initial Long Header на UDP 443 (подлежит троттлингу 80%)");
            }

            if (packet.Length >= 16)
            {
                var candidateSessionId = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(12, 4));
                if (candidateSessionId != 0)
                {
                    if (_lastObservedSessionId == candidateSessionId)
                    {
                        _consecutiveSessionIdMatches++;
                        if (_consecutiveSessionIdMatches >= 3)
                        {
                            threats |= TspuThreat.FechsueStaticSessionLeak;
                            detailsList.Add($"Утечка статического SessionID (0x{candidateSessionId:X8}) в открытом виде на смещении 12");
                        }
                    }
                    else
                    {
                        _lastObservedSessionId = candidateSessionId;
                        _consecutiveSessionIdMatches = 1;
                    }
                }
            }
        }
        else
        {
            if (packet.Length >= 8)
            {
                var totalLen = BinaryPrimitives.ReadInt32LittleEndian(packet[..4]);
                var payloadLen = BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(4, 4));
                if (totalLen == packet.Length && payloadLen > 0 && payloadLen <= totalLen - 8)
                {
                    threats |= TspuThreat.ObfuscatorStaticLengthHeader;
                    detailsList.Add($"Статический заголовок длин Obfuscator: Total={totalLen}, Payload={payloadLen}");
                }
            }

            if (packet.Length >= 5 && packet[0] == 0x16 && packet[1] == 0x03 && packet[2] is 0x01 or 0x03)
            {
                if (TryExtractTlsSni(packet, out var sni))
                {
                    threats |= TspuThreat.CleartextSniDetected;
                    detailsList.Add($"Обнаружен открытый SNI: {sni}");
                }
            }
        }

        var details = detailsList.Count > 0 ? string.Join("; ", detailsList) : "Пакет чист";
        var hexDump = Convert.ToHexString(packet.Length <= 16 ? packet : packet[..16]);
        return new TspuPacketAudit(index, packet.Length, isUdp, entropy, threats, details, hexDump);
    }

    public VirtualTspuReport AnalyzeStream(IEnumerable<ReadOnlyMemory<byte>> packets, bool isUdp)
    {
        var report = new VirtualTspuReport();
        var entropySum = 0.0;
        var idx = 0;

        foreach (var mem in packets)
        {
            var audit = InspectPacket(idx++, mem.Span, isUdp);
            report.TotalPackets++;
            entropySum += audit.Entropy;
            report.MaxEntropy = Math.Max(report.MaxEntropy, audit.Entropy);

            if (audit.DetectedThreats != TspuThreat.None)
            {
                report.BlockedCount++;
                report.TotalFlags |= audit.DetectedThreats;
                report.Violations.Add(audit);
            }
            else
            {
                report.PassedCount++;
            }
        }

        report.AverageEntropy = report.TotalPackets > 0 ? entropySum / report.TotalPackets : 0.0;

        report.Summary = report.IsStealthPassed
            ? "100% STEALTH: ТСПУ не обнаружил аномалий, трафик прошел фильтрацию."
            : $"BLOCKED: Зафиксировано {report.BlockedCount} нарушений из {report.TotalPackets} пакетов. Сработали фильтры: {report.TotalFlags}.";

        return report;
    }

    private static bool TryExtractTlsSni(ReadOnlySpan<byte> record, out string sni)
    {
        sni = string.Empty;
        if (record.Length < 43 || record[0] != 0x16)
        {
            return false;
        }

        var pos = 5;
        if (pos >= record.Length || record[pos] != 0x01)
        {
            return false;
        }

        pos += 4;
        pos += 2 + 32;

        if (pos >= record.Length)
        {
            return false;
        }
        var sessIdLen = record[pos];
        pos += 1 + sessIdLen;

        if (pos + 2 > record.Length)
        {
            return false;
        }
        var cipherLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(pos, 2));
        pos += 2 + cipherLen;

        if (pos + 1 > record.Length)
        {
            return false;
        }
        var compLen = record[pos];
        pos += 1 + compLen;

        if (pos + 2 > record.Length)
        {
            return false;
        }
        var extTotalLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(pos, 2));
        pos += 2;

        var extEnd = Math.Min(record.Length, pos + extTotalLen);
        while (pos + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(pos, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(pos + 2, 2));
            pos += 4;

            if (extType == 0x0000 && pos + extLen <= extEnd)
            {
                var sniPos = pos;
                if (sniPos + 2 <= extEnd)
                {
                    var listLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(sniPos, 2));
                    sniPos += 2;
                    if (sniPos + 3 <= extEnd && listLen >= 3)
                    {
                        var nameType = record[sniPos];
                        var nameLen = BinaryPrimitives.ReadUInt16BigEndian(record.Slice(sniPos + 1, 2));
                        sniPos += 3;
                        if (nameType == 0 && sniPos + nameLen <= extEnd)
                        {
                            sni = System.Text.Encoding.ASCII.GetString(record.Slice(sniPos, nameLen));
                            return true;
                        }
                    }
                }
            }

            pos += extLen;
        }

        return false;
    }
}
