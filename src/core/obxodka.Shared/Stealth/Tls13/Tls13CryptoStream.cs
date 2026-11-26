using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace obxodka.Shared.Stealth.Tls13;

//поток rfc 8446 tls 1.3 (tls_aes_128_gcm_sha256) с эталонным отпечатком chromium clienthello и поддержкой reality session_id
public sealed class Tls13CryptoStream : Stream
{
    private const int MaxPlaintextRecord = 16384;
    private const int MaxCiphertextRecord = MaxPlaintextRecord + 256;
    private const int GcmTagSize = 16;

    private static readonly byte[] s_emptySha256 = SHA256.HashData([]);

    private readonly Stream _inner;
    private readonly bool _leaveInnerOpen;
    private readonly AesGcm _readAes;
    private readonly byte[] _readIv;
    private readonly AesGcm _writeAes;
    private readonly byte[] _writeIv;

    private byte[] _decryptedBuffer = ArrayPool<byte>.Shared.Rent(MaxPlaintextRecord + 64);
    private int _decryptedLen;
    private int _decryptedOffset;
    private int _disposed;
    private ulong _readSeq;
    private ulong _writeSeq;

    private Tls13CryptoStream(
        Stream inner,
        bool leaveInnerOpen,
        ReadOnlySpan<byte> readKey,
        ReadOnlySpan<byte> readIv,
        ReadOnlySpan<byte> writeKey,
        ReadOnlySpan<byte> writeIv,
        string negotiatedProtocol)
    {
        _inner = inner;
        _leaveInnerOpen = leaveInnerOpen;
        _readAes = new AesGcm(readKey, GcmTagSize);
        _readIv = readIv.ToArray();
        _writeAes = new AesGcm(writeKey, GcmTagSize);
        _writeIv = writeIv.ToArray();
        NegotiatedApplicationProtocol = negotiatedProtocol;
    }

    public string NegotiatedApplicationProtocol { get; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    //клиентское рукопожатие rfc 8446 tls 1.3 с генерацией chromium clienthello и криптографическим тегом reality в session_id
    public static async Task<Tls13CryptoStream> ConnectClientAsync(
        Stream innerStream,
        string sniHost,
        ReadOnlyMemory<byte> serverRealityPublicKey,
        string? expectedServerSpkiSha256Base64 = null,
        bool leaveInnerOpen = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(innerStream);

        var clientEphPriv = new byte[32];
        var clientEphPub = new byte[32];
        var clientRandom = new byte[32];
        var sessionId = new byte[32];
        RandomNumberGenerator.Fill(clientRandom);
        Curve25519.GenerateKeyPair(clientEphPriv, clientEphPub);

        if (serverRealityPublicKey.Length == 32)
        {
            ObxRealityAuth.BuildClientSessionId(
                clientEphPriv,
                serverRealityPublicKey.Span,
                clientRandom,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                sessionId);
        }
        else
        {
            RandomNumberGenerator.Fill(sessionId);
        }

        try
        {
            var clientHelloRecord = BuildChromiumTls13ClientHelloRecord(
                sniHost,
                clientRandom,
                sessionId,
                clientEphPub,
                out var clientHelloHandshakeMsg);

            using var transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            transcript.AppendData(clientHelloHandshakeMsg);

            await innerStream.WriteAsync(clientHelloRecord, ct).ConfigureAwait(false);
            await innerStream.FlushAsync(ct).ConfigureAwait(false);

            //чтение serverhello (пропуская возможный change_cipher_spec)
            var serverHelloMsg = await ReadServerHelloHandshakeAsync(innerStream, ct).ConfigureAwait(false);
            if (!TryParseServerHello(serverHelloMsg, sessionId, out var serverEphPub, out var cipherSuite) ||
                cipherSuite != 0x1301)
            {
                throw new AuthenticationException("Server did not negotiate RFC 8446 TLS_AES_128_GCM_SHA256 with X25519.");
            }

            transcript.AppendData(serverHelloMsg);
            var chShHash = transcript.GetCurrentHash();

            var ecdheShared = new byte[32];
            if (!Curve25519.ComputeSharedSecret(clientEphPriv, serverEphPub, ecdheShared))
            {
                throw new AuthenticationException("Invalid server X25519 ephemeral key share.");
            }

            ComputeHandshakeSecrets(
                ecdheShared,
                chShHash,
                out var handshakeSecret,
                out var clientHsSecret,
                out var serverHsSecret);
            CryptographicOperations.ZeroMemory(ecdheShared);

            DeriveTrafficKeys(serverHsSecret, out var sHsKey, out var sHsIv);
            DeriveTrafficKeys(clientHsSecret, out var cHsKey, out var cHsIv);

            using var serverHsAes = new AesGcm(sHsKey, GcmTagSize);
            using var clientHsAes = new AesGcm(cHsKey, GcmTagSize);
            ulong serverHsSeq = 0;

            //чтение зашифрованных сообщений encryptedextensions, certificate, certificateverify, finished
            var negotiatedAlpn = string.Empty;
            byte[]? leafCertDer = null;
            var certVerified = false;
            var serverFinishedVerified = false;
            using var hsBuffer = new MemoryStream(4096);

            while (!serverFinishedVerified)
            {
                var (innerType, plainChunk) = await ReadEncryptedRecordAsync(innerStream, serverHsAes, sHsIv, serverHsSeq++, ct)
                    .ConfigureAwait(false);
                if (innerType == 0x15)
                {
                    var alertDesc = plainChunk.Length >= 2 ? plainChunk[1] : 0;
                    throw new AuthenticationException($"TLS 1.3 server aborted handshake with alert {alertDesc}.");
                }

                if (innerType != 0x16)
                {
                    throw new AuthenticationException($"Unexpected TLS 1.3 record content type 0x{innerType:X2} during handshake.");
                }

                await hsBuffer.WriteAsync(plainChunk, ct).ConfigureAwait(false);
                var allBytes = hsBuffer.ToArray();
                var consumed = 0;

                while (consumed + 4 <= allBytes.Length)
                {
                    var msgType = allBytes[consumed];
                    var msgLen = (allBytes[consumed + 1] << 16) | (allBytes[consumed + 2] << 8) | allBytes[consumed + 3];
                    if (consumed + 4 + msgLen > allBytes.Length)
                    {
                        break;
                    }

                    var fullMsg = allBytes.AsSpan(consumed, 4 + msgLen);
                    var body = fullMsg[4..];

                    switch (msgType)
                    {
                        case 0x08: // EncryptedExtensions
                            negotiatedAlpn = ParseEncryptedExtensionsAlpn(body);
                            transcript.AppendData(fullMsg);
                            break;

                        case 0x0b: // Certificate
                            leafCertDer = ParseLeafCertificateDer(body);
                            transcript.AppendData(fullMsg);
                            break;

                        case 0x0f: // CertificateVerify
                            var hashBeforeCertVerify = transcript.GetCurrentHash();
                            VerifyServerCertificateSignature(leafCertDer, body, hashBeforeCertVerify, expectedServerSpkiSha256Base64);
                            certVerified = true;
                            transcript.AppendData(fullMsg);
                            break;

                        case 0x14: // Server Finished
                            if (!certVerified)
                            {
                                throw new AuthenticationException("TLS 1.3 Server Finished arrived before CertificateVerify.");
                            }

                            var hashBeforeFinished = transcript.GetCurrentHash();
                            VerifyFinishedMac(serverHsSecret, hashBeforeFinished, body);
                            transcript.AppendData(fullMsg);
                            serverFinishedVerified = true;
                            break;

                        default:
                            transcript.AppendData(fullMsg);
                            break;
                    }

                    consumed += 4 + msgLen;
                    if (serverFinishedVerified)
                    {
                        break;
                    }
                }

                if (consumed > 0)
                {
                    var leftover = allBytes.Length - consumed;
                    hsBuffer.SetLength(0);
                    if (leftover > 0)
                    {
                        await hsBuffer.WriteAsync(allBytes.AsMemory(consumed, leftover), ct).ConfigureAwait(false);
                    }
                }
            }

            var hashAfterServerFinished = transcript.GetCurrentHash();
            ComputeApplicationSecrets(
                handshakeSecret,
                hashAfterServerFinished,
                out var clientAppSecret,
                out var serverAppSecret);

            //отправка change_cipher_spec + зашифрованного client finished
            var clientFinishedMsg = BuildFinishedMessage(clientHsSecret, hashAfterServerFinished);
            var ccsAndFinished = BuildCcsAndEncryptedHandshakeFlight(clientHsAes, cHsIv, 0, clientFinishedMsg);
            await innerStream.WriteAsync(ccsAndFinished, ct).ConfigureAwait(false);
            await innerStream.FlushAsync(ct).ConfigureAwait(false);

            DeriveTrafficKeys(serverAppSecret, out var sAppKey, out var sAppIv);
            DeriveTrafficKeys(clientAppSecret, out var cAppKey, out var cAppIv);

            return new Tls13CryptoStream(innerStream, leaveInnerOpen, sAppKey, sAppIv, cAppKey, cAppIv, negotiatedAlpn);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clientEphPriv);
        }
    }

