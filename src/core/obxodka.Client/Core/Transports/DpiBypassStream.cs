namespace obxodka.Core.Transports;

internal sealed class DpiBypassStream(Stream innerStream, int splitPosition = 0, int delayMs = 0, bool bypassEnabled = true) : Stream
{
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
    private readonly int _splitPosition = splitPosition;
    private readonly int _delayMs = delayMs;
    private readonly bool _bypassEnabled = bypassEnabled;
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
        if (_bypassEnabled && _firstWrite && buffer.Length > 32)
        {
            _firstWrite = false;
            if (TryDetermineSplitPoints(buffer, out var split1, out var split2, out var d1))
            {
                if (IsSingleTlsHandshakeRecord(buffer))
                {
                    WriteTlsRecordFragmentsSync(buffer, split1, split2, d1);
                    return;
                }

                _innerStream.Write(buffer[..split1]);
                _innerStream.Flush();
                if (d1 > 0)
                {
                    Thread.Sleep(d1);
                }

                if (split2 > split1 && split2 < buffer.Length)
                {
                    _innerStream.Write(buffer[split1..split2]);
                    _innerStream.Flush();
                    if (d1 > 0)
                    {
                        Thread.Sleep(1);
                    }

                    _innerStream.Write(buffer[split2..]);
                    _innerStream.Flush();
                    return;
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
        if (_bypassEnabled && _firstWrite && buffer.Length > 32)
        {
            _firstWrite = false;
            if (TryDetermineSplitPoints(buffer.Span, out var split1, out var split2, out var d1))
            {
                if (IsSingleTlsHandshakeRecord(buffer.Span))
                {
                    await WriteTlsRecordFragmentsAsync(buffer, split1, split2, d1, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (split2 > split1 && split2 < buffer.Length)
                {
                    Shared.Logging.AppLogger.Log($"[DPI-BYPASS] 3-stage TCP ClientHello SNI segmentation activated (Total={buffer.Length}B, Slice1={split1}B, Slice2={split2 - split1}B, Slice3={buffer.Length - split2}B, MicroDelay={d1}ms)");
                    await _innerStream.WriteAsync(buffer[..split1], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (d1 > 0)
                    {
                        await Task.Delay(d1, cancellationToken).ConfigureAwait(false);
                    }

                    await _innerStream.WriteAsync(buffer[split1..split2], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    if (d1 > 0)
                    {
                        await Task.Delay(1, cancellationToken).ConfigureAwait(false);
                    }

                    await _innerStream.WriteAsync(buffer[split2..], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                Shared.Logging.AppLogger.Log($"[DPI-BYPASS] 2-stage TCP ClientHello segmentation activated (Total={buffer.Length}B, Slice1={split1}B, Slice2={buffer.Length - split1}B, MicroDelay={d1}ms)");
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
            else
            {
                Shared.Logging.AppLogger.Log($"[DPI-BYPASS] First write ({buffer.Length}B) does not match TLS ClientHello pattern. Sending unsplit.");
            }
        }

        await _innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsSingleTlsHandshakeRecord(ReadOnlySpan<byte> span)
    {
        if (span.Length < 16 || span[0] != 0x16 || span[1] != 0x03 || span[5] != 0x01)
        {
            return false;
        }

        var recordPayloadLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(3, 2));
        return recordPayloadLen + 5 == span.Length && recordPayloadLen <= 16000;
    }

    private void WriteTlsRecordFragmentsSync(ReadOnlySpan<byte> buffer, int split1, int split2, int d1)
    {
        var verMajor = buffer[1];
        var verMinor = buffer[2];
        var payload = buffer[5..];
        var p1 = Math.Clamp(split1 - 5, 4, payload.Length - 4);

        WriteSingleTlsRecordSync(verMajor, verMinor, payload[..p1]);
        if (d1 > 0)
        {
            Thread.Sleep(d1);
        }

        if (split2 > split1 && split2 < buffer.Length)
        {
            var p2 = Math.Clamp(split2 - 5, p1 + 2, payload.Length - 2);
            WriteSingleTlsRecordSync(verMajor, verMinor, payload[p1..p2]);
            if (d1 > 0)
            {
                Thread.Sleep(1);
            }

            WriteSingleTlsRecordSync(verMajor, verMinor, payload[p2..]);
            return;
        }

        WriteSingleTlsRecordSync(verMajor, verMinor, payload[p1..]);
    }

    private void WriteSingleTlsRecordSync(byte verMajor, byte verMinor, ReadOnlySpan<byte> fragmentPayload)
    {
        var total = 5 + fragmentPayload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            rented[0] = 0x16;
            rented[1] = verMajor;
            rented[2] = verMinor;
            BinaryPrimitives.WriteUInt16BigEndian(rented.AsSpan(3, 2), (ushort)fragmentPayload.Length);
            fragmentPayload.CopyTo(rented.AsSpan(5));
            _innerStream.Write(rented.AsSpan(0, total));
            _innerStream.Flush();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private async ValueTask WriteTlsRecordFragmentsAsync(ReadOnlyMemory<byte> buffer, int split1, int split2, int d1, CancellationToken ct)
    {
        var span = buffer.Span;
        var verMajor = span[1];
        var verMinor = span[2];
        var payload = buffer[5..];
        var p1 = Math.Clamp(split1 - 5, 4, payload.Length - 4);

        if (split2 > split1 && split2 < buffer.Length)
        {
            var p2 = Math.Clamp(split2 - 5, p1 + 2, payload.Length - 2);
            Shared.Logging.AppLogger.Log($"[DPI-BYPASS] 3-stage TLS Record-Layer SNI fragmentation activated (Payload={payload.Length}B -> Rec1={p1}B, Rec2={p2 - p1}B, Rec3={payload.Length - p2}B, Delay={d1}ms)");
            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[..p1], ct).ConfigureAwait(false);
            if (d1 > 0)
            {
                await Task.Delay(d1, ct).ConfigureAwait(false);
            }

            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[p1..p2], ct).ConfigureAwait(false);
            if (d1 > 0)
            {
                await Task.Delay(1, ct).ConfigureAwait(false);
            }

            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[p2..], ct).ConfigureAwait(false);
            return;
        }

        Shared.Logging.AppLogger.Log($"[DPI-BYPASS] 2-stage TLS Record-Layer fragmentation activated (Payload={payload.Length}B -> Rec1={p1}B, Rec2={payload.Length - p1}B, Delay={d1}ms)");
        await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[..p1], ct).ConfigureAwait(false);
        if (d1 > 0)
        {
            await Task.Delay(d1, ct).ConfigureAwait(false);
        }

        await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[p1..], ct).ConfigureAwait(false);
    }

    private async ValueTask WriteSingleTlsRecordAsync(byte verMajor, byte verMinor, ReadOnlyMemory<byte> fragmentPayload, CancellationToken ct)
    {
        var total = 5 + fragmentPayload.Length;
        var rented = ArrayPool<byte>.Shared.Rent(total);
        try
        {
            rented[0] = 0x16;
            rented[1] = verMajor;
            rented[2] = verMinor;
            BinaryPrimitives.WriteUInt16BigEndian(rented.AsSpan(3, 2), (ushort)fragmentPayload.Length);
            fragmentPayload.Span.CopyTo(rented.AsSpan(5));
            await _innerStream.WriteAsync(rented.AsMemory(0, total), ct).ConfigureAwait(false);
            await _innerStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private bool TryDetermineSplitPoints(ReadOnlySpan<byte> span, out int split1, out int split2, out int delay1)
    {
        split1 = 0;
        split2 = 0;
        delay1 = _delayMs > 0 ? _delayMs : Random.Shared.Next(1, 3);

        if (_splitPosition > 0 && _splitPosition < span.Length)
        {
            split1 = _splitPosition;
            return true;
        }

        if (span.Length > 9 && span[0] == 0x16 && span[1] == 0x03 && span[5] == 0x01)
        {
            if (TryFindSniRange(span, out var sniStart, out var nameLen))
            {
                split1 = Math.Clamp(sniStart + 2, 16, span.Length - 16);
                if (nameLen >= 6 && split1 + 4 < span.Length)
                {
                    split2 = Math.Clamp(sniStart + (nameLen / 2) + 1, split1 + 2, span.Length - 8);
                }
                return true;
            }

            if (span.Length > 48)
            {
                split1 = Random.Shared.Next(24, Math.Min(48, span.Length - 16));
                return true;
            }
        }

        if (span.Length > 32)
        {
            split1 = 16;
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
