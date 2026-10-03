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

    public override int Read(byte[] buffer, int offset, int count) => _innerStream.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => _innerStream.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _innerStream.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);
    public override void SetLength(long value) => _innerStream.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_firstWrite && buffer.Length > _splitPosition)
        {
            _firstWrite = false;
            _innerStream.Write(buffer[.._splitPosition]);
            _innerStream.Flush();

            if (_delayMs > 0)
            {
                Thread.Sleep(_delayMs);
            }

            _innerStream.Write(buffer[_splitPosition..]);
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
        if (_firstWrite && buffer.Length > _splitPosition)
        {
            _firstWrite = false;
            await _innerStream.WriteAsync(buffer[.._splitPosition], cancellationToken).ConfigureAwait(false);
            await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);

            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken).ConfigureAwait(false);
            }

            await _innerStream.WriteAsync(buffer[_splitPosition..], cancellationToken).ConfigureAwait(false);
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