    //серверное рукопожатие rfc 8446 tls 1.3 для авторизованного клиента reality (с выравниванием размера сертификата под донора)
    public static async Task<Tls13CryptoStream> AcceptServerAsync(
        Stream innerStream,
        ReadOnlyMemory<byte> verifiedClientHelloHandshakeMsg,
        ReadOnlyMemory<byte> clientSessionId,
        ReadOnlyMemory<byte> clientX25519PublicKey,
        X509Certificate2 serverStealthCert,
        int targetCertPaddingBytes = 1800,
        bool leaveInnerOpen = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(innerStream);
        ArgumentNullException.ThrowIfNull(serverStealthCert);

        var serverEphPriv = new byte[32];
        var serverEphPub = new byte[32];
        Curve25519.GenerateKeyPair(serverEphPriv, serverEphPub);

        var ecdheShared = new byte[32];
        try
        {
            if (!Curve25519.ComputeSharedSecret(serverEphPriv, clientX25519PublicKey.Span, ecdheShared))
            {
                throw new AuthenticationException("Invalid client X25519 ephemeral key share.");
            }

            var serverHelloMsg = BuildServerHelloHandshakeMsg(clientSessionId.Span, serverEphPub);

            using var transcript = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            transcript.AppendData(verifiedClientHelloHandshakeMsg.Span);
            transcript.AppendData(serverHelloMsg);

            var chShHash = transcript.GetCurrentHash();
            ComputeHandshakeSecrets(
                ecdheShared,
                chShHash,
                out var handshakeSecret,
                out var clientHsSecret,
                out var serverHsSecret);

            DeriveTrafficKeys(serverHsSecret, out var sHsKey, out var sHsIv);
            DeriveTrafficKeys(clientHsSecret, out var cHsKey, out var cHsIv);

            using var serverHsAes = new AesGcm(sHsKey, GcmTagSize);
            using var clientHsAes = new AesGcm(cHsKey, GcmTagSize);

            var encryptedExtensionsMsg = BuildEncryptedExtensionsMsg("h2");
            transcript.AppendData(encryptedExtensionsMsg);

            var certificateMsg = BuildCertificateMsg(serverStealthCert.RawData, targetCertPaddingBytes);
            transcript.AppendData(certificateMsg);

            var hashBeforeCertVerify = transcript.GetCurrentHash();
            var certVerifyMsg = BuildServerCertificateVerifyMsg(serverStealthCert, hashBeforeCertVerify);
            transcript.AppendData(certVerifyMsg);

            var hashBeforeServerFinished = transcript.GetCurrentHash();
            var serverFinishedMsg = BuildFinishedMessage(serverHsSecret, hashBeforeServerFinished);
            transcript.AppendData(serverFinishedMsg);

            var hashAfterServerFinished = transcript.GetCurrentHash();
            ComputeApplicationSecrets(
                handshakeSecret,
                hashAfterServerFinished,
                out var clientAppSecret,
                out var serverAppSecret);

            //единый залп serverhello + ccs + зашифрованный блок рукопожатия (идентично nginx/cloudflare tls 1.3)
            var encryptedServerFlight = new byte[
                encryptedExtensionsMsg.Length + certificateMsg.Length + certVerifyMsg.Length + serverFinishedMsg.Length];
            var off = 0;
            encryptedExtensionsMsg.CopyTo(encryptedServerFlight.AsSpan(off));
            off += encryptedExtensionsMsg.Length;
            certificateMsg.CopyTo(encryptedServerFlight.AsSpan(off));
            off += certificateMsg.Length;
            certVerifyMsg.CopyTo(encryptedServerFlight.AsSpan(off));
            off += certVerifyMsg.Length;
            serverFinishedMsg.CopyTo(encryptedServerFlight.AsSpan(off));

            var serverWireFlight = BuildServerWireFlight(serverHelloMsg, serverHsAes, sHsIv, encryptedServerFlight);
            await innerStream.WriteAsync(serverWireFlight, ct).ConfigureAwait(false);
            await innerStream.FlushAsync(ct).ConfigureAwait(false);

            //чтение и проверка client finished
            var clientFinishedRaw = await ReadClientFinishedHandshakeAsync(innerStream, clientHsAes, cHsIv, ct).ConfigureAwait(false);
            if (clientFinishedRaw.Length < 4 || clientFinishedRaw[0] != 0x14)
            {
                throw new AuthenticationException("Expected TLS 1.3 Client Finished message.");
            }

            VerifyFinishedMac(clientHsSecret, hashAfterServerFinished, clientFinishedRaw.AsSpan(4));

            DeriveTrafficKeys(clientAppSecret, out var cAppKey, out var cAppIv);
            DeriveTrafficKeys(serverAppSecret, out var sAppKey, out var sAppIv);

            return new Tls13CryptoStream(innerStream, leaveInnerOpen, cAppKey, cAppIv, sAppKey, sAppIv, "h2");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(serverEphPriv);
            CryptographicOperations.ZeroMemory(ecdheShared);
        }
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (_decryptedOffset >= _decryptedLen)
        {
            var (innerType, plain) = await ReadEncryptedRecordAsync(_inner, _readAes, _readIv, _readSeq++, cancellationToken, returnZeroOnEof: true)
                .ConfigureAwait(false);
            if (innerType == 0 && plain.Length == 0)
            {
                return 0;
            }

            //alert close_notify (0x00)
            if (innerType == 0x15)
            {
                return plain.Length >= 2 && plain[1] == 0x00
                    ? 0
                    : throw new IOException($"TLS 1.3 peer sent alert 0x{(plain.Length >= 2 ? plain[1] : 0):X2}.");
            }

            //пропуск пост-рукопожатных сообщений new_session_ticket (0x04) от реальных серверов cdn
            if (innerType == 0x16)
            {
                continue;
            }

            if (innerType != 0x17)
            {
                continue;
            }

            if (plain.Length == 0)
            {
                continue;
            }

            if (plain.Length > _decryptedBuffer.Length)
            {
                ArrayPool<byte>.Shared.Return(_decryptedBuffer);
                _decryptedBuffer = ArrayPool<byte>.Shared.Rent(plain.Length);
            }

            plain.CopyTo(_decryptedBuffer.AsSpan());
            _decryptedOffset = 0;
            _decryptedLen = plain.Length;
        }

        var toCopy = Math.Min(buffer.Length, _decryptedLen - _decryptedOffset);
        _decryptedBuffer.AsSpan(_decryptedOffset, toCopy).CopyTo(buffer.Span);
        _decryptedOffset += toCopy;
        return toCopy;
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var remaining = buffer;

        while (!remaining.IsEmpty)
        {
            var chunkLen = Math.Min(remaining.Length, MaxPlaintextRecord);
            var chunk = remaining[..chunkLen];
            remaining = remaining[chunkLen..];

            var cipherLen = chunkLen + 1 + GcmTagSize;
            var wireTotal = 5 + cipherLen;
            var rented = ArrayPool<byte>.Shared.Rent(wireTotal);
            try
            {
                WriteEncryptedRecordToSpan(rented.AsSpan(0, wireTotal), _writeAes, _writeIv, _writeSeq++, 0x17, chunk.Span);
                await _inner.WriteAsync(rented.AsMemory(0, wireTotal), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && disposing)
        {
            var buf = Interlocked.Exchange(ref _decryptedBuffer, []);
            if (buf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            _readAes.Dispose();
            _writeAes.Dispose();
            CryptographicOperations.ZeroMemory(_readIv);
            CryptographicOperations.ZeroMemory(_writeIv);
            if (!_leaveInnerOpen)
            {
                _inner.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            var buf = Interlocked.Exchange(ref _decryptedBuffer, []);
            if (buf.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            _readAes.Dispose();
            _writeAes.Dispose();
            CryptographicOperations.ZeroMemory(_readIv);
            CryptographicOperations.ZeroMemory(_writeIv);
            if (!_leaveInnerOpen)
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
            }
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }

    //сборка эталонного chromium tls 1.3 clienthello с реальными grease, alpn h2, x25519 key_share и reality session_id
    private static byte[] BuildChromiumTls13ClientHelloRecord(
        string sniHost,
        ReadOnlySpan<byte> clientRandom32,
        ReadOnlySpan<byte> sessionId32,
        ReadOnlySpan<byte> clientX25519Pub32,
        out byte[] handshakeMsg)
    {
        var buf = new byte[1024];
        var offset = 0;

        buf[offset++] = 0x01; // ClientHello
        var hsLenPos = offset;
        offset += 3;

        buf[offset++] = 0x03;
        buf[offset++] = 0x03;

        clientRandom32.CopyTo(buf.AsSpan(offset, 32));
        offset += 32;

        buf[offset++] = 32;
        sessionId32.CopyTo(buf.AsSpan(offset, 32));
        offset += 32;

        //уникальные grease значения для соблюдения rfc 8446 и исключения tls alert 50 decode_error
        var greaseCipher = TlsGreaseHelper.GetRandomGrease();
        var greaseExt1 = TlsGreaseHelper.GetRandomGrease();
        var greaseExt2 = TlsGreaseHelper.GetRandomGrease();
        while (greaseExt2 == greaseExt1)
        {
            greaseExt2 = TlsGreaseHelper.GetRandomGrease();
        }

        var greaseGroup = TlsGreaseHelper.GetRandomGrease();
        var greaseVersion = TlsGreaseHelper.GetRandomGrease();

        //ciphersuites chromium (grease + tls 1.3 + ecdhe fallback для ja4 идентичности с chrome)
        ReadOnlySpan<ushort> ciphers =
        [
            greaseCipher,
            0x1301,
            0x1302,
            0x1303,
            0xc02b,
            0xc02f,
            0xc02c,
            0xc030,
            0xcca9,
            0xcca8
        ];

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), (ushort)(ciphers.Length * 2));
        offset += 2;
        foreach (var c in ciphers)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), c);
            offset += 2;
        }

        buf[offset++] = 0x01;
        buf[offset++] = 0x00;

        var extLenPos = offset;
        offset += 2;

        WriteExt(buf, ref offset, greaseExt1, []);

        if (!string.IsNullOrWhiteSpace(sniHost) && !IPAddress.TryParse(sniHost, out _))
        {
            var sniBytes = Encoding.ASCII.GetBytes(sniHost.Trim());
            var sniExt = new byte[5 + sniBytes.Length];
            BinaryPrimitives.WriteUInt16BigEndian(sniExt.AsSpan(0, 2), (ushort)(3 + sniBytes.Length));
            sniExt[2] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(sniExt.AsSpan(3, 2), (ushort)sniBytes.Length);
            sniBytes.CopyTo(sniExt.AsSpan(5));
            WriteExt(buf, ref offset, 0x0000, sniExt);
        }

        WriteExt(buf, ref offset, 0x0017, []);
        WriteExt(buf, ref offset, 0xff01, [0x00]);

        //supported_groups (единый greaseGroup с key_share по rfc 8446 + x25519 + secp256r1 + secp384r1)
        Span<byte> groupsBody = stackalloc byte[10];
        BinaryPrimitives.WriteUInt16BigEndian(groupsBody[..2], 8);
        BinaryPrimitives.WriteUInt16BigEndian(groupsBody.Slice(2, 2), greaseGroup);
        BinaryPrimitives.WriteUInt16BigEndian(groupsBody.Slice(4, 2), 0x001d);
        BinaryPrimitives.WriteUInt16BigEndian(groupsBody.Slice(6, 2), 0x0017);
        BinaryPrimitives.WriteUInt16BigEndian(groupsBody.Slice(8, 2), 0x0018);
        WriteExt(buf, ref offset, 0x000a, groupsBody);

        WriteExt(buf, ref offset, 0x000b, [0x01, 0x00]);
        WriteExt(buf, ref offset, 0x0023, []);
        WriteExt(buf, ref offset, 0x0010, [0x00, 0x0c, 0x02, 0x68, 0x32, 0x08, 0x68, 0x74, 0x74, 0x70, 0x2f, 0x31, 0x2e, 0x31]);
        WriteExt(buf, ref offset, 0x0005, [0x01, 0x00, 0x00, 0x00, 0x00]);

        //signature_algorithms
        ReadOnlySpan<byte> sigAlgs =
        [
            0x00, 0x10,
            0x04, 0x03,
            0x08, 0x04,
            0x04, 0x01,
            0x05, 0x03,
            0x08, 0x05,
            0x05, 0x01,
            0x08, 0x06,
            0x06, 0x01
        ];
        WriteExt(buf, ref offset, 0x000d, sigAlgs);

        WriteExt(buf, ref offset, 0x0012, []); // signed_certificate_timestamp

        //key_share: тот же greaseGroup (1b) + x25519 (32b)
        Span<byte> ksBody = stackalloc byte[43];
        BinaryPrimitives.WriteUInt16BigEndian(ksBody[..2], 41);
        BinaryPrimitives.WriteUInt16BigEndian(ksBody.Slice(2, 2), greaseGroup);
        BinaryPrimitives.WriteUInt16BigEndian(ksBody.Slice(4, 2), 1);
        ksBody[6] = 0x00;
        BinaryPrimitives.WriteUInt16BigEndian(ksBody.Slice(7, 2), 0x001d);
        BinaryPrimitives.WriteUInt16BigEndian(ksBody.Slice(9, 2), 32);
        clientX25519Pub32.CopyTo(ksBody.Slice(11, 32));
        WriteExt(buf, ref offset, 0x0033, ksBody);

        WriteExt(buf, ref offset, 0x002d, [0x01, 0x01]); // psk_key_exchange_modes

        //supported_versions (grease + tls 1.3 + tls 1.2)
        Span<byte> verBody = stackalloc byte[7];
        verBody[0] = 6;
        BinaryPrimitives.WriteUInt16BigEndian(verBody.Slice(1, 2), greaseVersion);
        BinaryPrimitives.WriteUInt16BigEndian(verBody.Slice(3, 2), 0x0304);
        BinaryPrimitives.WriteUInt16BigEndian(verBody.Slice(5, 2), 0x0303);
        WriteExt(buf, ref offset, 0x002b, verBody);

        WriteExt(buf, ref offset, 0x4469, [0x00, 0x03, 0x02, 0x68, 0x32]); // ALPS h2
        WriteExt(buf, ref offset, greaseExt2, [0x00]);

        //выравнивание размера clienthello до стандартных 512 байт chromium (extension 0x0015 padding)
        var targetHandshakeTotal = 512;
        if (offset + 4 < targetHandshakeTotal)
        {
            var padLen = targetHandshakeTotal - (offset + 4);
            WriteExt(buf, ref offset, 0x0015, new byte[padLen]);
        }

        var extTotal = offset - extLenPos - 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(extLenPos, 2), (ushort)extTotal);

