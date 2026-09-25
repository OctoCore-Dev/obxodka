using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace obxodka.Client.Diagnostics;

public sealed class TspuLiveHarness : IDisposable, IAsyncDisposable
{
    private readonly VirtualTspuEngine _engine = new();
    private readonly CancellationTokenSource _cts = new();
    private Socket? _udpDownstream;
    private Socket? _udpUpstream;
    private TcpListener? _tcpListener;
    private Socket? _mockUdpServer;
    private TcpListener? _mockTcpServer;
    private int _packetCounter;

    public int PacketsIntercepted => _packetCounter;
    public int ThreatsDetected { get; private set; }
    public int PacketsDropped { get; private set; }
    public int PacketsForwarded { get; private set; }
    public bool EnforceBlocking { get; set; } = true;
    public List<TspuPacketAudit> AuditHistory { get; } = [];

    public event Action<TspuPacketAudit, bool>? OnPacketInspected;

    public async Task StartUdpLiveTestAsync(int listenPort, int targetPort)
    {
        _mockUdpServer = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _mockUdpServer.Bind(new IPEndPoint(IPAddress.Loopback, targetPort));

        _udpDownstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpDownstream.Bind(new IPEndPoint(IPAddress.Loopback, listenPort));

        _udpUpstream = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpUpstream.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        _ = Task.Run(() => RunMockUdpEchoAsync(_mockUdpServer, _cts.Token));
        _ = Task.Run(() => RunUdpProxyAsync(_udpDownstream, _udpUpstream, new IPEndPoint(IPAddress.Loopback, targetPort), _cts.Token));

        await Task.Yield();
    }

    public async Task StartTcpLiveTestAsync(int listenPort, int targetPort)
    {
        _mockTcpServer = new TcpListener(IPAddress.Loopback, targetPort);
        _mockTcpServer.Start();

        _tcpListener = new TcpListener(IPAddress.Loopback, listenPort);
        _tcpListener.Start();

        _ = Task.Run(() => RunMockTcpEchoAsync(_mockTcpServer, _cts.Token));
        _ = Task.Run(() => RunTcpProxyAsync(_tcpListener, targetPort, _cts.Token));

        await Task.Yield();
    }

    private async Task RunUdpProxyAsync(Socket downstream, Socket upstream, IPEndPoint targetEp, CancellationToken ct)
    {
        var clientBuf = ArrayPool<byte>.Shared.Rent(65535);
        var serverBuf = ArrayPool<byte>.Shared.Rent(65535);
        EndPoint lastClientEp = new IPEndPoint(IPAddress.Any, 0);

        var upstreamTask = Task.Run(async () =>
        {
            EndPoint serverSender = new IPEndPoint(IPAddress.Any, 0);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var result = await upstream.ReceiveFromAsync(serverBuf.AsMemory(), serverSender, ct);
                    if (lastClientEp is IPEndPoint clientIp && clientIp.Port != 0)
                    {
                        _ = await downstream.SendToAsync(serverBuf.AsMemory(0, result.ReceivedBytes), clientIp, ct);
                    }
                }
            }
            catch
            {
            }
        }, ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await downstream.ReceiveFromAsync(clientBuf.AsMemory(), lastClientEp, ct);
                var receivedCount = result.ReceivedBytes;
                lastClientEp = result.RemoteEndPoint;

                var idx = Interlocked.Increment(ref _packetCounter);
                var audit = _engine.InspectPacket(idx, clientBuf.AsSpan(0, receivedCount), isUdp: true);

                lock (AuditHistory)
                {
                    AuditHistory.Add(audit);
                }

                var isBlocked = audit.DetectedThreats != TspuThreat.None;
                if (isBlocked)
                {
                    ThreatsDetected++;
                }

                if (isBlocked && EnforceBlocking)
                {
                    PacketsDropped++;
                    OnPacketInspected?.Invoke(audit, true);
                    continue;
                }

                PacketsForwarded++;
                OnPacketInspected?.Invoke(audit, false);

