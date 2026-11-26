namespace obxodka.Client.Diagnostics;

//боевой локальный l4 tcp mitm-стенд тспу 2026: эмулирует тихую заморозку tcp-соединения после заданного порога байт (15-20 кб)
//и инспектирует первый пакет каждого соединения на соответствие белому списку sni
public sealed class TspuMitmSimulator : IAsyncDisposable
{
    private readonly int _byteCapPerDirection;
    private readonly CancellationTokenSource _cts = new();
    private readonly Socket _listener;
    private readonly Task _acceptLoop;
    private readonly IPEndPoint _upstreamEndpoint;
    private readonly HashSet<string>? _whitelistSnis;

    private int _acceptedConnections;
    private int _frozenConnections;
    private int _sniBlocks;

    public TspuMitmSimulator(
        IPEndPoint upstreamEndpoint,
        int byteCapPerDirection = 15800,
        IEnumerable<string>? whitelistSnis = null)
    {
        _upstreamEndpoint = upstreamEndpoint;
        _byteCapPerDirection = byteCapPerDirection;
        _whitelistSnis = whitelistSnis is not null
            ? new HashSet<string>(whitelistSnis, StringComparer.OrdinalIgnoreCase)
            : null;

        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(32);
        ListenEndpoint = (IPEndPoint)_listener.LocalEndPoint!;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public IPEndPoint ListenEndpoint { get; }
    public int AcceptedConnections => Volatile.Read(ref _acceptedConnections);
    public int FrozenConnections => Volatile.Read(ref _frozenConnections);
    public int SniBlocks => Volatile.Read(ref _sniBlocks);

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Dispose();
        try
        {
            await _acceptLoop.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                _ = Interlocked.Increment(ref _acceptedConnections);
                _ = Task.Run(() => HandleProxiedConnectionAsync(client, ct), CancellationToken.None);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                break;
            }
        }
    }

    private async Task HandleProxiedConnectionAsync(Socket client, CancellationToken ct)
    {
        using (client)
        using (var upstream = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true })
        {
            client.NoDelay = true;
            await upstream.ConnectAsync(_upstreamEndpoint, ct).ConfigureAwait(false);

            using var connCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var frozenFlag = 0;

            var upTask = PumpWithByteCapAsync(client, upstream, checkFirstClientHello: true, () =>
            {
                if (Interlocked.Exchange(ref frozenFlag, 1) == 0)
                {
                    _ = Interlocked.Increment(ref _frozenConnections);
                }
            }, connCts.Token);

            var downTask = PumpWithByteCapAsync(upstream, client, checkFirstClientHello: false, () =>
            {
                if (Interlocked.Exchange(ref frozenFlag, 1) == 0)
                {
                    _ = Interlocked.Increment(ref _frozenConnections);
                }
            }, connCts.Token);

            _ = await Task.WhenAny(upTask, downTask).ConfigureAwait(false);
            await connCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(upTask, downTask).WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task PumpWithByteCapAsync(
        Socket src,
        Socket dst,
        bool checkFirstClientHello,
        Action onFrozenOnce,
        CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(16384);
        long forwarded = 0;
        var firstPacket = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await src.ReceiveAsync(buf, SocketFlags.None, ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    try
                    {
                        dst.Shutdown(SocketShutdown.Send);
                    }
                    catch
                    {
                    }

                    break;
                }

                if (firstPacket && checkFirstClientHello)
                {
                    firstPacket = false;
                    if (_whitelistSnis is not null &&
                        VirtualTspuEngine.TryExtractTlsSni(buf.AsSpan(0, n), out var sni) &&
                        !_whitelistSnis.Contains(sni))
                    {
                        _ = Interlocked.Increment(ref _sniBlocks);
                        return;
                    }
                }

                if (_byteCapPerDirection > 0 && forwarded + n > _byteCapPerDirection)
                {
                    var allowed = (int)Math.Max(0, _byteCapPerDirection - forwarded);
                    if (allowed > 0)
                    {
                        _ = await dst.SendAsync(buf.AsMemory(0, allowed), SocketFlags.None, ct).ConfigureAwait(false);
                        forwarded += allowed;
                    }

                    onFrozenOnce();
                    //точная модель тспу: тихий дроп всех последующих байт без отправки tcp rst (соединение висит мертвым)
                    while (!ct.IsCancellationRequested)
                    {
                        var discarded = await src.ReceiveAsync(buf, SocketFlags.None, ct).ConfigureAwait(false);
                        if (discarded <= 0)
                        {
                            break;
                        }
                    }

                    return;
                }

                _ = await dst.SendAsync(buf.AsMemory(0, n), SocketFlags.None, ct).ConfigureAwait(false);
                forwarded += n;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
