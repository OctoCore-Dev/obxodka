namespace obxodka.Core;

public sealed partial class TunnelGrpcStream(AsyncDuplexStreamingCall<TunnelPacket, TunnelPacket> call) : Stream
{
    private readonly AsyncDuplexStreamingCall<TunnelPacket, TunnelPacket> _call = call;
    private readonly CancellationTokenSource _cts = new();
    private ReadOnlyMemory<byte>? _readBuffer;
    private int _readOffset;
    private bool _disposed;

    public override bool CanRead => !_disposed;
    public override bool CanSeek => false;
    public override bool CanWrite => !_disposed;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            return 0;
        }

        while (true)
        {
            if (_readBuffer != null && _readOffset < _readBuffer.Value.Length)
            {
                var copyLen = Math.Min(buffer.Length, _readBuffer.Value.Length - _readOffset);
                _readBuffer.Value.Span.Slice(_readOffset, copyLen).CopyTo(buffer.Span);
                _readOffset += copyLen;
                return copyLen;
            }

            try
            {
                if (await _call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                {
                    var packet = _call.ResponseStream.Current;
                    _readBuffer = packet.Data.Memory;
                    _readOffset = 0;

                    if (_readBuffer is not { Length: > 0 })
                    {
                        continue;
                    }

                    var copyLen = Math.Min(buffer.Length, _readBuffer.Value.Length);
                    _readBuffer.Value.Span[..copyLen].CopyTo(buffer.Span);
                    _readOffset = copyLen;
                    return copyLen;
                }
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
            {
                return 0;
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
            catch (ObjectDisposedException)
            {
                return 0;
            }
            catch (Exception ex)
            {
                Shared.Logging.AppLogger.LogError($"[GRPC STREAM READ] {ex.Message}", ex);
                return 0;
            }

            return 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_disposed || cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var packet = new TunnelPacket { Data = UnsafeByteOperations.UnsafeWrap(buffer) };
            await _call.RequestStream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled || cancellationToken.IsCancellationRequested || _cts.IsCancellationRequested)
        {

        }
        catch (OperationCanceledException)
        {

        }
        catch (ObjectDisposedException)
        {

        }
        catch (Exception ex)
        {
            Shared.Logging.AppLogger.LogError($"[GRPC STREAM WRITE] {ex.Message}", ex);
        }
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch { }

        try
        {
            _ = await Task.WhenAny(_call.RequestStream.CompleteAsync(), Task.Delay(100)).ConfigureAwait(false);
        }
        catch { }

        try
        {
            _call.Dispose();
        }
        catch { }

        try
        {
            _cts.Dispose();
        }
        catch { }

        await base.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            try
            {
                _cts.Cancel();
            }
            catch { }

            try
            {
                _call.Dispose();
            }
            catch { }

            try
            {
                _cts.Dispose();
            }
            catch { }
        }

        base.Dispose(disposing);
    }
}
