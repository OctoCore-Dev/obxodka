namespace obxodka.Core;

public partial class TunnelGrpcStream(AsyncDuplexStreamingCall<TunnelPacket, TunnelPacket> call) : Stream
{
    private readonly AsyncDuplexStreamingCall<TunnelPacket, TunnelPacket> _call = call;
    private readonly CancellationTokenSource _cts = new();
    private ReadOnlyMemory<byte>? _readBuffer;
    private int _readOffset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() { }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_readBuffer != null && _readOffset < _readBuffer.Value.Length)
            {
                var copyLen = Math.Min(buffer.Length, _readBuffer.Value.Length - _readOffset);
                _readBuffer.Value.Span.Slice(_readOffset, copyLen).CopyTo(buffer.Span);
                _readOffset += copyLen;
                return copyLen;
            }

            if (await _call.ResponseStream.MoveNext(cancellationToken))
            {
                var packet = _call.ResponseStream.Current;
                _readBuffer = packet.Data.Memory;
                _readOffset = 0;

                if (_readBuffer.Value.Length == 0)
                {
                    continue;
                }

                var copyLen = Math.Min(buffer.Length, _readBuffer.Value.Length);
                _readBuffer.Value.Span[..copyLen].CopyTo(buffer.Span);
                _readOffset = copyLen;
                return copyLen;
            }

            return 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var packet = new TunnelPacket { Data = Google.Protobuf.ByteString.CopyFrom(buffer.Span) };
        await _call.RequestStream.WriteAsync(packet, cancellationToken);
    }

    public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    private bool _disposed;
    protected override async void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            try
            { _cts.Cancel(); }
            catch { }
            try
            {
                if (_call != null)
                {
                    _ = await Task.WhenAny(_call.RequestStream.CompleteAsync(), Task.Delay(500));
                }
            }
            catch { }
            try
            {
                _call?.Dispose();
            }
            catch { }
            try
            { _cts.Dispose(); }
            catch { }
        }
        base.Dispose(disposing);
    }
}
