namespace obxodka.Core.Transports;

public sealed partial class FechsueTransport : IVpnTransport
{
    public const int DefaultParallelStreams = 1;
    public const int FechsueServerPort = 443;

    public string ProtocolName => "FECHSUE";
    public string Thumbprint { get; private set; } = string.Empty;
    public int ParallelStreams { get; }

    private readonly Socket?[] _sockets;
    private readonly AesGcm?[] _rxCryptos;
    private readonly AesGcm?[] _txCryptos;
    private readonly Lock[] _txLocks;
    private readonly FechsueCodec.FecEncoder[] _fecEncoders;
    private readonly FechsueCodec.FecDecoder _fecDecoder = new();
    private readonly PacketDeduplicator _deduplicator = new();
    private IPEndPoint? _serverEp;
    private uint _sessionId;
    private byte[] _key = new byte[32];
    private CancellationTokenSource? _cts;
    private long _lastRxTicks = DateTime.UtcNow.Ticks;

    public FechsueTransport(int activeRays = 1)
    {
        ParallelStreams = Math.Clamp(activeRays, 1, PacketRouter.MaxRays);
        _sockets = new Socket?[ParallelStreams];
        _rxCryptos = new AesGcm?[ParallelStreams];
        _txCryptos = new AesGcm?[ParallelStreams];
        _txLocks = [.. Enumerable.Range(0, ParallelStreams).Select(_ => new Lock())];
        _fecEncoders = [.. Enumerable.Range(0, ParallelStreams).Select(_ => new FechsueCodec.FecEncoder(FechsueCodec.DefaultFecGroupSize))];
    }

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;

    private volatile bool _isConnected;
    private volatile bool _serverUsesSessionMasking;
    public bool IsConnected => _isConnected && _sockets[0] is not null;
    public bool EnableEntropyShaping { get; set; } = true;

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
        Debug.WriteLine($"[FECHSUE] Starting connection to {serverIp} ({_serverEp}), SessionId={_sessionId:X8}, ParallelStreams={ParallelStreams}");

        var ipTcs = new TaskCompletionSource<(string, string)>();

