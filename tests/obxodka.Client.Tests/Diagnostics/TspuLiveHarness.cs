namespace obxodka.Client.Diagnostics;

//тестовый стенд для симуляции перехвата и фильтрации трафика тспу
public sealed class TspuLiveHarness : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly VirtualTspuEngine _engine = new();
    private TcpListener? _mockTcpServer;
    private Socket? _mockUdpServer;
    private int _packetCounter;
    private TcpListener? _tcpListener;
    private Socket? _udpDownstream;
    private Socket? _udpUpstream;

    public int PacketsIntercepted => _packetCounter;
    public int ThreatsDetected { get; private set; }
    public int PacketsDropped { get; private set; }
    public int PacketsForwarded { get; private set; }
    public bool EnforceBlocking { get; set; } = true;
    public List<TspuPacketAudit> AuditHistory { get; } = [];

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _cts.CancelAsync();
        }
        catch (ObjectDisposedException)
        {
            //подавление ошибки асинхронной отмены токена стенда
        }

        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

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
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                //штатная остановка приемника upstream udp прокси
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
                var audit = _engine.InspectPacket(idx, clientBuf.AsSpan(0, receivedCount), true);

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
            //штатная остановка эхо сервера udp
        }
        catch (SocketException)
        {
            //закрытие сокета эхо сервера udp
        }
        finally
        {
            try
            {
                await upstreamTask;
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                //штатная остановка приемника upstream udp прокси
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(clientBuf);
                ArrayPool<byte>.Shared.Return(serverBuf);
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
            //штатная остановка эхо сервера udp
        }
        catch (SocketException)
        {
            //закрытие сокета эхо сервера udp
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
            //штатная остановка эхо сервера udp
        }
        catch (SocketException)
        {
            //закрытие сокета эхо сервера udp
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
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException
                                               or InvalidOperationException)
                {
                    //закрытие канала чтения ответа от целевого tcp сервера
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
                    var audit = _engine.InspectPacket(idx, buffer.AsSpan(0, read), false);

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
                try
                {
                    await innerCts.CancelAsync();
                    await targetToClientTask;
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException
                                               or InvalidOperationException)
                {
                    //закрытие канала чтения ответа от целевого tcp сервера
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            //штатная отмена обработки tcp клиента стенда
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
        {
            //закрытие клиентского соединения стенда
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
                    catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException
                                                   or ObjectDisposedException)
                    {
                        //закрытие клиентского подключения к эхо серверу tcp
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
            //штатная остановка эхо сервера udp
        }
        catch (SocketException)
        {
            //закрытие сокета эхо сервера udp
        }
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                _cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                //подавление ошибки отмены токена стенда
            }

            _udpDownstream?.Dispose();
            _udpUpstream?.Dispose();
            _mockUdpServer?.Dispose();
            _tcpListener?.Dispose();
            _mockTcpServer?.Dispose();
            _cts.Dispose();
        }
    }
}
