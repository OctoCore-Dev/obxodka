namespace obxodka.Core.Transports;

public interface IVpnTransport : IDisposable, IAsyncDisposable
{
    public string ProtocolName { get; }
    public bool IsConnected { get; }

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;

    public Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct);
    public void SendPacketFromPool(byte[] packet, int length);
    public Task SendDisconnectSignalAsync();
    public void ProtectSockets(Action<Socket> protectAction);
}
