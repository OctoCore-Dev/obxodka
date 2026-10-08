namespace obxodka.Core.Transports;

internal sealed class DpiBypassStream(Stream innerStream, int splitPosition = 0, int delayMs = 0) : Stream
{
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    private readonly int _splitPosition = splitPosition;
    private readonly int _delayMs = delayMs;
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
        if (_firstWrite && buffer.Length > 5)
        {
            _firstWrite = false;
            if (TryDetermineSplitPoints(buffer, out var split1, out var split2, out var d1, out var d2))
            {
                if (split2 > split1 && split2 < buffer.Length)
                {
                    _innerStream.Write(buffer[..split1]);
                    _innerStream.Flush();
                    if (d1 > 0)
                    {
                        Thread.Sleep(d1);
                    }

                    _innerStream.Write(buffer[split1..split2]);
                    _innerStream.Flush();
                    if (d2 > 0)
                    {
                        Thread.Sleep(d2);
                    }

                    _innerStream.Write(buffer[split2..]);
                    _innerStream.Flush();
                    return;
                }

                _innerStream.Write(buffer[..split1]);
                _innerStream.Flush();
                if (d1 > 0)
                {
                    Thread.Sleep(d1);
                }

                _innerStream.Write(buffer[split1..]);
                _innerStream.Flush();
                return;
            }
        }

        _innerStream.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_firstWrite && buffer.Length > 5)
        {
            _firstWrite = false;
            if (TryDetermineSplitPoints(buffer.Span, out var split1, out var split2, out var d1, out var d2))
            {
                if (split2 > split1 && split2 < buffer.Length)
                {
                    await _innerStream.WriteAsync(buffer[..split1], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (d1 > 0)
                    {
                        await Task.Delay(d1, cancellationToken).ConfigureAwait(false);
                    }

                    await _innerStream.WriteAsync(buffer[split1..split2], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (d2 > 0)
                    {
                        await Task.Delay(d2, cancellationToken).ConfigureAwait(false);
                    }

                    await _innerStream.WriteAsync(buffer[split2..], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                await _innerStream.WriteAsync(buffer[..split1], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (d1 > 0)
                {
                    await Task.Delay(d1, cancellationToken).ConfigureAwait(false);
                }

                await _innerStream.WriteAsync(buffer[split1..], cancellationToken).ConfigureAwait(false);
                await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await _innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private bool TryDetermineSplitPoints(ReadOnlySpan<byte> span, out int split1, out int split2, out int delay1, out int delay2)
    {
        split1 = 0;
        split2 = 0;
        delay1 = _delayMs > 0 ? _delayMs : Random.Shared.Next(25, 55);
        delay2 = Random.Shared.Next(15, 35);

        if (_splitPosition > 0 && _splitPosition < span.Length)
        {
            split1 = _splitPosition;
            return true;
        }

        if (span.Length > 9 && span[0] == 0x16 && span[1] == 0x03 && span[5] == 0x01)
        {
            if (TryFindSniRange(span, out var sniStart, out var sniLen))
            {
                var mid = sniStart + (sniLen / 2);
                split1 = Math.Clamp(mid, 1, span.Length - 1);
                var headerCut = Random.Shared.Next(1, 4);
                if (headerCut < split1)
                {
                    split2 = split1;
                    split1 = headerCut;
                }
                return true;
            }

            split1 = Random.Shared.Next(1, 4);
            split2 = Random.Shared.Next(split1 + 10, Math.Min(span.Length - 5, split1 + 45));
            return true;
        }

        if (span.Length > 2)
        {
            split1 = 1;
            return true;
        }

        return false;
    }

    private static bool TryFindSniRange(ReadOnlySpan<byte> data, out int nameStart, out int nameLen)
    {
        nameStart = 0;
        nameLen = 0;
        if (data.Length < 44 || data[0] != 0x16 || data[1] != 0x03 || data[5] != 0x01)
        {
            return false;
        }

        var offset = 43;
        if (offset >= data.Length)
        {
            return false;
        }

        var sessionIdLen = data[offset++];
        offset += sessionIdLen;
        if (offset + 2 > data.Length)
        {
            return false;
        }

        var cipherSuitesLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2 + cipherSuitesLen;
        if (offset + 1 > data.Length)
        {
            return false;
        }

        var compMethodsLen = data[offset++];
        offset += compMethodsLen;
        if (offset + 2 > data.Length)
        {
            return false;
        }

        var extensionsLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
        offset += 2;
        var extEnd = Math.Min(data.Length, offset + extensionsLen);

        while (offset + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 2, 2));
            offset += 4;
            if (offset + extLen > extEnd)
            {
                break;
            }

            if (extType == 0x0000 && extLen >= 5)
            {
                var nameType = data[offset + 2];
                if (nameType == 0)
                {
                    var len = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset + 3, 2));
                    var start = offset + 5;
                    if (start + len <= offset + extLen && len > 1)
                    {
                        nameStart = start;
                        nameLen = len;
                        return true;
                    }
                }
            }

            offset += extLen;
        }

        return false;
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
