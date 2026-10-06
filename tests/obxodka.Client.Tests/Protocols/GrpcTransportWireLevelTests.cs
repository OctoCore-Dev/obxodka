namespace obxodka.Client.Tests.Protocols;

[Trait("Category", "WireLevel")]
[Trait("Category", "Integration")]
public class StreamTransportWireLevelTests
{
    [Fact]
    public async Task StreamTransportConnectsViaStandardTlsClientHelloOnWireAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var transport = new ObxodkaStreamTransport(
            serverPort: port,
            configuredSni: "obxodka.one"
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

        Assert.True(firstRead > 5, "Server socket must receive TLS ClientHello from StreamTransport");
        Assert.Equal(0x16, buffer[0]);
        Assert.Equal(0x03, buffer[1]);
        Assert.Equal(0x01, buffer[5]);

        listener.Stop();
        await transport.DisposeAsync();
    }
}
