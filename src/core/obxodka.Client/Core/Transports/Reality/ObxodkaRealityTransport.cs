using obxodka.Shared.Stealth.Tls13;

namespace obxodka.Core.Transports;

//прямой стелс-транспорт obx-v8 поверх собственного стека rfc 8446 tls 1.3 chromium с криптографическим тегом reality в session_id
public sealed class ObxodkaRealityTransport(int serverPort = 443, string? configuredCoverSni = null, string? preferredAssignedIp = null)
    : IVpnTransport, IHealthAwareTransport
{
    //нейтральные европейские и глобальные домены-доноры без упоминания бренда vpn
    private static readonly string[] s_coverDonorHosts =
    [
        "cdn.jsdelivr.net",
        "fastly.jsdelivr.net",
        "unpkg.com"
    ];

    private readonly string? _configuredCoverSni = configuredCoverSni;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<int>> _pendingMtuProbes = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pendingMtuSyncAcks = new();
    private readonly List<int> _receivedBurstMtus = [];
    private readonly int _serverPort = serverPort > 0 ? serverPort : 443;
    private readonly PriorityPacketQueue _txQueue = new(8192);

    private Task[] _activeLoopTasks = [];
    private string _activeServerIp = string.Empty;
    private volatile string? _assignedIp = preferredAssignedIp;
    private bool _connected;
    private CancellationTokenSource? _cts;
    private int _disposed;
    private int _dropSignaled;
    private TaskCompletionSource<(string ip, string ip6)> _ipTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action<Socket>? _protectAction;
    private uint _sessionKey;
    private Tls13CryptoStream? _tlsStream;

    public string ActiveSni { get; private set; } = string.Empty;
    public TlsBrowserProfile ActiveBrowserProfile { get; private set; } = TlsBrowserProfile.Chrome;
    public static Action<Socket>? OnSocketCreated { get; set; }

    public string? RemoteConnectedEndpointIp => string.IsNullOrWhiteSpace(_activeServerIp) ? null : _activeServerIp;

    public void ReportUnhealthyEndpoint() => OctopusEngine.MarkNodeByteCapped(_activeServerIp);

    public string ProtocolName => "OBXODKA-REALITY";

    public bool IsConnected => Volatile.Read(ref _connected);

    public event Action<byte[], int>? OnPacketReceived;
    public event Action<long>? OnPingUpdated;
    public event Action? OnConnectionDropped;
    public event Action<int>? OnMtuUpdated;

    public void ProtectSockets(Action<Socket> protectAction) => _protectAction = protectAction;

    [SuppressMessage("Reliability", "CA2000:Ликвидировать объекты перед потерей области",
        Justification = "Владение сокетом и потоком передается в Tls13CryptoStream (_tlsStream) или гарантированно освобождается в CleanupFailedAttemptAsync")]
    public async Task<(string ip, string ip6)> ConnectAsync(string serverIp, string thumbprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        _activeServerIp = serverIp;
        var spkiPin = !string.IsNullOrWhiteSpace(OctopusEngine.DynamicSslPublicKeyHash)
            ? OctopusEngine.DynamicSslPublicKeyHash
            : !string.IsNullOrWhiteSpace(OctopusEngine.DirectOriginCertHash)
                ? OctopusEngine.DirectOriginCertHash
                : AppSecrets.DirectServerSpkiPin;

        var realityPub = ObxRealityAuth.DeriveNodeRealityPublicKey(spkiPin);
        var coverCandidates = BuildCoverSniList(_configuredCoverSni);
        Exception? lastEx = null;

        for (var idx = 0; idx < coverCandidates.Length; idx++)
        {
            var coverSni = coverCandidates[idx];
            ActiveSni = coverSni;
            Volatile.Write(ref _dropSignaled, 0);
            _cts = new CancellationTokenSource();
            _ipTcs = new TaskCompletionSource<(string ip, string ip6)>(TaskCreationOptions.RunContinuationsAsynchronously);

            Socket? socket = null;
            Tls13CryptoStream? tls = null;
            var ownershipTransferred = false;

            try
            {
                using var attemptCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3800));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token, attemptCts.Token);

                var targetIp = IPAddress.TryParse(serverIp, out var parsed)
                    ? parsed
                    : await ApiService.ResolveHostAntiPoisonAsync(serverIp, linkedCts.Token).ConfigureAwait(false);
                ActiveBrowserProfile = RoutingHello.GetProfileForAttempt(idx);

                socket = new Socket(targetIp.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                    SendBufferSize = 4194304,
                    ReceiveBufferSize = 4194304
                };
                (_protectAction ?? OnSocketCreated ?? OctopusEngine.Current.ProtectSocket).Invoke(socket);

                await socket.ConnectAsync(new IPEndPoint(targetIp, _serverPort), linkedCts.Token).ConfigureAwait(false);

                var netStream = new NetworkStream(socket, true);
                var isLoopback = IPAddress.IsLoopback(targetIp);
                Stream transportWire = isLoopback
                    ? netStream
                    : new DpiBypassStream(netStream, delayMs: RandomNumberGenerator.GetInt32(8, 18), bypassEnabled: true)
                    {
                        Mode = DpiBypassMode.TlsRecordMidsld
                    };

                tls = await Tls13CryptoStream.ConnectClientAsync(
                        transportWire,
                        coverSni,
                        realityPub,
                        spkiPin,
                        leaveInnerOpen: false,
                        linkedCts.Token)
                    .ConfigureAwait(false);

                var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var sessionSeed = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
                var sessionKey = ObxodkaFraming.DeriveSessionKey(thumbprint, 0, sessionSeed);
                Volatile.Write(ref _sessionKey, sessionKey);

                var hsStr = OctopusEngine.Current.BuildAuthenticatedHandshakeString(
                    thumbprint,
                    _assignedIp,
                    0,
                    SmartMtuEngine.CurrentMtu,
                    sessionSeed,
                    nowSec,
                    includePublicKey: true);
                var hsPayload = Encoding.UTF8.GetBytes(hsStr);
                var hsFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdHandshake, hsPayload, out var hsLen, SmartMtuEngine.CurrentMtu);
                var chaffFrame = ObxodkaFraming.Pack(ObxodkaFraming.CmdChaff, [], out var chaffLen, SmartMtuEngine.CurrentMtu, sessionKey);

                try
                {
                    await tls.WriteAsync(hsFrame.AsMemory(0, hsLen), linkedCts.Token).ConfigureAwait(false);
                    await tls.WriteAsync(chaffFrame.AsMemory(0, chaffLen), linkedCts.Token).ConfigureAwait(false);
                    await tls.FlushAsync(linkedCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(hsFrame);
                    ArrayPool<byte>.Shared.Return(chaffFrame);
                }

                _tlsStream = tls;
                ownershipTransferred = true;

                var readerTask = Task.Run(() => ReaderLoopAsync(tls, sessionKey, _cts.Token), _cts.Token);
                var writerTask = Task.Run(() => WriterLoopAsync(tls, hsLen + chaffLen, _cts.Token), _cts.Token);
                _activeLoopTasks = [readerTask, writerTask];

                await using (linkedCts.Token.Register(() => _ = _ipTcs.TrySetCanceled()))
                {
                    var assigned = await _ipTcs.Task.ConfigureAwait(false);
                    Volatile.Write(ref _connected, true);
                    var pingTask = Task.Run(() => PingLoopAsync(_cts.Token), _cts.Token);
                    _activeLoopTasks = [readerTask, writerTask, pingTask];
                    AppLogger.Log(
                        $"[OBXODKA-REALITY] Connected to {serverIp}:{_serverPort} (CoverSNI='{coverSni}')! Assigned IPv4={assigned.ip}");
                    return assigned;
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && idx + 1 < coverCandidates.Length)
            {
                lastEx = ex;
                AppLogger.LogWarning($"[OBXODKA-REALITY] Cover candidate '{coverSni}' failed: {ex.Message}");
                await CleanupFailedAttemptAsync(tls, socket, ownershipTransferred).ConfigureAwait(false);
            }
            catch
            {
                await CleanupFailedAttemptAsync(tls, socket, ownershipTransferred).ConfigureAwait(false);
                throw;
            }
        }

        throw lastEx ?? new TimeoutException("Reality connection failed across cover SNI candidates.");
    }

    public void SendPacketFromPool(byte[] packet, int length)
    {
        if (length <= 0 || !Volatile.Read(ref _connected))
        {
            ArrayPool<byte>.Shared.Return(packet);
            return;
        }

        if (!_txQueue.TryEnqueue(packet, length))
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    public Task SendDisconnectSignalAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var discBuf = ArrayPool<byte>.Shared.Rent(2);
        discBuf[0] = 0xEE;
        discBuf[1] = 0x99;
        if (!_txQueue.TryEnqueue(discBuf, 2))
        {
            ArrayPool<byte>.Shared.Return(discBuf);
        }

        return Task.CompletedTask;
    }

    public Task SendPingProbeAsync()
    {
        if (!Volatile.Read(ref _connected))
        {
            return Task.CompletedTask;
        }

        var pingPayload = ArrayPool<byte>.Shared.Rent(8);
        BinaryPrimitives.WriteInt64LittleEndian(pingPayload.AsSpan(0, 8), Stopwatch.GetTimestamp());
        if (!_txQueue.TryEnqueue(pingPayload, 8))
        {
            ArrayPool<byte>.Shared.Return(pingPayload);
        }

        return Task.CompletedTask;
    }

    public async Task<bool> ProbeMtuAsync(int size, CancellationToken ct)
    {
        if (!Volatile.Read(ref _connected) || _cts?.IsCancellationRequested == true || size < 16)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeTs = Stopwatch.GetTimestamp();
        while (!_pendingMtuProbes.TryAdd(probeTs, tcs))
        {
            probeTs++;
        }

        var payloadLen = Math.Max(16, size - 2);
        var queueLen = payloadLen + 2;
        var buf = ArrayPool<byte>.Shared.Rent(queueLen);
        try
        {
            buf[0] = 0xAA;
            buf[1] = 0xBB;
            BinaryPrimitives.WriteInt64LittleEndian(buf.AsSpan(2, 8), probeTs);
            buf[10] = 0x11;
            RandomNumberGenerator.Fill(buf.AsSpan(11, queueLen - 11));

            if (!_txQueue.TryEnqueue(buf, queueLen))
            {
                ArrayPool<byte>.Shared.Return(buf);
                return false;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(1200));
            await using var reg = linkedCts.Token.Register(() => _ = tcs.TrySetResult(0));

            var returnedLen = await tcs.Task.ConfigureAwait(false);
            return returnedLen >= payloadLen;
        }
        finally
        {
            _ = ((IDictionary<long, TaskCompletionSource<int>>)_pendingMtuProbes).Remove(probeTs);
        }
    }

    public async Task SendMtuSyncAsync(int mtu)
    {
        if (!Volatile.Read(ref _connected))
        {
            return;
        }

        var ackTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingMtuSyncAcks[mtu] = ackTcs;

        var buf = ArrayPool<byte>.Shared.Rent(4);
        buf[0] = 0xCC;
        buf[1] = 0xDD;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(2, 2), (ushort)mtu);
        if (!_txQueue.TryEnqueue(buf, 4))
        {
            ArrayPool<byte>.Shared.Return(buf);
            _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
            return;
        }

        try
        {
            _ = await ackTcs.Task.WaitAsync(TimeSpan.FromMilliseconds(900)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
        finally
        {
            _ = _pendingMtuSyncAcks.TryRemove(mtu, out _);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));
        _cts?.Cancel();
        _cts?.Dispose();
        _tlsStream?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        using var cts = Interlocked.Exchange(ref _cts, null);
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        var loops = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(1.2), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _txQueue.Dispose();
        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));
        if (_tlsStream is not null)
        {
            await _tlsStream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string[] BuildCoverSniList(string? configured) =>
        !string.IsNullOrWhiteSpace(configured) &&
        !IPAddress.TryParse(configured, out _) &&
        !SniEngine.IsInternalBrandHost(configured)
            ? [configured.Trim(), .. s_coverDonorHosts]
            : s_coverDonorHosts;

    private async Task CleanupFailedAttemptAsync(Tls13CryptoStream? tls, Socket? socket, bool ownershipTransferred)
    {
        Volatile.Write(ref _connected, false);
        using var attemptCts = Interlocked.Exchange(ref _cts, null);
        if (attemptCts is not null)
        {
            await attemptCts.CancelAsync().ConfigureAwait(false);
        }

        var loops = _activeLoopTasks;
        _activeLoopTasks = [];
        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromMilliseconds(800), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        _txQueue.DrainAndReturn(static b => ArrayPool<byte>.Shared.Return(b));
        if (!ownershipTransferred)
        {
            if (tls is not null)
            {
                await tls.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                socket?.Dispose();
            }
        }

        var activeTls = Interlocked.Exchange(ref _tlsStream, null);
        if (activeTls is not null)
        {
            await activeTls.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ReaderLoopAsync(Tls13CryptoStream stream, uint sessionKey, CancellationToken ct)
    {
        var rxBuf = ArrayPool<byte>.Shared.Rent(131072);
        var rxFilled = 0;
        var rxOffset = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var readCount = await stream.ReadAsync(rxBuf.AsMemory(rxFilled, rxBuf.Length - rxFilled), ct).ConfigureAwait(false);
                if (readCount <= 0)
                {
                    break;
                }

                rxFilled += readCount;

                while (rxFilled - rxOffset >= ObxodkaFraming.HeaderSize)
                {
                    var hdrSpan = rxBuf.AsSpan(rxOffset, ObxodkaFraming.HeaderSize);
                    var activeKey = sessionKey;
                    if (!ObxodkaFraming.TryDecodeHeader(hdrSpan, out var totalLen, out var payloadLen, out var cmd, out var salt, activeKey))
                    {
                        if (activeKey != 0 && ObxodkaFraming.TryDecodeHeader(hdrSpan, out totalLen, out payloadLen, out cmd, out salt, 0u))
                        {
                            activeKey = 0u;
                        }
                        else
                        {
                            return;
                        }
                    }

                    if (rxFilled - rxOffset < totalLen)
                    {
                        break;
                    }

                    var payloadSpan = rxBuf.AsSpan(rxOffset + ObxodkaFraming.HeaderSize, payloadLen);
                    if (cmd != ObxodkaFraming.CmdChaff && payloadLen > 0)
                    {
                        ObxodkaFraming.UnmaskPayload(payloadSpan, salt, activeKey);
                    }

                    rxOffset += totalLen;
                    HandleIncomingFrame(cmd, payloadSpan, payloadLen);
                }

                if (rxOffset <= 0)
                {
                    continue;
                }

                var remaining = rxFilled - rxOffset;
                if (remaining > 0)
                {
                    rxBuf.AsSpan(rxOffset, remaining).CopyTo(rxBuf);
                }

                rxFilled = remaining;
                rxOffset = 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException or ObjectDisposedException)
        {
            AppLogger.LogWarning($"[OBXODKA-REALITY] Reader stopped: {ex.Message}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rxBuf);
            if (!_ipTcs.Task.IsCompleted)
            {
                _ = _ipTcs.TrySetCanceled(ct.IsCancellationRequested ? ct : CancellationToken.None);
            }

            NotifyDropOnce();
        }
    }

    private void HandleIncomingFrame(byte command, ReadOnlySpan<byte> payloadSpan, int payloadLen)
    {
        switch (command)
        {
            case ObxodkaFraming.CmdHandshakeResponse:
                {
                    var info = Encoding.UTF8.GetString(payloadSpan);
                    if (!info.StartsWith("IP:", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    var parts = info[3..].Split('|');
                    var ip4 = parts[0];
                    var ip6 = parts.Length > 1 ? parts[1] : string.Empty;
                    foreach (var p in parts)
                    {
                        if (p.StartsWith("MTU:", StringComparison.OrdinalIgnoreCase) &&
                            int.TryParse(p[4..], out var sMtu) &&
                            sMtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                        {
                            OnMtuUpdated?.Invoke(sMtu);
                        }
                    }

                    _assignedIp = ip4;
                    _ = _ipTcs.TrySetResult((ip4, ip6));
                    return;
                }
            case ObxodkaFraming.CmdData when payloadLen > 0:
                {
                    if (OnPacketReceived is not { } onPacket)
                    {
                        return;
                    }

                    var pkt = ArrayPool<byte>.Shared.Rent(payloadLen);
                    payloadSpan.CopyTo(pkt);
                    onPacket(pkt, payloadLen);
                    return;
                }
            case ObxodkaFraming.CmdPing when payloadLen >= 8:
                {
                    var echoLen = 2 + payloadLen;
                    var echo = ArrayPool<byte>.Shared.Rent(echoLen);
                    echo[0] = 0xEE;
                    echo[1] = 0x88;
                    payloadSpan.CopyTo(echo.AsSpan(2, payloadLen));
                    if (!_txQueue.TryEnqueue(echo, echoLen))
                    {
                        ArrayPool<byte>.Shared.Return(echo);
                    }

                    return;
                }
            case ObxodkaFraming.CmdPong when payloadLen >= 8:
                {
                    if (payloadLen >= 10 && payloadSpan[0] == 0xAA && payloadSpan[1] == 0xBB)
                    {
                        var probeTs = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan.Slice(2, 8));
                        if (_pendingMtuProbes.TryRemove(probeTs, out var probeTcs))
                        {
                            _ = probeTcs.TrySetResult(payloadLen - 2);
                        }

                        return;
                    }

                    var sentTs = BinaryPrimitives.ReadInt64LittleEndian(payloadSpan[..8]);
                    var rttMs = (long)((Stopwatch.GetTimestamp() - sentTs) * 1000.0 / Stopwatch.Frequency);
                    if (rttMs is >= 0 and < 30000)
                    {
                        OnPingUpdated?.Invoke(rttMs);
                    }

                    return;
                }
            case ObxodkaFraming.CmdMtuSync when payloadLen >= 2:
                {
                    var mtu = (int)BinaryPrimitives.ReadUInt16BigEndian(payloadSpan[..2]);
                    if (mtu is >= SmartMtuEngine.MinMtu and <= SmartMtuEngine.MaxMtu)
                    {
                        if (_pendingMtuSyncAcks.TryRemove(mtu, out var ackTcs))
                        {
                            _ = ackTcs.TrySetResult(true);
                        }

                        OnMtuUpdated?.Invoke(mtu);
                    }

                    return;
                }
            case ObxodkaFraming.CmdMtuBurstPayload when payloadLen >= 4 && payloadSpan[0] == 0xEE && payloadSpan[1] == 0x55:
                {
                    var targetMtu = BinaryPrimitives.ReadUInt16BigEndian(payloadSpan.Slice(2, 2));
                    lock (_receivedBurstMtus)
                    {
                        if (!_receivedBurstMtus.Contains(targetMtu))
                        {
                            _receivedBurstMtus.Add(targetMtu);
                        }
                    }

                    return;
                }
        }
    }

    private async Task WriterLoopAsync(Tls13CryptoStream stream, long initialBytes, CancellationToken ct)
    {
        var coalesceBuf = ArrayPool<byte>.Shared.Rent(131072);
        var txBytes = initialBytes;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var first = await _txQueue.DequeueAsync(ct).ConfigureAwait(false);
                if (first.length <= 0)
                {
                    break;
                }

                var coalescedLen = 0;
                var batchCount = 0;
                var hasInnerTls = false;
                var item = first;
                var hasItem = true;
                var txKey = Volatile.Read(ref _sessionKey);

                while (hasItem)
                {
                    var (packet, length) = item;
                    if (packet is not null && length > 0)
                    {
                        try
                        {
                            var cmd = TransportFrameControlMapper.ClassifyOutgoingPacket(packet, length, out var offset, out var payloadLen);
                            var payloadSpan = payloadLen > 0 ? packet.AsSpan(offset, payloadLen) : ReadOnlySpan<byte>.Empty;

                            if (!hasInnerTls && cmd == ObxodkaFraming.CmdData &&
                                InnerTlsMorpher.IsInnerTlsControlOrHandshake(payloadSpan))
                            {
                                hasInnerTls = true;
                            }

                            var isEarly = txBytes + coalescedLen < ObxodkaFraming.EarlyStageWindowBytes;
                            var padLen = InnerTlsMorpher.CalculateAntiMlPadding(payloadSpan, cmd, SmartMtuEngine.CurrentMtu, isEarly);
                            var totalLen = ObxodkaFraming.HeaderSize + payloadLen + padLen;

                            if (coalescedLen + totalLen > coalesceBuf.Length && coalescedLen > 0)
                            {
                                await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                                txBytes += coalescedLen;
                                coalescedLen = 0;
                            }

                            if (totalLen <= coalesceBuf.Length)
                            {
                                ObxodkaFraming.WriteFrame(coalesceBuf.AsSpan(coalescedLen, totalLen), cmd, payloadSpan, padLen, txKey);
                                coalescedLen += totalLen;
                            }
                            else
                            {
                                var rentedFrame = ArrayPool<byte>.Shared.Rent(totalLen);
                                try
                                {
                                    ObxodkaFraming.WriteFrame(rentedFrame.AsSpan(0, totalLen), cmd, payloadSpan, padLen, txKey);
                                    await stream.WriteAsync(rentedFrame.AsMemory(0, totalLen), ct).ConfigureAwait(false);
                                    txBytes += totalLen;
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(rentedFrame);
                                }
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(packet);
                        }
                    }

                    batchCount++;
                    hasItem = batchCount < 128 && _txQueue.TryDequeue(out item);
                }

                if (coalescedLen > 0)
                {
                    var split = hasInnerTls ? InnerTlsMorpher.ComputeIrregularRecordSplit(coalescedLen) : 0;
                    if (split > 0)
                    {
                        await stream.WriteAsync(coalesceBuf.AsMemory(0, split), ct).ConfigureAwait(false);
                        await stream.FlushAsync(ct).ConfigureAwait(false);
                        await Task.Delay(RandomNumberGenerator.GetInt32(2, 6), ct).ConfigureAwait(false);
                        await stream.WriteAsync(coalesceBuf.AsMemory(split, coalescedLen - split), ct).ConfigureAwait(false);
                    }
                    else
                    {
                        await stream.WriteAsync(coalesceBuf.AsMemory(0, coalescedLen), ct).ConfigureAwait(false);
                    }

                    txBytes += coalescedLen;
                }

                if (_txQueue.Count == 0)
                {
                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            AppLogger.LogWarning($"[OBXODKA-REALITY] Writer stopped: {ex.Message}");
            NotifyDropOnce();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coalesceBuf);
        }
    }

    private async Task PingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && Volatile.Read(ref _connected))
        {
            try
            {
                await Task.Delay(RandomNumberGenerator.GetInt32(4100, 7600), ct).ConfigureAwait(false);
                await SendPingProbeAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }

    private void NotifyDropOnce()
    {
        if (!Volatile.Read(ref _connected) || Interlocked.Exchange(ref _dropSignaled, 1) != 0)
        {
            return;
        }

        Volatile.Write(ref _connected, false);
        OnConnectionDropped?.Invoke();
    }
}