        for (byte i = 0; i < ParallelStreams; i++)
        {
            var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveBufferSize = 16777216,
                SendBufferSize = 16777216
            };
            try
            {
                sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.TypeOfService, 0x2E);
            }
            catch { }

            OnSocketCreated?.Invoke(sock);
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    const int sioUdpConnReset = -1744830452;
                    _ = sock.IOControl((IOControlCode)sioUdpConnReset, [0, 0, 0, 0], null);
                }
                catch { }
            }
            try
            {
                sock.Connect(_serverEp);
                Debug.WriteLine($"[FECHSUE] Stream #{i} connected socket to {_serverEp}. Local: {sock.LocalEndPoint}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FECHSUE] Stream #{i} sock.Connect failed: {ex.Message}");
            }
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

            Debug.WriteLine($"[FECHSUE] Sending auth handshake attempt #{attempt + 1}/20 to {_serverEp}...");
            for (byte i = 0; i < ParallelStreams; i++)
            {
                var authPacket = FechsueCodec.PackStealthAuth(thumbprint, i, out var authLen);
                try
                {
                    if (_sockets[i] is { } s)
                    {
                        try
                        {
                            var sent = s.Send(authPacket.AsSpan(0, authLen), SocketFlags.None);
                            Debug.WriteLine($"[FECHSUE] Stream #{i} sent auth datagram ({sent} bytes)");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[FECHSUE] Stream #{i} s.Send failed ({ex.Message}), trying SendTo...");
                            var sent = s.SendTo(authPacket.AsSpan(0, authLen), SocketFlags.None, _serverEp);
                            Debug.WriteLine($"[FECHSUE] Stream #{i} sent via SendTo ({sent} bytes)");
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
                Debug.WriteLine("[FECHSUE] Auth response received successfully!");
                break;
            }
        }

        if (!ipTcs.Task.IsCompleted)
        {
            Debug.WriteLine("[FECHSUE] Initial 20 attempts finished without response. Final 2s timeout wait...");
            var timeoutTask = await Task.WhenAny(ipTcs.Task, Task.Delay(2000, ct));
            if (timeoutTask != ipTcs.Task)
            {
                Debug.WriteLine($"[FECHSUE TIMEOUT] Server {_serverEp} did not reply to UDP auth handshake!");
                throw new TimeoutException($"Сервер FECHSUE не ответил на авторизационное рукопожатие (порт {FechsueServerPort} UDP).");
            }
        }

        _ = PingLoopAsync(_cts.Token);

        _isConnected = true;
        return await ipTcs.Task;
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (_serverEp == null || !_isConnected)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        PacketRouter.GetRays(packet, length, ParallelStreams, out var primaryRay, out var secondaryRay);
        var pRay = primaryRay % ParallelStreams;
        var sock = _sockets[pRay] ?? _sockets[0];
        var crypto = _txCryptos[pRay] ?? _txCryptos[0];
        var txLock = _txLocks[pRay] ?? _txLocks[0];
        if (sock == null || crypto == null)
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        var isFastTrack = secondaryRay >= 0 || length < 20;

        if (isFastTrack)
        {
            byte[] packed;
            int totalLen;
            lock (txLock)
            {
                packed = EnableEntropyShaping
                    ? FechsueCodec.PackShaped(packet, length, _sessionId, crypto, out totalLen, _serverUsesSessionMasking)
                    : FechsueCodec.Pack(packet, length, _sessionId, crypto, out totalLen, _serverUsesSessionMasking);
            }
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

            if (secondaryRay >= 0 && secondaryRay < ParallelStreams && secondaryRay != pRay)
            {
                var sSock = _sockets[secondaryRay];
                var sCrypto = _txCryptos[secondaryRay] ?? crypto;
                var sLock = _txLocks[secondaryRay] ?? txLock;
                if (sSock != null)
                {
                    byte[] secPacked;
                    int secLen;
                    lock (sLock)
                    {
                        secPacked = EnableEntropyShaping
                            ? FechsueCodec.PackShaped(packet, length, _sessionId, sCrypto, out secLen, _serverUsesSessionMasking)
                            : FechsueCodec.Pack(packet, length, _sessionId, sCrypto, out secLen, _serverUsesSessionMasking);
                    }
                    try
                    {
                        _ = sSock.Send(secPacked.AsSpan(0, secLen), SocketFlags.None);
                    }
                    catch
                    {
                        try
                        {
                            _ = sSock.SendTo(secPacked.AsSpan(0, secLen), SocketFlags.None, _serverEp);
                        }
                        catch { }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(secPacked);
                    }
                }
            }

            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        byte[] dataPacked;
        int dataLen;
        byte[]? parityPacked;
        int parityLen;

        lock (txLock)
        {
            (dataPacked, dataLen, parityPacked, parityLen) = _fecEncoders[pRay].Encode(packet, length, _sessionId, crypto, _serverUsesSessionMasking, shapeEntropy: EnableEntropyShaping);
        }
        ArrayPool<byte>.Shared.Return(packet);

        try
        {
            _ = sock.Send(dataPacked.AsSpan(0, dataLen), SocketFlags.None);
            if (parityPacked != null && parityLen > 0)
            {
                _ = sock.Send(parityPacked.AsSpan(0, parityLen), SocketFlags.None);
            }
        }
        catch
        {
            try
            {
                _ = sock.SendTo(dataPacked.AsSpan(0, dataLen), SocketFlags.None, _serverEp);
                if (parityPacked != null && parityLen > 0)
                {
                    _ = sock.SendTo(parityPacked.AsSpan(0, parityLen), SocketFlags.None, _serverEp);
                }
            }
            catch { }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(dataPacked);
            if (parityPacked != null)
            {
                ArrayPool<byte>.Shared.Return(parityPacked);
            }
        }
    }

    public Task SendPacketAsync(byte[] packet, int length)
    {
        var copy = ArrayPool<byte>.Shared.Rent(length);
        Buffer.BlockCopy(packet, 0, copy, 0, length);
        SendPacketFromPool(copy, length);
        return Task.CompletedTask;
    }

    private void HandleDecryptedPacket(byte[] payload, int realLen, TaskCompletionSource<(string, string)> ipTcs)
    {
        Volatile.Write(ref _lastRxTicks, DateTime.UtcNow.Ticks);

        if (realLen >= 3 && payload[0] == 'I' && payload[1] == 'P' && payload[2] == ':')
        {
            try
            {
                var msg = Encoding.UTF8.GetString(payload, 0, realLen);
                Debug.WriteLine($"[FECHSUE-AUTH] Received IP configuration packet: '{msg}'");
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
                    else if (part.StartsWith("WAN:", StringComparison.Ordinal))
                    {
                        var wan = part[4..].Trim();
                        if (!string.IsNullOrEmpty(wan))
                        {
                            OctopusEngine.Current.PublicWanIp = wan;
                            Debug.WriteLine($"[FECHSUE-AUTH] Detected Public WAN IP: {wan}");
                        }
                    }
                }
                if (!string.IsNullOrEmpty(ip))
                {
                    Debug.WriteLine($"[FECHSUE-AUTH] Handshake SUCCESS -> Assigned IP: {ip}, IPv6: {ip6}");
                    _ = ipTcs.TrySetResult((ip, ip6));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else if (payload[0] == 0x99 && realLen >= 9)
        {
            try
            {
                var streamIdx = realLen >= 10 ? payload[1] : (byte)0;
                if (ParallelStreams > 1 && streamIdx != 0)
                {
                    return;
                }

                var ticksOffset = realLen >= 10 ? 2 : 1;
                var sentTimestamp = BinaryPrimitives.ReadInt64LittleEndian(payload.AsSpan(ticksOffset, 8));
                var elapsedMs = (Stopwatch.GetTimestamp() - sentTimestamp) * 1000.0 / Stopwatch.Frequency;
                var rtt = (long)Math.Round(elapsedMs);
                if (rtt is >= 0 and < 10000)
                {
                    OnPingUpdated?.Invoke(Math.Max(1, rtt));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
        else
        {
            if (!_deduplicator.IsDuplicate(payload, realLen))
            {
                OnPacketReceived?.Invoke(payload, realLen);
            }
            else
            {
                ArrayPool<byte>.Shared.Return(payload);
            }
        }
    }

    private void StartReceiveThread(Socket sock, AesGcm rxCrypto, TaskCompletionSource<(string, string)> ipTcs, byte streamId, CancellationToken ct)
    {
        var thread = new Thread(() =>
        {
            Thread.CurrentThread.Priority = ThreadPriority.Highest;
            Thread.CurrentThread.Name = $"Fechsue-Stream-{streamId}";
            Debug.WriteLine($"[FECHSUE-RX-{streamId}] Receive thread started.");
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

                    if (!FechsueCodec.TryUnpackAuto(rxBuffer, len, rxCrypto, out var rxSessionId, out var payload, out var realLen))
                    {
                        continue;
                    }

                    var rawId = BinaryPrimitives.ReadUInt32LittleEndian(rxBuffer.AsSpan(12, 4));
                    if (rawId == _sessionId)
                    {
                        _serverUsesSessionMasking = false;
                    }
                    else if ((rawId ^ BinaryPrimitives.ReadUInt32LittleEndian(rxBuffer.AsSpan(0, 4))) == _sessionId)
                    {
                        _serverUsesSessionMasking = true;
                    }

                    if (payload == null || realLen <= 0)
                    {
                        continue;
                    }

                    if (!_fecDecoder.ProcessPayload(payload, realLen, out var directPkt, out var directLen, out var recPkt, out var recLen))
                    {
                        ArrayPool<byte>.Shared.Return(payload);
                        continue;
                    }
                    ArrayPool<byte>.Shared.Return(payload);

                    if (directPkt != null && directLen > 0)
                    {
                        HandleDecryptedPacket(directPkt, directLen, ipTcs);
                    }
                    if (recPkt != null && recLen > 0)
                    {
                        HandleDecryptedPacket(recPkt, recLen, ipTcs);
                    }
                }
                catch (SocketException sex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    if (sex.NativeErrorCode == 10054 ||
                        sex.SocketErrorCode == SocketError.ConnectionReset ||
                        sex.SocketErrorCode == SocketError.ConnectionRefused)
                    {
                        continue;
                    }

                    OnConnectionDropped?.Invoke();
                    break;
                }
                catch (Exception)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

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

    public Task SendPingProbeAsync()
    {
        try
        {
            if (_sockets[0] is { } sock0 && _txCryptos[0] is { } crypto0)
            {
                var packet = ArrayPool<byte>.Shared.Rent(10);
                packet[0] = 0x99;
                packet[1] = 0;
                BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(2, 8), Stopwatch.GetTimestamp());

                byte[] packed;
                int totalLen;
                lock (_txLocks[0])
                {
                    packed = EnableEntropyShaping
                        ? FechsueCodec.PackShaped(packet, 10, _sessionId, crypto0, out totalLen, _serverUsesSessionMasking)
                        : FechsueCodec.Pack(packet, 10, _sessionId, crypto0, out totalLen, _serverUsesSessionMasking);
                }
                ArrayPool<byte>.Shared.Return(packet);
                try
                {
                    _ = sock0.Send(packed.AsSpan(0, totalLen), SocketFlags.None);
                }
                catch
                {
                    try
                    {
                        if (_serverEp != null)
                        {
                            _ = sock0.SendTo(packed.AsSpan(0, totalLen), SocketFlags.None, _serverEp);
                        }
                    }
                    catch { }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(packed);
                }
            }
        }
        catch { }
        return Task.CompletedTask;
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        var loopCount = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SendPingProbeAsync();

                loopCount++;
                if (loopCount % 5 == 0 && ParallelStreams > 1)
                {
                    for (byte i = 1; i < ParallelStreams; i++)
                    {
                        if (_sockets[i] is { } sock && _txCryptos[i] is { } crypto)
                        {
                            var packet = ArrayPool<byte>.Shared.Rent(10);
                            packet[0] = 0x99;
                            packet[1] = i;
                            BinaryPrimitives.WriteInt64LittleEndian(packet.AsSpan(2, 8), Stopwatch.GetTimestamp());

                            byte[] packed;
                            int totalLen;
                            lock (_txLocks[i])
                            {
                                packed = EnableEntropyShaping
                                    ? FechsueCodec.PackShaped(packet, 10, _sessionId, crypto, out totalLen, _serverUsesSessionMasking)
                                    : FechsueCodec.Pack(packet, 10, _sessionId, crypto, out totalLen, _serverUsesSessionMasking);
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
                                    if (_serverEp != null)
                                    {
                                        _ = sock.SendTo(packed.AsSpan(0, totalLen), SocketFlags.None, _serverEp);
                                    }
                                }
                                catch { }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(packed);
                            }
                        }
                    }
                }

                var idleTicks = DateTime.UtcNow.Ticks - Volatile.Read(ref _lastRxTicks);
                if (idleTicks > TimeSpan.FromSeconds(3).Ticks && !string.IsNullOrEmpty(Thumbprint))
                {
                    for (byte i = 0; i < ParallelStreams; i++)
                    {
                        if (_sockets[i] is { } s)
                        {
                            var authPacket = FechsueCodec.PackStealthAuth(Thumbprint, i, out var authLen);
                            try
                            {
                                _ = s.Send(authPacket.AsSpan(0, authLen), SocketFlags.None);
                            }
                            catch
                            {
                                try
                                {
                                    if (_serverEp != null)
                                    {
                                        _ = s.SendTo(authPacket.AsSpan(0, authLen), SocketFlags.None, _serverEp);
                                    }
                                }
                                catch { }
                            }
                            finally
                            {
                                ArrayPool<byte>.Shared.Return(authPacket);
                            }
                        }
                    }
                }
            }
            catch { }
            await Task.Delay(1000, ct);
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
            var crypto = _txCryptos[0];
            if (crypto != null && _sessionId != 0)
            {
                for (var attempt = 0; attempt < 3; attempt++)
                {
                    var (discPacket, len) = EnableEntropyShaping
                        ? (FechsueCodec.PackEncryptedDiscShaped(_sessionId, crypto, out var sLen, _serverUsesSessionMasking), sLen)
                        : (FechsueCodec.PackEncryptedDisc(_sessionId, crypto, out var uLen, _serverUsesSessionMasking), uLen);
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
                    await Task.Delay(10);
                }
            }

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var discPacket = FechsueCodec.PackStealthDisc(Thumbprint, out var len);
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
                await Task.Delay(10);
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
        _isConnected = false;
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
