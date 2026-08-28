namespace obxodka.Client.Tests.Mesh;

public class MeshRelayHandshakeTests
{
    private const uint ExpectedObxmMagic = 0x4D58424F;
    private const byte ProtocolVersion = 0x01;

    [Fact]
    public void BinaryMagic_MatchesExpectedProtocolDefinition()
    {
        var magicBytes = "OBXM"u8.ToArray();
        var magicUint = BinaryPrimitives.ReadUInt32LittleEndian(magicBytes);
        Assert.Equal(ExpectedObxmMagic, magicUint);
    }

    [Fact]
    public async Task LoopbackHandshake_SuccessfulExchangeAsync()
    {

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync();
            using var stream = incoming.GetStream();

            var prefix = new byte[5];
            await stream.ReadExactlyAsync(prefix);
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(prefix.AsSpan(0, 4));
            Assert.Equal(ExpectedObxmMagic, magic);
            Assert.Equal(ProtocolVersion, prefix[4]);

            var lenBuf = new byte[2];
            await stream.ReadExactlyAsync(lenBuf);
            var jwtLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
            var jwtBytes = new byte[jwtLen];
            if (jwtLen > 0)
            {
                await stream.ReadExactlyAsync(jwtBytes);
            }

            await stream.ReadExactlyAsync(lenBuf);
            var targetLen = BinaryPrimitives.ReadUInt16LittleEndian(lenBuf);
            var targetBytes = new byte[targetLen];
            await stream.ReadExactlyAsync(targetBytes);
            var target = Encoding.UTF8.GetString(targetBytes);
            Assert.Equal("1.1.1.1:443", target);

            var flags = new byte[1];
            await stream.ReadExactlyAsync(flags);
            Assert.Equal(0x01, flags[0]);

            await stream.WriteAsync(new byte[] { 0x01, 0x00, 0x01 });
            await stream.FlushAsync();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clientStream = await MeshRelayClient.ConnectThroughRelayAsync("127.0.0.1", port, "1.1.1.1", 443, "dummy-jwt-token", isFriend: true, cts.Token);

        Assert.NotNull(clientStream);
        await serverTask;
        clientStream.Dispose();
    }

    [Fact]
    public async Task LoopbackHandshake_WhenOverloaded_ThrowsExpectedExceptionAsync()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var incoming = await listener.AcceptTcpClientAsync();
            using var stream = incoming.GetStream();

            var buf = new byte[128];
            _ = await stream.ReadAsync(buf);

            await stream.WriteAsync(new byte[] { 0x00, 0x02, 0x01 });
            await stream.FlushAsync();
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            MeshRelayClient.ConnectThroughRelayAsync("127.0.0.1", port, "1.1.1.1", 443, "jwt", isFriend: false, cts.Token)
        );

        Assert.Contains("перегружен", ex.Message);
        await serverTask;
    }
}
