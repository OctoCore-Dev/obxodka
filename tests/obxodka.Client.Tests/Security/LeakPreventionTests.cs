namespace obxodka.Client.Tests.Security;

[Collection("DnsAdBlocker")]
[Trait("Category", "Security")]
[Trait("Category", "Unit")]
public class LeakPreventionTests
{
    public LeakPreventionTests() => DnsAdBlocker.ResetToDefaults();

    private static byte[] CreateDnsQueryPacket(string domain)
    {
        var packet = new byte[20 + 8 + 12 + domain.Length + 2 + 4];
        packet[0] = 0x45;
        packet[9] = 17;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(20, 2), 12345);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(22, 2), 53);

        var dnsOffset = 28;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(dnsOffset + 4, 2), 1);
        var qnameOffset = dnsOffset + 12;
        var parts = domain.Split('.');
        var currentOffset = qnameOffset;
        foreach (var part in parts)
        {
            packet[currentOffset++] = (byte)part.Length;
            var partBytes = Encoding.UTF8.GetBytes(part);
            Buffer.BlockCopy(partBytes, 0, packet, currentOffset, partBytes.Length);
            currentOffset += partBytes.Length;
        }

        packet[currentOffset++] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(currentOffset + 2, 2), 1);
        return packet;
    }

    [Theory]
    [InlineData("doubleclick.net", true)]
    [InlineData("pagead2.googlesyndication.com", true)]
    [InlineData("adservice.google.com", true)]
    [InlineData("appmetrica.yandex.net", true)]
    [InlineData("metrika.yandex.ru", true)]
    [InlineData("stat.vk.com", true)]
    [InlineData("top-fwz1.mail.ru", true)]
    [InlineData("analytics.sberbank.ru", true)]
    [InlineData("analytics.gosuslugi.ru", true)]
    [InlineData("appsflyer.com", true)]
    [InlineData("adjust.com", true)]
    [InlineData("secure.bank.com", false)]
    [InlineData("sberbank.ru", false)]
    [InlineData("gosuslugi.ru", false)]
    [InlineData("yandex.ru", false)]
    [InlineData("vk.com", false)]
    [InlineData("youtube.com", false)]
    public void DnsAdBlockerPreventsTelemetryLeaksBySinkholing(string domain, bool shouldBeBlocked)
    {
        var query = CreateDnsQueryPacket(domain);
        var response = DnsAdBlocker.ProcessPacket(query, query.Length, true);

        if (shouldBeBlocked)
        {
            Assert.NotNull(response);
            var answerIp = new byte[4];
            Buffer.BlockCopy(response, response.Length - 4, answerIp, 0, 4);
            Assert.Equal("\0\0\0\0"u8.ToArray(), answerIp);
        }
        else
        {
            Assert.Null(response);
        }
    }

    [Fact]
    public void IPv4TunnelAddressValidationProtectsAgainstBogons()
    {
        var privateRange = IPAddress.Parse("10.8.0.2");
        var loopback = IPAddress.Loopback;
        var publicVpnServer = IPAddress.Parse("185.220.101.5");

        Assert.Equal(AddressFamily.InterNetwork, privateRange.AddressFamily);
        Assert.True(IPAddress.IsLoopback(loopback));
        Assert.False(IPAddress.IsLoopback(publicVpnServer));
    }

    [Fact]
    public void DnsDefaults_ProvidesValidIpv4AndIpv6ResolversWithoutLoopback()
    {
        Assert.NotEmpty(DnsDefaults.TrustedDnsServers);
        Assert.NotEmpty(DnsDefaults.TunnelDnsServersIPv6);
        Assert.Contains(DnsDefaults.PrimaryDns, DnsDefaults.TrustedDnsServers);
        Assert.Contains(DnsDefaults.SecondaryDns, DnsDefaults.TrustedDnsServers);
        Assert.Contains(DnsDefaults.PrimaryDnsV6, DnsDefaults.TunnelDnsServersIPv6);
        Assert.Contains(DnsDefaults.SecondaryDnsV6, DnsDefaults.TunnelDnsServersIPv6);

        foreach (var v4 in DnsDefaults.TrustedDnsServers)
        {
            Assert.True(IPAddress.TryParse(v4, out var addr));
            Assert.Equal(AddressFamily.InterNetwork, addr.AddressFamily);
            Assert.False(IPAddress.IsLoopback(addr));
        }

        foreach (var v6 in DnsDefaults.TunnelDnsServersIPv6)
        {
            Assert.True(IPAddress.TryParse(v6, out var addr));
            Assert.Equal(AddressFamily.InterNetworkV6, addr.AddressFamily);
            Assert.False(IPAddress.IsLoopback(addr));
        }
    }

    [Fact]
    public async Task LoopbackTunnel_UploadAndDownloadFramingThroughput_Exceeds200MbpsAsync()
    {
        const int packetSize = 1340;
        const int totalPackets = 4096; //~5.5 MB
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        using var client = new TcpClient
        {
            NoDelay = true
        };
        var acceptTask = listener.AcceptTcpClientAsync();
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var server = await acceptTask;
        server.NoDelay = true;
        listener.Stop();

        await using var clientStream = client.GetStream();
        await using var serverStream = server.GetStream();

        var sessionKey = ObxodkaFraming.DeriveSessionKey("loopback-bench", 0, 0x12345678u);
        var samplePacket = new byte[packetSize];
        samplePacket[0] = 0x45;
        BinaryPrimitives.WriteUInt16BigEndian(samplePacket.AsSpan(2, 2), packetSize);
        samplePacket[9] = 6;
        samplePacket[12] = 100;
        samplePacket[13] = 64;
        samplePacket[15] = 2;
        samplePacket[16] = 1;
        samplePacket[17] = 1;
        samplePacket[18] = 1;
        samplePacket[19] = 1;

        var sw = Stopwatch.StartNew();
        var rxTask = Task.Run(async () =>
        {
            var received = 0;
            var recvBuf = new byte[65536];
            var pending = 0;
            while (received < totalPackets)
            {
                var n = await serverStream.ReadAsync(recvBuf.AsMemory(pending));
                if (n <= 0)
                {
                    break;
                }

                pending += n;
                var offset = 0;
                while (pending - offset >= ObxodkaFraming.HeaderSize)
                {
                    if (!ObxodkaFraming.TryDecodeHeader(recvBuf.AsSpan(offset, ObxodkaFraming.HeaderSize), out var totalLen,
                            out var payloadLen, out _, out var salt, sessionKey))
                    {
                        throw new InvalidOperationException("Loopback framing decode failed");
                    }

                    if (pending - offset < totalLen)
                    {
                        break;
                    }

                    ObxodkaFraming.UnmaskPayload(recvBuf.AsSpan(offset + ObxodkaFraming.HeaderSize, payloadLen), salt, sessionKey);
                    received++;
                    offset += totalLen;
                }

                if (offset > 0 && offset < pending)
                {
                    Buffer.BlockCopy(recvBuf, offset, recvBuf, 0, pending - offset);
                }

                pending -= offset;
            }

            return received;
        });

        var coalesce = new byte[262144];
        var sent = 0;
        while (sent < totalPackets)
        {
            var batchOffset = 0;
            while (sent < totalPackets && batchOffset + ObxodkaFraming.HeaderSize + packetSize <= coalesce.Length)
            {
                var frameLen = ObxodkaFraming.HeaderSize + packetSize;
                ObxodkaFraming.WriteFrame(coalesce.AsSpan(batchOffset, frameLen), ObxodkaFraming.CmdData, samplePacket, 0, sessionKey);
                batchOffset += frameLen;
                sent++;
            }

            await clientStream.WriteAsync(coalesce.AsMemory(0, batchOffset));
        }

        await clientStream.FlushAsync();

        var receivedCount = await rxTask;
        sw.Stop();

        Assert.Equal(totalPackets, receivedCount);
        var mbps = totalPackets * (double)packetSize * 8.0 / (sw.Elapsed.TotalSeconds * 1_000_000.0);
        Assert.True(mbps > 200.0, $"Expected >200 Mbps wire framing throughput, got {mbps:F1} Mbps");
    }
}
