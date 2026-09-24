using System.Net;
using System.Net.Sockets;
using obxodka.Client.Diagnostics;
using obxodka.Helpers;
using obxodka.Shared.Stealth;
using Xunit;

namespace obxodka.Client.Tests.Diagnostics;

[Trait("Category", "Integration")]
public class TspuLiveSocketTests
{
    [Fact]
    public async Task LiveUdpSocketInterceptionDetectsAndDropsThreatsAsync()
    {
        await using var harness = new TspuLiveHarness();
        const int proxyPort = 18443;
        const int mockPort = 19443;

        await harness.StartUdpLiveTestAsync(proxyPort, mockPort);

        using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            ReceiveTimeout = 2000
        };
        var targetEp = new IPEndPoint(IPAddress.Loopback, proxyPort);

        var wgPacket = new byte[148];
        wgPacket[0] = 0x01;
        Random.Shared.NextBytes(wgPacket.AsSpan(1));
        _ = await clientSocket.SendToAsync(wgPacket, targetEp);

        var authPacket = FechsueCodec.PackAuth("aabbccddeeff00112233445566778899aabbccdd", 0, out var authLen);
        _ = await clientSocket.SendToAsync(authPacket.AsMemory(0, authLen), targetEp);

        var cleanPacket = new byte[64];
        for (var i = 0; i < cleanPacket.Length; i++)
        {
            cleanPacket[i] = (byte)(i % 16);
        }
        _ = await clientSocket.SendToAsync(cleanPacket, targetEp);

        await Task.Delay(250);

        Assert.Equal(3, harness.PacketsIntercepted);
        Assert.Equal(2, harness.ThreatsDetected);
        Assert.Equal(2, harness.PacketsDropped);
        Assert.Equal(1, harness.PacketsForwarded);

        var receiveBuffer = new byte[1024];
        EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);
        var received = clientSocket.ReceiveFrom(receiveBuffer, ref senderEp);

        Assert.Equal(cleanPacket.Length, received);
        Assert.Equal(cleanPacket, receiveBuffer[..received]);
    }

    [Fact]
    public async Task LiveTcpSocketInterceptionBlocksCleartextSniAndPermitsSplitStreamAsync()
    {
        await using var harness = new TspuLiveHarness();
        const int proxyPort = 18080;
        const int mockPort = 19080;

        await harness.StartTcpLiveTestAsync(proxyPort, mockPort);

        var tlsHello = new byte[]
        {
            0x16, 0x03, 0x01, 0x00, 0x43,
            0x01, 0x00, 0x00, 0x3F,
            0x03, 0x03,
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
            0x00,
            0x00, 0x02, 0x13, 0x01,
            0x01, 0x00,
            0x00, 0x14,
            0x00, 0x00, 0x00, 0x10,
            0x00, 0x0E, 0x00, 0x00, 0x0B, 0x64, 0x69, 0x73, 0x63, 0x6F, 0x72, 0x64, 0x2E, 0x63, 0x6F, 0x6D
        };

        using (var directClient = new TcpClient())
        {
            directClient.ReceiveTimeout = 2000;
            directClient.SendTimeout = 2000;
            await directClient.ConnectAsync(IPAddress.Loopback, proxyPort);
            var stream = directClient.GetStream();
            await stream.WriteAsync(tlsHello);

            var readBuf = new byte[32];
            var exThrown = false;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var read = await stream.ReadAsync(readBuf, cts.Token);
                if (read == 0)
                {
                    exThrown = true;
                }
            }
            catch
            {
                exThrown = true;
            }

            Assert.True(exThrown);
        }

        using (var splitClient = new TcpClient())
        {
            splitClient.ReceiveTimeout = 2000;
            splitClient.SendTimeout = 2000;
            await splitClient.ConnectAsync(IPAddress.Loopback, proxyPort);
            var networkStream = splitClient.GetStream();
            using var bypass = new DpiBypassStream(networkStream, splitPosition: 2, delayMs: 10);

            var sampleRequest = "GET /index.html HTTP/1.1\r\nHost: example.com\r\n\r\n"u8.ToArray();
            await bypass.WriteAsync(sampleRequest);

            var responseBuf = new byte[sampleRequest.Length];
            var totalRead = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (totalRead < sampleRequest.Length)
            {
                var r = await networkStream.ReadAsync(responseBuf.AsMemory(totalRead, sampleRequest.Length - totalRead), cts.Token);
                if (r == 0)
                {
                    break;
                }
                totalRead += r;
            }

            Assert.Equal(sampleRequest.Length, totalRead);
            Assert.Equal(sampleRequest, responseBuf);
        }
    }
}
