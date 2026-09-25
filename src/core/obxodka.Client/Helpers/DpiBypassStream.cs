using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using obxodka.Shared.Stealth;

namespace obxodka.Helpers;

public sealed partial class DpiBypassStream(
    Stream innerStream,
    int splitPosition = 2,
    int delayMs = 25,
    ChameleonState? chameleon = null,
    Socket? socket = null,
    bool enableTtlDesync = false,
    int desyncTtl = TcpDesyncHelper.DefaultDesyncTtl) : Stream
{
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    private readonly ChameleonState? _chameleon = chameleon;
    [SuppressMessage("Usage", "CA2213:DisposableFieldsShouldBeDisposed", Justification = "Socket lifecycle is managed by caller or inner NetworkStream.")]
    private readonly Socket? _socket = socket;
    private bool _firstWrite = true;

    public int SplitPosition { get; } = Math.Max(1, splitPosition);
    public int DelayMs { get; } = Math.Max(0, delayMs);
    public bool EnableTtlDesync { get; } = enableTtlDesync;
    public int DesyncTtl { get; } = Math.Clamp(desyncTtl, 1, 255);

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _innerStream.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _innerStream.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var effectiveSplit = _chameleon?.NextSplitPosition(1, 2) ?? SplitPosition;
        if (_firstWrite && buffer.Length > effectiveSplit)
        {
            _firstWrite = false;
            var effectiveDelay = _chameleon?.NextDelayMs(15, 25) ?? DelayMs;

            if (EnableTtlDesync && _socket != null)
            {
                _ = TcpDesyncHelper.TrySetSocketTtl(_socket, DesyncTtl);
            }

            _innerStream.Write(buffer[..effectiveSplit]);
            _innerStream.Flush();

            if (EnableTtlDesync && _socket != null)
            {
                _ = TcpDesyncHelper.TrySetSocketTtl(_socket, TcpDesyncHelper.DefaultRealTtl);
            }

            if (effectiveDelay > 0)
            {
                Thread.Sleep(effectiveDelay);
            }
            _innerStream.Write(buffer[effectiveSplit..]);
            _innerStream.Flush();
        }
        else
        {
            _innerStream.Write(buffer);
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var effectiveSplit = _chameleon?.NextSplitPosition(1, 2) ?? SplitPosition;
        if (_firstWrite && buffer.Length > effectiveSplit)
        {
            _firstWrite = false;
            var effectiveDelay = _chameleon?.NextDelayMs(15, 25) ?? DelayMs;

            if (EnableTtlDesync && _socket != null)
            {
                _ = TcpDesyncHelper.TrySetSocketTtl(_socket, DesyncTtl);
            }

            await _innerStream.WriteAsync(buffer[..effectiveSplit], cancellationToken).ConfigureAwait(false);
            await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (EnableTtlDesync && _socket != null)
            {
                _ = TcpDesyncHelper.TrySetSocketTtl(_socket, TcpDesyncHelper.DefaultRealTtl);
            }

            if (effectiveDelay > 0)
            {
                await Task.Delay(effectiveDelay, cancellationToken).ConfigureAwait(false);
            }
            await _innerStream.WriteAsync(buffer[effectiveSplit..], cancellationToken).ConfigureAwait(false);
            await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await _innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
