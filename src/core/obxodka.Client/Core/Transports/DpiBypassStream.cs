namespace obxodka.Core.Transports;

internal sealed class DpiBypassStream(Stream innerStream, int splitPosition = 0, int delayMs = 0) : Stream
{
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    private readonly int _splitPosition = splitPosition > 0 ? splitPosition : Random.Shared.Next(1, 4);
    private readonly int _delayMs = delayMs > 0 ? delayMs : Random.Shared.Next(18, 46);
    private bool _firstWrite = true;

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

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) => _innerStream.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _innerStream.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_firstWrite && buffer.Length > 4)
        {
            _firstWrite = false;
            var isTlsHandshake = buffer[0] == 0x16 && buffer[1] == 0x03;
            var p1 = _splitPosition > 0 ? Math.Min(_splitPosition, buffer.Length - 1) : Random.Shared.Next(1, 4);
            var delay1 = _delayMs > 0 ? _delayMs : Random.Shared.Next(15, 35);

            if (isTlsHandshake && buffer.Length > 64 && _splitPosition == 0)
            {
                var p2 = Random.Shared.Next(p1 + 8, Math.Min(buffer.Length - 16, p1 + 42));
                var delay2 = Random.Shared.Next(10, 25);

                _innerStream.Write(buffer[..p1]);
                _innerStream.Flush();
                Thread.Sleep(delay1);

                _innerStream.Write(buffer[p1..p2]);
                _innerStream.Flush();
                Thread.Sleep(delay2);

                _innerStream.Write(buffer[p2..]);
                _innerStream.Flush();
            }
            else
            {
                _innerStream.Write(buffer[..p1]);
                _innerStream.Flush();
                if (delay1 > 0)
                {
                    Thread.Sleep(delay1);
                }
                _innerStream.Write(buffer[p1..]);
                _innerStream.Flush();
            }
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
        if (_firstWrite && buffer.Length > 4)
        {
            _firstWrite = false;
            var span = buffer.Span;
            var isTlsHandshake = span[0] == 0x16 && span[1] == 0x03;
            var p1 = _splitPosition > 0 ? Math.Min(_splitPosition, buffer.Length - 1) : Random.Shared.Next(1, 4);
            var delay1 = _delayMs > 0 ? _delayMs : Random.Shared.Next(15, 35);

            if (isTlsHandshake && buffer.Length > 64 && _splitPosition == 0)
            {
                var p2 = Random.Shared.Next(p1 + 8, Math.Min(buffer.Length - 16, p1 + 42));
                var delay2 = Random.Shared.Next(10, 25);

                await _innerStream.WriteAsync(buffer[..p1], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(delay1, cancellationToken).ConfigureAwait(false);

                await _innerStream.WriteAsync(buffer[p1..p2], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(delay2, cancellationToken).ConfigureAwait(false);

                await _innerStream.WriteAsync(buffer[p2..], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await _innerStream.WriteAsync(buffer[..p1], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (delay1 > 0)
                {
                    await Task.Delay(delay1, cancellationToken).ConfigureAwait(false);
                }
                await _innerStream.WriteAsync(buffer[p1..], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
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
