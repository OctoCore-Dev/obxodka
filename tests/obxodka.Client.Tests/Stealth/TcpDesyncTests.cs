using System.Net;
using System.Net.Sockets;
using obxodka.Helpers;
using obxodka.Shared.Stealth;
using Xunit;

namespace obxodka.Client.Tests.Stealth;

[Trait("Category", "Unit")]
public class TcpDesyncTests
{
    [Fact]
    public void SetAndGetSocketTtlOperatesCorrectly()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var ok = TcpDesyncHelper.TrySetSocketTtl(socket, 3);
        if (ok)
        {
            var ttl = TcpDesyncHelper.GetSocketTtl(socket);
            Assert.Equal(3, ttl);

            _ = TcpDesyncHelper.TrySetSocketTtl(socket, 64);
            Assert.Equal(64, TcpDesyncHelper.GetSocketTtl(socket));
        }
    }

    [Fact]
    public async Task PerformTtlDesyncSendsFakeSegmentAndRestoresTtlAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            var stream = serverClient.GetStream();
            var buf = new byte[256];
            var read = await stream.ReadAsync(buf);
            return (read, buf[..read]);
        });

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        var originalTtl = TcpDesyncHelper.GetSocketTtl(client);
        var desyncSuccess = TcpDesyncHelper.PerformTtlDesync(client, desyncTtl: 3, realTtl: 64, fakePayload: "GET / HTTP/1.1\r\n\r\n"u8);
        Assert.True(desyncSuccess);

        var finalTtl = TcpDesyncHelper.GetSocketTtl(client);
        Assert.Equal(originalTtl > 0 ? originalTtl : 64, finalTtl);

        var (bytesReceived, receivedData) = await serverTask;
        listener.Stop();

        Assert.True(bytesReceived > 0);
        Assert.Equal("GET / HTTP/1.1\r\n\r\n"u8.ToArray(), receivedData);
    }

    [Fact]
    public async Task SendWithTtlDesyncAsyncTransfersFullPayloadPreservingOrderAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var payload = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x20, 0x01, 0x02, 0x03, 0x04 };

        var serverTask = Task.Run(async () =>
        {
            using var serverClient = await listener.AcceptTcpClientAsync();
            var stream = serverClient.GetStream();
            var buf = new byte[256];
            var totalRead = 0;
            while (totalRead < payload.Length)
            {
                var r = await stream.ReadAsync(buf.AsMemory(totalRead, payload.Length - totalRead));
                if (r == 0)
                {
                    break;
                }
                totalRead += r;
            }
            return (totalRead, buf[..totalRead]);
        });

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        var sent = await TcpDesyncHelper.SendWithTtlDesyncAsync(client, payload, splitPosition: 2, desyncTtl: 3, realTtl: 64, delayMs: 5);
        Assert.True(sent);

        var (receivedCount, receivedBytes) = await serverTask;
        listener.Stop();

        Assert.Equal(payload.Length, receivedCount);
        Assert.Equal(payload, receivedBytes);
    }

    [Fact]
    public async Task DpiBypassStreamWithChameleonAndTtlDesyncIntegratesSeamlesslyAsync()
    {
        using var mem = new MemoryStream();
        var chameleon = new ChameleonState(0xFEEDFACE);

        await using var bypass = new DpiBypassStream(
            mem,
            splitPosition: 2,
            delayMs: 1,
            chameleon: chameleon,
            socket: null,
            enableTtlDesync: false,
            desyncTtl: 3);

        var testData = new byte[] { 0x16, 0x03, 0x01, 0x00, 0x10, 0xAA, 0xBB, 0xCC, 0xDD };
        await bypass.WriteAsync(testData);
        await bypass.FlushAsync();

        Assert.Equal(testData, mem.ToArray());
    }
}
