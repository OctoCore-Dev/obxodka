namespace obxodka.Core.Transports;

//режимы фрагментации рукопожатия tls для обхода тспу и dpi
public enum DpiBypassMode
{
    //сегментация на уровне транспортного протокола l4 tcp
    PureL4Midsld = 0,

    //двухступенчатая сегментация на уровне прикладных записей tls record layer
    TlsRecordMidsld = 1,

    //трехступенчатая сегментация tls record layer (отдельный заголовок handshake + первая половина sni + остаток)
    MultiRecordMidsld = 2
}

//поток обхода блокировок: перехватывает первый пакет clienthello и разрезает домен sni пополам
internal sealed class DpiBypassStream(Stream innerStream, int splitPosition = 0, int delayMs = 0, bool bypassEnabled = true) : Stream
{
    //подлежащий сетевой поток сокета
    private readonly Stream _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));

    //принудительная позиция разделения первого пакета
    private readonly int _splitPosition = splitPosition;

    //пауза между отправкой фрагментов в миллисекундах
    private readonly int _delayMs = delayMs;

    //флаг включения механизма обхода dpi
    private readonly bool _bypassEnabled = bypassEnabled;

    //признак первого пакета рукопожатия
    private bool _firstWrite = true;

    //активный режим фрагментации
    public DpiBypassMode Mode { get; set; } = DpiBypassMode.PureL4Midsld;

    //создание потока обхода dpi с автоматическим подбором режима нарезки под профиль браузера
    public static DpiBypassStream CreateForProfile(Stream innerStream, TlsBrowserProfile profile, bool bypassEnabled = true)
    {
        RoutingHello.GetDpiDesyncParams(profile, out var modeInt, out var delayMs);
        var mode = modeInt switch
        {
            1 => DpiBypassMode.TlsRecordMidsld,
            2 => DpiBypassMode.MultiRecordMidsld,
            _ => DpiBypassMode.PureL4Midsld
        };

        return new DpiBypassStream(innerStream, delayMs: delayMs, bypassEnabled: bypassEnabled)
        {
            Mode = mode
        };
    }

    //проверка возможности чтения
    public override bool CanRead => _innerStream.CanRead;

    //проверка возможности перемотки
    public override bool CanSeek => _innerStream.CanSeek;

    //проверка возможности записи
    public override bool CanWrite => _innerStream.CanWrite;

    //длина данных подлежащего потока
    public override long Length => _innerStream.Length;

    //текущая позиция в потоке
    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    //сброс буфера в сетевой стек
    public override void Flush()
    {
        try
        {
            _innerStream.Flush();
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    //асинхронный сброс буфера в сетевой стек
    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    //синхронное чтение байтов из потока через массив
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    //синхронное чтение байтов из потока через спан
    public override int Read(Span<byte> buffer)
    {
        try
        {
            return _innerStream.Read(buffer);
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException)
        {
            return 0;
        }
    }

    //асинхронное чтение байтов из потока через массив
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    //асинхронное чтение байтов из потока через память
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException)
        {
            return 0;
        }
    }

    //перемотка позиции в потоке
    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    //установка длины потока
    public override void SetLength(long value) => _innerStream.SetLength(value);

    //синхронная запись через массив с перенаправлением в спан
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    //синхронная запись с разделением sni при первом вызове
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        try
        {
            if (_bypassEnabled && _firstWrite && buffer.Length > 32)
            {
                _firstWrite = false;
                if (TryDetermineSplitPoints(buffer, out var split1, out var delay1))
                {
                    if (Mode is DpiBypassMode.TlsRecordMidsld or DpiBypassMode.MultiRecordMidsld && IsSingleTlsHandshakeRecord(buffer))
                    {
                        WriteTlsRecordFragmentsSync(buffer, split1, delay1, multiRecord: Mode == DpiBypassMode.MultiRecordMidsld);
                        return;
                    }

                    //сегментация l4 tcp: пакет 1 до середины sni, затем пакет 2 со второй половиной
                    AppLogger.Log($"[DPI-BYPASS] Pure L4 TCP segmentation activated (Sync, Total={buffer.Length}B, Slice1={split1}B, Slice2={buffer.Length - split1}B, Jitter={delay1}ms)");
                    _innerStream.Write(buffer[..split1]);
                    _innerStream.Flush();
                    if (delay1 > 0)
                    {
                        Thread.Sleep(delay1);
                    }

                    _innerStream.Write(buffer[split1..]);
                    _innerStream.Flush();
                    return;
                }
            }

            _innerStream.Write(buffer);
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException) { }
    }

    //асинхронная запись через массив с перенаправлением в память
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    //асинхронная запись с разделением sni при первом вызове
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            if (_bypassEnabled && _firstWrite && buffer.Length > 32)
            {
                _firstWrite = false;
                if (TryDetermineSplitPoints(buffer.Span, out var split1, out var delay1))
                {
                    if (Mode is DpiBypassMode.TlsRecordMidsld or DpiBypassMode.MultiRecordMidsld && IsSingleTlsHandshakeRecord(buffer.Span))
                    {
                        await WriteTlsRecordFragmentsAsync(buffer, split1, delay1, multiRecord: Mode == DpiBypassMode.MultiRecordMidsld, cancellationToken).ConfigureAwait(false);
                        return;
                    }

                    //сегментация l4 tcp: отправка первой части clienthello, пауза и отправка остатка
                    AppLogger.Log($"[DPI-BYPASS] Pure L4 TCP segmentation activated (Total={buffer.Length}B, Slice1={split1}B, Slice2={buffer.Length - split1}B, Jitter={delay1}ms)");
                    await _innerStream.WriteAsync(buffer[..split1], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);

                    if (delay1 > 0)
                    {
                        await Task.Delay(delay1, cancellationToken).ConfigureAwait(false);
                    }

                    await _innerStream.WriteAsync(buffer[split1..], cancellationToken).ConfigureAwait(false);
                    await _innerStream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }
                else
                {
                    AppLogger.Log($"[DPI-BYPASS] First write ({buffer.Length}B) does not match TLS ClientHello pattern. Sending unsplit.");
                }
            }

            await _innerStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            //при закрытии или отключении туннеля поток уже освобожден
        }
        catch (OperationCanceledException)
        {
            //отмена операции при отключении
        }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException)
        {
            //обрыв соединения при остановке туннеля
        }
    }

    //проверка является ли буфер единой записью tls handshake
    private static bool IsSingleTlsHandshakeRecord(ReadOnlySpan<byte> span)
    {
        if (span.Length < 16 || span[0] != 0x16 || span[1] != 0x03 || span[5] != 0x01)
        {
            return false;
        }

        var recordPayloadLen = BinaryPrimitives.ReadUInt16BigEndian(span.Slice(3, 2));
        return span.Length >= 5 + recordPayloadLen;
    }

    //синхронная отправка фрагментов tls record (2 или 3 записи)
    private void WriteTlsRecordFragmentsSync(ReadOnlySpan<byte> buffer, int split1, int d1, bool multiRecord)
    {
        var verMajor = buffer[1];
        var verMinor = buffer[2];
        var payload = buffer[5..];
        var p1 = Math.Clamp(split1 - 5, 8, payload.Length - 4);

        if (multiRecord && p1 > 8)
        {
            AppLogger.Log($"[DPI-BYPASS] 3-stage Multi-Record TLS fragmentation activated (Sync, Payload={payload.Length}B -> Rec1=4B, Rec2={p1 - 4}B, Rec3={payload.Length - p1}B, Delay={d1}ms)");
            WriteSingleTlsRecordSync(verMajor, verMinor, payload[..4]);
            WriteSingleTlsRecordSync(verMajor, verMinor, payload[4..p1]);
            if (d1 > 0)
            {
                Thread.Sleep(d1);
            }

            WriteSingleTlsRecordSync(verMajor, verMinor, payload[p1..]);
            return;
        }

        AppLogger.Log($"[DPI-BYPASS] 2-stage TLS Record-Layer fragmentation activated (Sync, Payload={payload.Length}B -> Rec1={p1}B, Rec2={payload.Length - p1}B, Delay={d1}ms)");
        WriteSingleTlsRecordSync(verMajor, verMinor, payload[..p1]);
        if (d1 > 0)
        {
            Thread.Sleep(d1);
        }

        WriteSingleTlsRecordSync(verMajor, verMinor, payload[p1..]);
    }

    //синхронная отправка одиночной tls записи
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
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    //асинхронная отправка фрагментов tls record (2 или 3 записи)
    private async ValueTask WriteTlsRecordFragmentsAsync(ReadOnlyMemory<byte> buffer, int split1, int d1, bool multiRecord, CancellationToken ct)
    {
        var span = buffer.Span;
        var verMajor = span[1];
        var verMinor = span[2];
        var payload = buffer[5..];
        var p1 = Math.Clamp(split1 - 5, 8, payload.Length - 4);

        if (multiRecord && p1 > 8)
        {
            AppLogger.Log($"[DPI-BYPASS] 3-stage Multi-Record TLS fragmentation activated (Async, Payload={payload.Length}B -> Rec1=4B, Rec2={p1 - 4}B, Rec3={payload.Length - p1}B, Delay={d1}ms)");
            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[..4], ct).ConfigureAwait(false);
            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[4..p1], ct).ConfigureAwait(false);
            if (d1 > 0)
            {
                await Task.Delay(d1, ct).ConfigureAwait(false);
            }

            await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[p1..], ct).ConfigureAwait(false);
            return;
        }

        AppLogger.Log($"[DPI-BYPASS] 2-stage TLS Record-Layer fragmentation activated (Async, Payload={payload.Length}B -> Rec1={p1}B, Rec2={payload.Length - p1}B, Delay={d1}ms)");
        await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[..p1], ct).ConfigureAwait(false);
        if (d1 > 0)
        {
            await Task.Delay(d1, ct).ConfigureAwait(false);
        }

        await WriteSingleTlsRecordAsync(verMajor, verMinor, payload[p1..], ct).ConfigureAwait(false);
    }

    //асинхронная отправка одиночной tls записи
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
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
        catch (IOException ex) when (ex.InnerException is SocketException or ObjectDisposedException) { }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    //расчет точки распила sni и величины задержки
    private bool TryDetermineSplitPoints(ReadOnlySpan<byte> span, out int split1, out int delay1)
    {
        split1 = 0;
        delay1 = _delayMs > 0 ? _delayMs : RandomNumberGenerator.GetInt32(10, 22);

        if (_splitPosition > 0 && _splitPosition < span.Length)
        {
            split1 = _splitPosition;
            return true;
        }

        if (span.Length > 9 && span[0] == 0x16 && span[1] == 0x03 && span[5] == 0x01)
        {
            if (TryFindSniMidsldOffset(span, out var midsldOffset))
            {
                split1 = Math.Clamp(midsldOffset, 16, span.Length - 16);
                return true;
            }

            if (span.Length > 48)
            {
                split1 = RandomNumberGenerator.GetInt32(24, Math.Min(48, span.Length - 16));
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

    //вычисляет смещение середины доменного имени sld внутри расширения sni
    public static bool TryFindSniMidsldOffset(ReadOnlySpan<byte> data, out int splitOffset)
    {
        splitOffset = 0;
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
                        var hostname = data.Slice(start, len);
                        var dotIndex = hostname.LastIndexOf((byte)'.');
                        if (dotIndex > 0)
                        {
                            var prevDot = hostname[..dotIndex].LastIndexOf((byte)'.');
                            var sldStart = prevDot >= 0 ? prevDot + 1 : 0;
                            var sldLen = dotIndex - sldStart;
                            splitOffset = start + sldStart + (sldLen / 2);
                        }
                        else
                        {
                            splitOffset = start + (len / 2);
                        }
                        return true;
                    }
                }
            }

            offset += extLen;
        }

        return false;
    }

    //асинхронное освобождение ресурсов потока
    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    //синхронное освобождение ресурсов потока
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }

        base.Dispose(disposing);
    }
}