                _ = await upstream.SendToAsync(clientBuf.AsMemory(0, receivedCount), targetEp, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(clientBuf);
            ArrayPool<byte>.Shared.Return(serverBuf);
            try
            {
                await upstreamTask;
            }
            catch
            {
            }
        }
    }

    private static async Task RunMockUdpEchoAsync(Socket mockServer, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65535);
        EndPoint senderEp = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await mockServer.ReceiveFromAsync(buffer.AsMemory(), senderEp, ct);
                _ = await mockServer.SendToAsync(buffer.AsMemory(0, result.ReceivedBytes), result.RemoteEndPoint, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RunTcpProxyAsync(TcpListener listener, int targetPort, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleTcpClientAsync(client, targetPort, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    private async Task HandleTcpClientAsync(TcpClient client, int targetPort, CancellationToken ct)
    {
        using var clientDisposable = client;
        using var target = new TcpClient();
        try
        {
            await target.ConnectAsync(IPAddress.Loopback, targetPort, ct);
            using var clientStream = client.GetStream();
            using var targetStream = target.GetStream();

            using var innerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = ArrayPool<byte>.Shared.Rent(65535);

            var targetToClientTask = Task.Run(async () =>
            {
                var retBuf = ArrayPool<byte>.Shared.Rent(65535);
                try
                {
                    while (!innerCts.Token.IsCancellationRequested)
                    {
                        var read = await targetStream.ReadAsync(retBuf.AsMemory(), innerCts.Token);
                        if (read == 0)
                        {
                            break;
                        }
                        await clientStream.WriteAsync(retBuf.AsMemory(0, read), innerCts.Token);
                    }
                }
                catch
                {
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(retBuf);
                }
            }, innerCts.Token);

            try
            {
                while (!innerCts.Token.IsCancellationRequested)
                {
                    var read = await clientStream.ReadAsync(buffer.AsMemory(), innerCts.Token);
                    if (read == 0)
                    {
                        break;
                    }

                    var idx = Interlocked.Increment(ref _packetCounter);
                    var audit = _engine.InspectPacket(idx, buffer.AsSpan(0, read), isUdp: false);

                    lock (AuditHistory)
                    {
                        AuditHistory.Add(audit);
                    }

                    var isBlocked = audit.DetectedThreats != TspuThreat.None;
                    if (isBlocked)
                    {
                        ThreatsDetected++;
                    }

                    if (isBlocked && EnforceBlocking)
                    {
                        PacketsDropped++;
                        OnPacketInspected?.Invoke(audit, true);
                        client.Client.LingerState = new LingerOption(true, 0);
                        client.Close();
                        await innerCts.CancelAsync();
                        break;
                    }

                    PacketsForwarded++;
                    OnPacketInspected?.Invoke(audit, false);
                    await targetStream.WriteAsync(buffer.AsMemory(0, read), innerCts.Token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                try
                {
                    await targetToClientTask;
                }
                catch
                {
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private static async Task RunMockTcpEchoAsync(TcpListener server, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await server.AcceptTcpClientAsync(ct);
                _ = Task.Run(async () =>
                {
                    using var c = client;
                    using var stream = c.GetStream();
                    var buf = ArrayPool<byte>.Shared.Rent(65535);
                    try
                    {
                        while (!ct.IsCancellationRequested)
                        {
                            var read = await stream.ReadAsync(buf.AsMemory(), ct);
                            if (read == 0)
                            {
                                break;
                            }
                            await stream.WriteAsync(buf.AsMemory(0, read), ct);
                        }
                    }
                    catch
                    {
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buf);
                    }
                }, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (SocketException)
        {
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _cts.Cancel();
            }
            catch { }

            _udpDownstream?.Dispose();
            _udpUpstream?.Dispose();
            _mockUdpServer?.Dispose();
            _tcpListener?.Dispose();
            _mockTcpServer?.Dispose();
            _cts.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync();
        }
        catch { }

        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
