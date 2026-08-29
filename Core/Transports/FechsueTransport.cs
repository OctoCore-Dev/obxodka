namespace obxodka.Core.Transports;

public sealed partial class FechsueTransport : IVpnTransport
{
    public const int ParallelStreams = 1;
    public const int FechsueServerPort = 443;

    public string ProtocolName => "FECHSUE";
    public string Thumbprint { get; private set; } = string.Empty;

    private readonly Socket?[] _sockets = new Socket?[ParallelStreams];
    private readonly AesGcm?[] _rxCryptos = new AesGcm?[ParallelStreams];
    private readonly AesGcm?[] _txCryptos = new AesGcm?[ParallelStreams];
    private readonly Lock[] _txLocks = [new Lock()];
    private IPEndPoint? _serverEp;
    private uint _sessionId;
    private byte[] _key = new byte[32];
    private CancellationTokenSource? _cts;

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;

    public bool IsConnected => _sockets[0] is { Connected: true };

    public static Action<Socket>? OnSocketCreated { get; set; }

    public void ProtectSockets(Action<Socket> protectAction)
    {
        foreach (var s in _sockets)
        {
            if (s != null)
            {
                try
                {
                    protectAction(s);
                }
                catch { }
            }
        }
    }

    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Thumbprint = thumbprint;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(thumbprint));
        _key = hash;
        _sessionId = BinaryPrimitives.ReadUInt32LittleEndian(hash);
        for (var i = 0; i < ParallelStreams; i++)
        {
            _txCryptos[i] = new AesGcm(_key, 16);
        }

        var addresses = await Dns.GetHostAddressesAsync(serverIp, ct);
        var targetIp = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        _serverEp = new IPEndPoint(targetIp, FechsueServerPort);

        var ipTcs = new TaskCompletionSource<(string, string)>();

        for (byte i = 0; i < ParallelStreams; i++)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 16777216,
                SendBufferSize = 16777216
            };

            OnSocketCreated?.Invoke(sock);
            try
            {
                sock.Connect(_serverEp);
            }
            catch { }
            _sockets[i] = sock;
            _rxCryptos[i] = new AesGcm(_key, 16);

            StartReceiveThread(sock, _rxCryptos[i]!, ipTcs, i, _cts.Token);
        }

        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (ipTcs.Task.IsCompleted)
            {
                break;
            }

            for (byte i = 0; i < ParallelStreams; i++)
            {
                var authPacket = FechsueCodec.PackAuth(thumbprint, i, out var authLen);
                try
                {
                    if (_sockets[i] is { } s)
                    {
                        try
                        {
                            _ = s.Send(authPacket.AsSpan(0, authLen), SocketFlags.None);
                        }
                        catch
                        {
                            _ = s.SendTo(authPacket.AsSpan(0, authLen), SocketFlags.None, _serverEp);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(authPacket);
                }
            }

            var completed = await Task.WhenAny(ipTcs.Task, Task.Delay(300, ct));
            if (completed == ipTcs.Task)
            {
                break;
            }
        }

        if (!ipTcs.Task.IsCompleted)
        {
            var timeoutTask = await Task.WhenAny(ipTcs.Task, Task.Delay(2000, ct));
            if (timeoutTask != ipTcs.Task)
            {
                throw new TimeoutException("Сервер FECHSUE не ответил на авторизационное рукопожатие (порт 443 UDP).");
            }
        }

        _ = PingLoopAsync(_cts.Token);

        return await ipTcs.Task;
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (_serverEp == null)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        var sock = _sockets[0];
        var crypto = _txCryptos[0];
        var txLock = _txLocks[0];
        if (sock == null || crypto == null)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        byte[] packed;
        int totalLen;
        lock (txLock)
        {
            packed = FechsueCodec.Pack(packet, length, _sessionId, crypto, out totalLen);
        }
        ArrayPool<byte>.Shared.Return(packet);

        try
        {
            _ = sock.Send(packed.AsSpan(0, totalLen), SocketFlags.None);
        }
        catch
        {
            try
            {
                _ = sock.SendTo(packed.AsSpan(0, totalLen), SocketFlags.None, _serverEp);
            }
            catch { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packed);
        }
    }

    public Task SendPacketAsync(byte[] packet, int length)
    {
        var copy = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(packet, 0, copy, 0, length);
        SendPacketFromPool(copy, length);
        return Task.CompletedTask;
    }

    private void StartReceiveThread(Socket sock, AesGcm rxCrypto, TaskCompletionSource<(string, string)> ipTcs, byte streamId, CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = $"Fechsue-Stream-{streamId}";
            var rxBuffer = new byte[65536];
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var len = sock.Receive(rxBuffer, 0, rxBuffer.Length, SocketFlags.None);
                    if (len < FechsueCodec.Overhead)
                    {
                        continue;
                    }

                    if (!FechsueCodec.TryUnpack(rxBuffer, len, rxCrypto, out _, out var payload, out var realLen))
                    {
                        continue;
                    }

                    if (payload == null || realLen <= 0)
                    {
                        continue;
                    }

                    if (realLen >= 3 && payload[0] == 'I' && payload[1] == 'P' && payload[2] == ':')
                    {
                        var msg = Encoding.UTF8.GetString(payload, 0, realLen);
                        string ip = "", ip6 = "";
                        foreach (var part in msg.Split('|'))
                        {
                            if (part.StartsWith("IP:", StringComparison.Ordinal))
                            {
                                ip = part[3..].Trim();
                            }
                            else if (part.StartsWith("IP6:", StringComparison.Ordinal))
                            {
                                ip6 = part[4..].Trim();
                            }
                        }
                        if (!string.IsNullOrEmpty(ip))
                        {
                            _ = ipTcs.TrySetResult((ip, ip6));
                        }
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                    else if (payload[0] == 0x99 && realLen == 9)
                    {
                        var sentTicks = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(1, 8));
                        var rtt = (DateTime.UtcNow.Ticks - sentTicks) / TimeSpan.TicksPerMillisecond;
                        OnPingUpdated?.Invoke(rtt);
                        ArrayPool<byte>.Shared.Return(payload);
                    }
                    else
                    {
                        OnPacketReceived?.Invoke(payload, realLen);
                    }
                }
                catch (SocketException)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    OnConnectionDropped?.Invoke();
                    break;
                }
                catch (Exception ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    Debug.WriteLine($"[FECHSUE RX ERROR] {ex.Message}");
                    OnConnectionDropped?.Invoke();
                    break;
                }
            }
        })
        {
            IsBackground = true
        };
        thread.Start();
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var packet = ArrayPool<byte>.Shared.Rent(9);
                packet[0] = 0x99;
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(1, 8), DateTime.UtcNow.Ticks);
                SendPacketFromPool(packet, 9);
            }
            catch { }
            await Task.Delay(2000, ct);
        }
    }

    public async Task SendDisconnectSignalAsync()
    {
        if (_serverEp is null || string.IsNullOrEmpty(Thumbprint))
        {
            return;
        }

        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var discPacket = FechsueCodec.PackDisc(Thumbprint, out var len);
                try
                {
                    if (_sockets[0] is { } sock)
                    {
                        try
                        {
                            _ = sock.Send(discPacket.AsSpan(0, len), SocketFlags.None);
                        }
                        catch
                        {
                            _ = sock.SendTo(discPacket.AsSpan(0, len), SocketFlags.None, _serverEp);
                        }
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(discPacket);
                }
                await Task.Delay(15);
            }
        }
        catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await SendDisconnectSignalAsync();
        Dispose();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        for (var i = 0; i < ParallelStreams; i++)
        {
            _txCryptos[i]?.Dispose();
            _txCryptos[i] = null;
            _rxCryptos[i]?.Dispose();
            _rxCryptos[i] = null;
            _sockets[i]?.Dispose();
            _sockets[i] = null;
        }

        GC.SuppressFinalize(this);
    }
}
