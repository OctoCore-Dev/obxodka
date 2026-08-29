namespace obxodka.Core.Mesh;

public sealed partial class UdpSessionTable : IDisposable, IAsyncDisposable
{
    private sealed class SessionEntry(UdpClient socket, long lastSeenTicks)
    {
        public UdpClient Socket { get; } = socket;
        public long LastSeenTicks = lastSeenTicks;
    }

    private readonly ConcurrentDictionary<IPEndPoint, SessionEntry> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _lock = new();

    public UdpSessionTable() => _ = CleanupLoopAsync(_cts.Token);

    public UdpClient GetOrCreate(IPEndPoint clientEp, Func<UdpClient> factory)
    {
        if (_sessions.TryGetValue(clientEp, out var entry))
        {
            Volatile.Write(ref entry.LastSeenTicks, Stopwatch.GetTimestamp());
            return entry.Socket;
        }

        lock (_lock)
        {
            if (_sessions.TryGetValue(clientEp, out entry))
            {
                Volatile.Write(ref entry.LastSeenTicks, Stopwatch.GetTimestamp());
                return entry.Socket;
            }

            var socket = factory();
            var newEntry = new SessionEntry(socket, Stopwatch.GetTimestamp());
            _sessions[clientEp] = newEntry;
            return socket;
        }
    }

    public void Touch(IPEndPoint clientEp)
    {
        if (_sessions.TryGetValue(clientEp, out var existing))
        {
            Volatile.Write(ref existing.LastSeenTicks, Stopwatch.GetTimestamp());
        }
    }

    private async Task CleanupLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
                var now = Stopwatch.GetTimestamp();
                var deadline = now - (Stopwatch.Frequency * 30);

                foreach (var (key, val) in _sessions)
                {
                    if (Volatile.Read(ref val.LastSeenTicks) < deadline && _sessions.TryRemove(key, out var removed))
                    {
                        try
                        {
                            removed.Socket.Dispose();
                        }
                        catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _cts.Dispose();
        }
        catch { }

        foreach (var (_, val) in _sessions)
        {
            try
            {
                val.Socket.Dispose();
            }
            catch { }
        }
        _sessions.Clear();
        GC.SuppressFinalize(this);
    }
}

