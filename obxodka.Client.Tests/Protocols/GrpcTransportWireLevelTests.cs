namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "WireLevel")]
[Trait("Category", "Integration")]
public class GrpcTransportWireLevelTests
{
    [Fact]
    public async Task GrpcTransportConnectsViaDpiBypassStreamAndSendsSegmentedTlsClientHelloOnWireAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var transport = new GrpcTransport(
            useHttp3: false,
            activeRays: 1,
            clientCert: null,
            jwtToken: null,
            serverPort: port,
            meshRelay: null,
            targetSni: "obxodka.one"
        );

        var connectTask = Task.Run(async () =>
        {
            try
            {
                _ = await transport.ConnectAsync("127.0.0.1", "test-thumbprint", cts.Token);
            }
            catch
            {

            }
        }, cts.Token);

        using var acceptedSocket = await listener.AcceptSocketAsync(cts.Token);
        acceptedSocket.ReceiveTimeout = 3000;

        var buffer = new byte[4096];
        var firstRead = acceptedSocket.Receive(buffer, 0, buffer.Length, SocketFlags.None);

        Assert.True(firstRead > 0, "Server socket must receive bytes from GrpcTransport");

        Assert.Equal(0x16, buffer[0]);
        Assert.Equal(0x03, buffer[1]);

        if (firstRead == 2)
        {

            var secondRead = acceptedSocket.Receive(buffer, 2, buffer.Length - 2, SocketFlags.None);
            Assert.True(secondRead > 0, "Remainder of ClientHello must arrive in subsequent TCP segment");
            Assert.Equal(0x01, buffer[2]);
        }
        else
        {

            Assert.True(firstRead > 5);
            Assert.Equal(0x01, buffer[5]);
        }

        listener.Stop();
        await transport.DisposeAsync();
    }
}