        var hsBodyLen = offset - 4;
        buf[hsLenPos] = (byte)(hsBodyLen >> 16);
        buf[hsLenPos + 1] = (byte)(hsBodyLen >> 8);
        buf[hsLenPos + 2] = (byte)hsBodyLen;

        handshakeMsg = buf.AsSpan(0, offset).ToArray();

        var record = new byte[5 + offset];
        record[0] = 0x16;
        record[1] = 0x03;
        record[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(record.AsSpan(3, 2), (ushort)offset);
        handshakeMsg.CopyTo(record.AsSpan(5));
        return record;
    }

    private static void WriteExt(byte[] buf, ref int offset, ushort extType, ReadOnlySpan<byte> body)
    {
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), extType);
        offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(offset, 2), (ushort)body.Length);
        offset += 2;
        if (!body.IsEmpty)
        {
            body.CopyTo(buf.AsSpan(offset));
            offset += body.Length;
        }
    }

    private static byte[] BuildServerHelloHandshakeMsg(ReadOnlySpan<byte> echoedSessionId32, ReadOnlySpan<byte> serverX25519Pub32)
    {
        var buf = new byte[122];
        var off = 0;

        buf[off++] = 0x02; // ServerHello
        var lenPos = off;
        off += 3;

        buf[off++] = 0x03;
        buf[off++] = 0x03;

        RandomNumberGenerator.Fill(buf.AsSpan(off, 32));
        off += 32;

        buf[off++] = 32;
        echoedSessionId32[..32].CopyTo(buf.AsSpan(off, 32));
        off += 32;

        //cipher_suite TLS_AES_128_GCM_SHA256 (0x1301) + compression null (0x00)
        buf[off++] = 0x13;
        buf[off++] = 0x01;
        buf[off++] = 0x00;

        var extLenPos = off;
        off += 2;

        //supported_versions = 0x0304 (TLS 1.3)
        WriteExt(buf, ref off, 0x002b, [0x03, 0x04]);

        //key_share = x25519 (0x001d) + 32b pub
        Span<byte> ks = stackalloc byte[36];
        BinaryPrimitives.WriteUInt16BigEndian(ks[..2], 0x001d);
        BinaryPrimitives.WriteUInt16BigEndian(ks.Slice(2, 2), 32);
        serverX25519Pub32[..32].CopyTo(ks.Slice(4, 32));
        WriteExt(buf, ref off, 0x0033, ks);

        BinaryPrimitives.WriteUInt16BigEndian(buf.AsSpan(extLenPos, 2), (ushort)(off - extLenPos - 2));
        var hsLen = off - 4;
        buf[lenPos] = (byte)(hsLen >> 16);
        buf[lenPos + 1] = (byte)(hsLen >> 8);
        buf[lenPos + 2] = (byte)hsLen;

        return buf[..off];
    }

    private static byte[] BuildEncryptedExtensionsMsg(string alpn)
    {
        var protoBytes = Encoding.ASCII.GetBytes(alpn);
        var alpnBody = new byte[3 + protoBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(alpnBody.AsSpan(0, 2), (ushort)(1 + protoBytes.Length));
        alpnBody[2] = (byte)protoBytes.Length;
        protoBytes.CopyTo(alpnBody.AsSpan(3));

        var extTotal = 4 + alpnBody.Length;
        var msg = new byte[4 + 2 + extTotal];
        msg[0] = 0x08; // EncryptedExtensions
        var bodyLen = 2 + extTotal;
        msg[1] = (byte)(bodyLen >> 16);
        msg[2] = (byte)(bodyLen >> 8);
        msg[3] = (byte)bodyLen;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), (ushort)extTotal);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6, 2), 0x0010);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(8, 2), (ushort)alpnBody.Length);
        alpnBody.CopyTo(msg.AsSpan(10));
        return msg;
    }

    private static byte[] BuildCertificateMsg(ReadOnlySpan<byte> certDer, int targetPaddingBytes)
    {
        //выравниваем длину зашифрованного блока сертификата под реальную цепочку уц донора
        var padExtBytes = certDer.Length < targetPaddingBytes
            ? RandomNumberGenerator.GetInt32(targetPaddingBytes - certDer.Length, targetPaddingBytes - certDer.Length + 240)
            : 0;
        var certExtFieldLen = padExtBytes > 0 ? 4 + padExtBytes : 0;

        var listLen = 3 + certDer.Length + 2 + certExtFieldLen;
        var bodyLen = 1 + 3 + listLen;
        var msg = new byte[4 + bodyLen];
        var off = 0;

        msg[off++] = 0x0b; // Certificate
        msg[off++] = (byte)(bodyLen >> 16);
        msg[off++] = (byte)(bodyLen >> 8);
        msg[off++] = (byte)bodyLen;

        msg[off++] = 0x00; // request_context = empty
        msg[off++] = (byte)(listLen >> 16);
        msg[off++] = (byte)(listLen >> 8);
        msg[off++] = (byte)listLen;

        msg[off++] = (byte)(certDer.Length >> 16);
        msg[off++] = (byte)(certDer.Length >> 8);
        msg[off++] = (byte)certDer.Length;
        certDer.CopyTo(msg.AsSpan(off));
        off += certDer.Length;

        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(off, 2), (ushort)certExtFieldLen);
        off += 2;
        if (padExtBytes > 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(off, 2), TlsGreaseHelper.GetRandomGrease());
            off += 2;
            BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(off, 2), (ushort)padExtBytes);
            off += 2;
            RandomNumberGenerator.Fill(msg.AsSpan(off, padExtBytes));
        }

        return msg;
    }

    private static byte[] BuildServerCertificateVerifyMsg(X509Certificate2 cert, ReadOnlySpan<byte> transcriptHash)
    {
        var toSign = BuildCertVerifyContent(transcriptHash);
        ushort sigScheme;
        byte[] signature;

        using var ecdsa = cert.GetECDsaPrivateKey();
        if (ecdsa is not null)
        {
            sigScheme = 0x0403; // ecdsa_secp256r1_sha256
            signature = ecdsa.SignData(toSign, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        else
        {
            using var rsa = cert.GetRSAPrivateKey()
                            ?? throw new CryptographicException("Server TLS 1.3 certificate has no ECDSA or RSA private key.");
            sigScheme = 0x0804; // rsa_pss_rsae_sha256
            signature = rsa.SignData(toSign, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }

        var bodyLen = 4 + signature.Length;
        var msg = new byte[4 + bodyLen];
        msg[0] = 0x0f; // CertificateVerify
        msg[1] = (byte)(bodyLen >> 16);
        msg[2] = (byte)(bodyLen >> 8);
        msg[3] = (byte)bodyLen;
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(4, 2), sigScheme);
        BinaryPrimitives.WriteUInt16BigEndian(msg.AsSpan(6, 2), (ushort)signature.Length);
        signature.CopyTo(msg.AsSpan(8));
        return msg;
    }

    private static byte[] BuildCertVerifyContent(ReadOnlySpan<byte> transcriptHash)
    {
        var label = "TLS 1.3, server CertificateVerify"u8;
        var buf = new byte[64 + label.Length + 1 + transcriptHash.Length];
        buf.AsSpan(0, 64).Fill(0x20);
        label.CopyTo(buf.AsSpan(64));
        buf[64 + label.Length] = 0x00;
        transcriptHash.CopyTo(buf.AsSpan(65 + label.Length));
        return buf;
    }

    private static void VerifyServerCertificateSignature(
        byte[]? leafCertDer,
        ReadOnlySpan<byte> certVerifyBody,
        ReadOnlySpan<byte> transcriptHash,
        string? expectedSpkiSha256Base64)
    {
        if (leafCertDer is null || leafCertDer.Length == 0 || certVerifyBody.Length < 4)
        {
            throw new AuthenticationException("Missing server certificate in TLS 1.3 handshake.");
        }

        var scheme = BinaryPrimitives.ReadUInt16BigEndian(certVerifyBody[..2]);
        var sigLen = BinaryPrimitives.ReadUInt16BigEndian(certVerifyBody.Slice(2, 2));
        if (4 + sigLen > certVerifyBody.Length)
        {
            throw new AuthenticationException("Malformed TLS 1.3 CertificateVerify message.");
        }

        var sig = certVerifyBody.Slice(4, sigLen);
        var toVerify = BuildCertVerifyContent(transcriptHash);

        using var cert = X509CertificateLoader.LoadCertificate(leafCertDer);
        if (!string.IsNullOrWhiteSpace(expectedSpkiSha256Base64))
        {
            using var ecdsaPin = cert.GetECDsaPublicKey();
            using var rsaPin = cert.GetRSAPublicKey();
            var spkiBytes = ecdsaPin is not null
                ? ecdsaPin.ExportSubjectPublicKeyInfo()
                : rsaPin is not null
                    ? rsaPin.ExportSubjectPublicKeyInfo()
                    : cert.GetPublicKey();

            var spkiSha256 = Convert.ToBase64String(SHA256.HashData(spkiBytes));
            var rawPubKeySha256 = Convert.ToBase64String(SHA256.HashData(cert.GetPublicKey()));
            if (!string.Equals(spkiSha256, expectedSpkiSha256Base64.Trim(), StringComparison.Ordinal) &&
                !string.Equals(rawPubKeySha256, expectedSpkiSha256Base64.Trim(), StringComparison.Ordinal))
            {
                throw new AuthenticationException("Server TLS 1.3 SPKI pin mismatch.");
            }
        }

        var ok = scheme switch
        {
            0x0403 => VerifyEcdsaCert(cert, toVerify, sig, HashAlgorithmName.SHA256),
            0x0503 => VerifyEcdsaCert(cert, toVerify, sig, HashAlgorithmName.SHA384),
            0x0804 => VerifyRsaPssCert(cert, toVerify, sig, HashAlgorithmName.SHA256),
            0x0805 => VerifyRsaPssCert(cert, toVerify, sig, HashAlgorithmName.SHA384),
            0x0806 => VerifyRsaPssCert(cert, toVerify, sig, HashAlgorithmName.SHA512),
            _ => false
        };

        if (!ok)
        {
            throw new AuthenticationException($"TLS 1.3 CertificateVerify cryptographic signature check failed (scheme=0x{scheme:X4}).");
        }
    }

    private static bool VerifyEcdsaCert(X509Certificate2 cert, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig, HashAlgorithmName hash)
    {
        using var ecdsa = cert.GetECDsaPublicKey();
        return ecdsa is not null && ecdsa.VerifyData(data, sig, hash, DSASignatureFormat.Rfc3279DerSequence);
    }

    private static bool VerifyRsaPssCert(X509Certificate2 cert, ReadOnlySpan<byte> data, ReadOnlySpan<byte> sig, HashAlgorithmName hash)
    {
        using var rsa = cert.GetRSAPublicKey();
        return rsa is not null && rsa.VerifyData(data, sig, hash, RSASignaturePadding.Pss);
    }

    private static byte[] BuildFinishedMessage(ReadOnlySpan<byte> baseSecret, ReadOnlySpan<byte> transcriptHash)
    {
        Span<byte> finishedKey = stackalloc byte[32];
        HkdfExpandLabel(baseSecret, "finished"u8, [], finishedKey);

        Span<byte> verifyData = stackalloc byte[32];
        _ = HMACSHA256.HashData(finishedKey, transcriptHash, verifyData);

        var msg = new byte[36];
        msg[0] = 0x14; // Finished
        msg[1] = 0x00;
        msg[2] = 0x00;
        msg[3] = 32;
        verifyData.CopyTo(msg.AsSpan(4, 32));
        return msg;
    }

    private static void VerifyFinishedMac(ReadOnlySpan<byte> baseSecret, ReadOnlySpan<byte> transcriptHash, ReadOnlySpan<byte> peerVerifyData)
    {
        Span<byte> finishedKey = stackalloc byte[32];
        HkdfExpandLabel(baseSecret, "finished"u8, [], finishedKey);

        Span<byte> expected = stackalloc byte[32];
        _ = HMACSHA256.HashData(finishedKey, transcriptHash, expected);

        if (!CryptographicOperations.FixedTimeEquals(expected, peerVerifyData))
        {
            throw new AuthenticationException("TLS 1.3 Finished MAC verification failed.");
        }
    }

    private static byte[] BuildCcsAndEncryptedHandshakeFlight(
        AesGcm aes,
        ReadOnlySpan<byte> iv,
        ulong seq,
        ReadOnlySpan<byte> handshakePlaintext)
    {
        var cipherLen = handshakePlaintext.Length + 1 + GcmTagSize;
        var outBuf = new byte[6 + 5 + cipherLen];
        // ChangeCipherSpec (0x14 0x03 0x03 0x00 0x01 0x01)
        outBuf[0] = 0x14;
        outBuf[1] = 0x03;
        outBuf[2] = 0x03;
        outBuf[3] = 0x00;
        outBuf[4] = 0x01;
        outBuf[5] = 0x01;

        WriteEncryptedRecordToSpan(outBuf.AsSpan(6), aes, iv, seq, 0x16, handshakePlaintext);
        return outBuf;
    }

    private static byte[] BuildServerWireFlight(
        ReadOnlySpan<byte> serverHelloMsg,
        AesGcm serverHsAes,
        ReadOnlySpan<byte> sHsIv,
        ReadOnlySpan<byte> encryptedFlightPlaintext)
    {
        var shRecLen = 5 + serverHelloMsg.Length;
        var ccsLen = 6;
        var encCipherLen = encryptedFlightPlaintext.Length + 1 + GcmTagSize;
        var encRecLen = 5 + encCipherLen;

        var total = shRecLen + ccsLen + encRecLen;
        var wire = new byte[total];
        var off = 0;

        wire[off] = 0x16;
        wire[off + 1] = 0x03;
        wire[off + 2] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(wire.AsSpan(off + 3, 2), (ushort)serverHelloMsg.Length);
        serverHelloMsg.CopyTo(wire.AsSpan(off + 5));
        off += shRecLen;

        wire[off++] = 0x14;
        wire[off++] = 0x03;
        wire[off++] = 0x03;
        wire[off++] = 0x00;
        wire[off++] = 0x01;
        wire[off++] = 0x01;

        WriteEncryptedRecordToSpan(wire.AsSpan(off, encRecLen), serverHsAes, sHsIv, 0, 0x16, encryptedFlightPlaintext);
        return wire;
    }

    private static void WriteEncryptedRecordToSpan(
        Span<byte> destRecord,
        AesGcm aes,
        ReadOnlySpan<byte> baseIv,
        ulong seq,
        byte innerContentType,
        ReadOnlySpan<byte> plaintext)
    {
        var cipherLen = plaintext.Length + 1 + GcmTagSize;
        destRecord[0] = 0x17;
        destRecord[1] = 0x03;
        destRecord[2] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(destRecord.Slice(3, 2), (ushort)cipherLen);

        var aad = destRecord[..5];
        var innerPlain = ArrayPool<byte>.Shared.Rent(plaintext.Length + 1);
        try
        {
            plaintext.CopyTo(innerPlain);
            innerPlain[plaintext.Length] = innerContentType;

            Span<byte> nonce = stackalloc byte[12];
            BuildNonce(baseIv, seq, nonce);

            var cipherSlice = destRecord.Slice(5, plaintext.Length + 1);
            var tagSlice = destRecord.Slice(5 + plaintext.Length + 1, GcmTagSize);
            aes.Encrypt(nonce, innerPlain.AsSpan(0, plaintext.Length + 1), cipherSlice, tagSlice, aad);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(innerPlain);
        }
    }

    private static async Task<(byte InnerContentType, byte[] Plaintext)> ReadEncryptedRecordAsync(
        Stream stream,
        AesGcm aes,
        byte[] baseIv,
        ulong seq,
        CancellationToken ct,
        bool returnZeroOnEof = false)
    {
        var hdr = new byte[5];
        while (true)
        {
            var gotHdr = await ReadExactOrEofAsync(stream, hdr, ct).ConfigureAwait(false);
            if (!gotHdr)
            {
                return returnZeroOnEof
                    ? (0, [])
                    : throw new IOException("Unexpected EOF while reading TLS 1.3 record header.");
            }

            var outerType = hdr[0];
            var recLen = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(3, 2));
            if (recLen > MaxCiphertextRecord)
            {
                throw new IOException($"TLS 1.3 ciphertext record length {recLen} exceeds RFC 8446 limit.");
            }

            //пропуск middlebox change_cipher_spec (0x14)
            if (outerType == 0x14)
            {
                var ccsBody = new byte[recLen];
                await stream.ReadExactlyAsync(ccsBody, ct).ConfigureAwait(false);
                continue;
            }

            if (outerType == 0x15)
            {
                var plainAlert = new byte[recLen];
                await stream.ReadExactlyAsync(plainAlert, ct).ConfigureAwait(false);
                return (0x15, plainAlert);
            }

            if (outerType != 0x17 || recLen < GcmTagSize + 1)
            {
                throw new IOException($"Unexpected outer TLS record type 0x{outerType:X2} (len={recLen}).");
            }

            var cipherBuf = ArrayPool<byte>.Shared.Rent(recLen);
            var plainBuf = ArrayPool<byte>.Shared.Rent(recLen - GcmTagSize);
            try
            {
                await stream.ReadExactlyAsync(cipherBuf.AsMemory(0, recLen), ct).ConfigureAwait(false);

                var payloadWithTypeLen = recLen - GcmTagSize;
                Span<byte> nonce = stackalloc byte[12];
                BuildNonce(baseIv, seq, nonce);

                aes.Decrypt(
                    nonce,
                    cipherBuf.AsSpan(0, payloadWithTypeLen),
                    cipherBuf.AsSpan(payloadWithTypeLen, GcmTagSize),
                    plainBuf.AsSpan(0, payloadWithTypeLen),
                    hdr);

                //удаление нулевого паддинга tls 1.3 с конца записи
                var typeIdx = payloadWithTypeLen - 1;
                while (typeIdx >= 0 && plainBuf[typeIdx] == 0x00)
                {
                    typeIdx--;
                }

                if (typeIdx < 0)
                {
                    throw new AuthenticationException("TLS 1.3 inner plaintext missing content type byte.");
                }

                var innerType = plainBuf[typeIdx];
                var content = plainBuf.AsSpan(0, typeIdx).ToArray();
                return (innerType, content);
            }
            catch (AuthenticationTagMismatchException ex)
            {
                throw new AuthenticationException("TLS 1.3 AEAD authentication tag mismatch.", ex);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(cipherBuf);
                ArrayPool<byte>.Shared.Return(plainBuf);
            }
        }
    }

    private static async Task<byte[]> ReadServerHelloHandshakeAsync(Stream stream, CancellationToken ct)
    {
        var hdr = new byte[5];
        while (true)
        {
            await stream.ReadExactlyAsync(hdr, ct).ConfigureAwait(false);
            var recType = hdr[0];
            var recLen = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(3, 2));
            if (recLen is 0 or > MaxPlaintextRecord)
            {
                throw new AuthenticationException($"Invalid ServerHello record length {recLen}.");
            }

            var body = new byte[recLen];
            await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);

            if (recType == 0x14)
            {
                continue;
            }

            return recType == 0x15
                ? throw new AuthenticationException($"Server rejected ClientHello with TLS alert {(body.Length >= 2 ? body[1] : 0)}.")
                : recType != 0x16 || body.Length < 4 || body[0] != 0x02
                    ? throw new AuthenticationException("Expected TLS 1.3 ServerHello handshake message.")
                    : body;
        }
    }

    private static async Task<byte[]> ReadClientFinishedHandshakeAsync(
        Stream stream,
        AesGcm clientHsAes,
        byte[] cHsIv,
        CancellationToken ct)
    {
        var (innerType, plain) = await ReadEncryptedRecordAsync(stream, clientHsAes, cHsIv, 0, ct).ConfigureAwait(false);
        return innerType != 0x16
            ? throw new AuthenticationException($"Expected encrypted handshake record from client, got 0x{innerType:X2}.")
            : plain;
    }

    private static bool TryParseServerHello(
        ReadOnlySpan<byte> shMsg,
        ReadOnlySpan<byte> expectedSessionId,
        out byte[] serverX25519Pub,
        out ushort cipherSuite)
    {
        serverX25519Pub = [];
        cipherSuite = 0;

        if (shMsg.Length < 44 || shMsg[0] != 0x02)
        {
            return false;
        }

        var bodyLen = (shMsg[1] << 16) | (shMsg[2] << 8) | shMsg[3];
        if (shMsg.Length < 4 + bodyLen)
        {
            return false;
        }

        var off = 4 + 2 + 32; // legacy_version + server_random
        var sidLen = shMsg[off++];
        if (sidLen != expectedSessionId.Length || off + sidLen + 3 > shMsg.Length)
        {
            return false;
        }

        if (!shMsg.Slice(off, sidLen).SequenceEqual(expectedSessionId))
        {
            return false;
        }

        off += sidLen;
        cipherSuite = BinaryPrimitives.ReadUInt16BigEndian(shMsg.Slice(off, 2));
        off += 3; // cipherSuite + compressionMethod

        if (off + 2 > shMsg.Length)
        {
            return false;
        }

        var extTotal = BinaryPrimitives.ReadUInt16BigEndian(shMsg.Slice(off, 2));
        off += 2;
        var extEnd = Math.Min(shMsg.Length, off + extTotal);
        var hasTls13 = false;

        while (off + 4 <= extEnd)
        {
            var extType = BinaryPrimitives.ReadUInt16BigEndian(shMsg.Slice(off, 2));
            var extLen = BinaryPrimitives.ReadUInt16BigEndian(shMsg.Slice(off + 2, 2));
            off += 4;
            if (off + extLen > extEnd)
            {
                return false;
            }

            var extBody = shMsg.Slice(off, extLen);
            if (extType == 0x002b && extBody.Length == 2 && BinaryPrimitives.ReadUInt16BigEndian(extBody) == 0x0304)
            {
                hasTls13 = true;
            }
            else if (extType == 0x0033 && extBody.Length >= 4)
            {
                var grp = BinaryPrimitives.ReadUInt16BigEndian(extBody[..2]);
                var kLen = BinaryPrimitives.ReadUInt16BigEndian(extBody.Slice(2, 2));
                if (grp == 0x001d && kLen == 32 && extBody.Length >= 36)
                {
                    serverX25519Pub = extBody.Slice(4, 32).ToArray();
                }
            }

            off += extLen;
        }

        return hasTls13 && serverX25519Pub.Length == 32;
    }

    private static string ParseEncryptedExtensionsAlpn(ReadOnlySpan<byte> body)
    {
        if (body.Length < 2)
        {
            return string.Empty;
        }

        var extLen = BinaryPrimitives.ReadUInt16BigEndian(body[..2]);
        var off = 2;
        var end = Math.Min(body.Length, 2 + extLen);

        while (off + 4 <= end)
        {
            var t = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(off, 2));
            var l = BinaryPrimitives.ReadUInt16BigEndian(body.Slice(off + 2, 2));
            off += 4;
            if (off + l > end)
            {
                break;
            }

            if (t == 0x0010 && l >= 3)
            {
                var pLen = body[off + 2];
                if (3 + pLen <= l)
                {
                    return Encoding.ASCII.GetString(body.Slice(off + 3, pLen));
                }
            }

            off += l;
        }

        return string.Empty;
    }

    private static byte[]? ParseLeafCertificateDer(ReadOnlySpan<byte> body)
    {
        if (body.Length < 4)
        {
            return null;
        }

        var ctxLen = body[0];
        var off = 1 + ctxLen;
        if (off + 6 > body.Length)
        {
            return null;
        }

        off += 3; // cert_list total len
        var leafLen = (body[off] << 16) | (body[off + 1] << 8) | body[off + 2];
        off += 3;
        return leafLen > 0 && off + leafLen <= body.Length
            ? body.Slice(off, leafLen).ToArray()
            : null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildNonce(ReadOnlySpan<byte> baseIv, ulong seq, Span<byte> nonce12)
    {
        baseIv[..12].CopyTo(nonce12);
        Span<byte> seqBytes = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(seqBytes, seq);
        for (var i = 0; i < 8; i++)
        {
            nonce12[4 + i] ^= seqBytes[i];
        }
    }

    private static void ComputeHandshakeSecrets(
        ReadOnlySpan<byte> ecdheShared,
        ReadOnlySpan<byte> chShTranscriptHash,
        out byte[] handshakeSecret,
        out byte[] clientHsTrafficSecret,
        out byte[] serverHsTrafficSecret)
    {
        Span<byte> zero32 = stackalloc byte[32];
        zero32.Clear();

        Span<byte> earlySecret = stackalloc byte[32];
        _ = HKDF.Extract(HashAlgorithmName.SHA256, zero32, zero32, earlySecret);

        Span<byte> derived0 = stackalloc byte[32];
        HkdfExpandLabel(earlySecret, "derived"u8, s_emptySha256, derived0);

        handshakeSecret = new byte[32];
        _ = HKDF.Extract(HashAlgorithmName.SHA256, ecdheShared, derived0, handshakeSecret);

        clientHsTrafficSecret = new byte[32];
        serverHsTrafficSecret = new byte[32];
        HkdfExpandLabel(handshakeSecret, "c hs traffic"u8, chShTranscriptHash, clientHsTrafficSecret);
        HkdfExpandLabel(handshakeSecret, "s hs traffic"u8, chShTranscriptHash, serverHsTrafficSecret);
    }

    private static void ComputeApplicationSecrets(
        ReadOnlySpan<byte> handshakeSecret,
        ReadOnlySpan<byte> chToServerFinishedHash,
        out byte[] clientAppTrafficSecret,
        out byte[] serverAppTrafficSecret)
    {
        Span<byte> zero32 = stackalloc byte[32];
        zero32.Clear();

        Span<byte> derived1 = stackalloc byte[32];
        HkdfExpandLabel(handshakeSecret, "derived"u8, s_emptySha256, derived1);

        Span<byte> masterSecret = stackalloc byte[32];
        _ = HKDF.Extract(HashAlgorithmName.SHA256, zero32, derived1, masterSecret);

        clientAppTrafficSecret = new byte[32];
        serverAppTrafficSecret = new byte[32];
        HkdfExpandLabel(masterSecret, "c ap traffic"u8, chToServerFinishedHash, clientAppTrafficSecret);
        HkdfExpandLabel(masterSecret, "s ap traffic"u8, chToServerFinishedHash, serverAppTrafficSecret);
    }

    private static void DeriveTrafficKeys(ReadOnlySpan<byte> trafficSecret, out byte[] key16, out byte[] iv12)
    {
        key16 = new byte[16];
        iv12 = new byte[12];
        HkdfExpandLabel(trafficSecret, "key"u8, [], key16);
        HkdfExpandLabel(trafficSecret, "iv"u8, [], iv12);
    }

    private static void HkdfExpandLabel(
        ReadOnlySpan<byte> secret,
        ReadOnlySpan<byte> labelAscii,
        ReadOnlySpan<byte> context,
        Span<byte> destination)
    {
        var prefix = "tls13 "u8;
        var fullLabelLen = prefix.Length + labelAscii.Length;
        Span<byte> info = stackalloc byte[2 + 1 + fullLabelLen + 1 + context.Length];

        BinaryPrimitives.WriteUInt16BigEndian(info[..2], (ushort)destination.Length);
        info[2] = (byte)fullLabelLen;
        prefix.CopyTo(info.Slice(3, prefix.Length));
        labelAscii.CopyTo(info.Slice(3 + prefix.Length, labelAscii.Length));
        var ctxPos = 3 + fullLabelLen;
        info[ctxPos] = (byte)context.Length;
        if (!context.IsEmpty)
        {
            context.CopyTo(info[(ctxPos + 1)..]);
        }

        HKDF.Expand(HashAlgorithmName.SHA256, secret, destination, info);
    }

    private static async Task<bool> ReadExactOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0 && total == 0)
            {
                return false;
            }

            if (n == 0)
            {
                throw new EndOfStreamException("Truncated TLS 1.3 record header.");
            }

            total += n;
        }

        return true;
    }
}
