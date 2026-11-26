using System.Security.Cryptography.X509Certificates;
using obxodka.Shared.Stealth.Tls13;

namespace obxodka.Client.Tests.Protocols;

[Collection("GlobalStaticState")]
[Trait("Category", "Unit")]
[Trait("Category", "Protocols")]
public sealed class ObxV8RealityAndStealthTests
{
    [Fact]
    public void Curve25519_MatchesOfficialRfc7748Section61TestVectors()
    {
        var alicePriv = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var expectedAlicePub = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");

        var bobPriv = Convert.FromHexString("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        var expectedBobPub = Convert.FromHexString("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");

        var expectedShared = Convert.FromHexString("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        Span<byte> alicePub = stackalloc byte[32];
        Span<byte> bobPub = stackalloc byte[32];
        Curve25519.GetPublicKey(alicePriv, alicePub);
        Curve25519.GetPublicKey(bobPriv, bobPub);

        Assert.Equal(expectedAlicePub, alicePub.ToArray());
        Assert.Equal(expectedBobPub, bobPub.ToArray());

        Span<byte> sharedAb = stackalloc byte[32];
        Span<byte> sharedBa = stackalloc byte[32];
        Assert.True(Curve25519.ComputeSharedSecret(alicePriv, bobPub, sharedAb));
        Assert.True(Curve25519.ComputeSharedSecret(bobPriv, alicePub, sharedBa));

        Assert.Equal(expectedShared, sharedAb.ToArray());
        Assert.Equal(expectedShared, sharedBa.ToArray());
    }

    [Fact]
    public async Task Tls13CryptoStream_And_ObxRealityAuth_CompleteVerifiedDuplexSessionAsync()
    {
        const string spkiPinSeed = "TEST_NODE_SPKI_PIN_V8==";
        Span<byte> realityPriv = stackalloc byte[32];
        Span<byte> realityPub = stackalloc byte[32];
        ObxRealityAuth.DeriveNodeRealityKeyPair(spkiPinSeed, realityPriv, realityPub);
        var serverPrivCopy = realityPriv.ToArray();
        var serverPubCopy = realityPub.ToArray();

        using var serverEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var certReq = new CertificateRequest("CN=cdn.jsdelivr.net", serverEcdsa, HashAlgorithmName.SHA256);
        using var serverCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30));
        var expectedSpkiPin = Convert.ToBase64String(SHA256.HashData(serverEcdsa.ExportSubjectPublicKeyInfo()));

        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(4);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

        var serverTask = Task.Run(async () =>
        {
            using var accepted = await listener.AcceptAsync(cts.Token);
            await using var net = new NetworkStream(accepted, ownsSocket: false);

            var initial = new byte[2048];
            var n = await net.ReadAsync(initial, cts.Token);
            Assert.True(n > 100);

            var verified = ObxRealityAuth.TryVerifyClientHello(
                initial.AsSpan(0, n),
                serverPrivCopy,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                out var chHandshakeMsg,
                out _,
                out var sid,
                out var clientEphPub,
                out var sniHost,
                out var replayFp);

            Assert.True(verified);
            Assert.Equal("cdn.jsdelivr.net", sniHost);
            Assert.NotEqual(0UL, replayFp);

            await using var serverTls = await Tls13CryptoStream.AcceptServerAsync(
                net,
                chHandshakeMsg,
                sid,
                clientEphPub,
                serverCert,
                targetCertPaddingBytes: 1900,
                leaveInnerOpen: true,
                cts.Token);

            var rx = new byte[4096];
            var rxLen = await serverTls.ReadAsync(rx, cts.Token);
            var msg = Encoding.UTF8.GetString(rx, 0, rxLen);
            Assert.Equal("PING_OBX_V8_REALITY", msg);

            var bigPayload = new byte[32768];
            RandomNumberGenerator.Fill(bigPayload);
            await serverTls.WriteAsync(bigPayload, cts.Token);
            await serverTls.FlushAsync(cts.Token);
            return bigPayload;
        }, cts.Token);

        using var clientSock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await clientSock.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cts.Token);
        await using var clientNet = new NetworkStream(clientSock, ownsSocket: true);

        await using var clientTls = await Tls13CryptoStream.ConnectClientAsync(
            clientNet,
            "cdn.jsdelivr.net",
            serverPubCopy,
            expectedSpkiPin,
            leaveInnerOpen: false,
            cts.Token);

        Assert.Equal("h2", clientTls.NegotiatedApplicationProtocol);

        var pingBytes = "PING_OBX_V8_REALITY"u8.ToArray();
        await clientTls.WriteAsync(pingBytes, cts.Token);
        await clientTls.FlushAsync(cts.Token);

        var receivedAll = new byte[32768];
        var offset = 0;
        while (offset < receivedAll.Length)
        {
            var got = await clientTls.ReadAsync(receivedAll.AsMemory(offset), cts.Token);
            Assert.True(got > 0);
            offset += got;
        }

        var expectedBig = await serverTask;
        Assert.Equal(expectedBig, receivedAll);
    }

    [Fact]
    public void ObxRealityAuth_RejectsUnauthenticatedBrowserOrProbeClientHello()
    {
        Span<byte> priv = stackalloc byte[32];
        Span<byte> pub = stackalloc byte[32];
        ObxRealityAuth.DeriveNodeRealityKeyPair("SERVER_KEY", priv, pub);

        //обычный браузерный clienthello со случайным session_id (активный зонд тспу)
        var probeHello = ChromiumHello.Build("cdn.jsdelivr.net"u8);

        var ok = ObxRealityAuth.TryVerifyClientHello(
            probeHello,
            priv,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            out _,
            out _,
            out _,
            out _,
            out _,
            out _);

        Assert.False(ok);
    }

    [Fact]
    public void InnerTlsMorpher_DetectsAndMorphsNestedTlsHandshakeWithoutBloatingBulkVideo()
    {
        //вложенный ipv4 + tcp пакет с большим post-quantum tls clienthello (1240 байт > 1100б)
        var innerTlsPkt = new byte[20 + 20 + 1240];
        innerTlsPkt[0] = 0x45; // IPv4 IHL=20
        innerTlsPkt[9] = 6; // TCP
        innerTlsPkt[20 + 12] = 0x50; // TCP header 20B
        innerTlsPkt[40] = 0x16; // TLS Handshake
        innerTlsPkt[41] = 0x03;
        innerTlsPkt[42] = 0x01;

        Assert.True(InnerTlsMorpher.IsInnerTlsControlOrHandshake(innerTlsPkt));
        var morphedPad = InnerTlsMorpher.CalculateAntiMlPadding(innerTlsPkt, ObxodkaFraming.CmdData, 1280, isEarlySession: false);
        Assert.True(morphedPad >= 24);

        //обычный пакет видеопотока 0x17 application data (1240 байт) вне стартового окна 16кб не получает лишнего оверхеда
        var bulkDataPkt = (byte[])innerTlsPkt.Clone();
        bulkDataPkt[40] = 0x17;
        Assert.False(InnerTlsMorpher.IsInnerTlsControlOrHandshake(bulkDataPkt));
        var bulkPad = InnerTlsMorpher.CalculateAntiMlPadding(bulkDataPkt, ObxodkaFraming.CmdData, 1280, isEarlySession: false);
        Assert.Equal(0, bulkPad);
    }

    [Fact]
    public void ObxodkaFraming_EcdsaHandshakeAndStealthCookie_RoundtripAndRejectTamper()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();
        const string thumb = "AABBCCDDEEFF00112233445566778899AABBCCDD";
        const uint seed = 0x1234ABCDu;
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sig = ObxodkaFraming.SignHandshakeEcdsa(ecdsa, thumb, 0, seed, ts);
        Assert.False(string.IsNullOrWhiteSpace(sig));
        Assert.True(ObxodkaFraming.VerifyHandshakeEcdsa(spki, thumb, 0, seed, ts, sig));
        Assert.False(ObxodkaFraming.VerifyHandshakeEcdsa(spki, thumb, 1, seed, ts, sig));

        var rawHs = $"{thumb}|R:0|M:1360|K:{seed:X8}|T:{ts}|P:ABCDEF0123456789|S:{sig}";
        var cookie = ObxodkaFraming.PackStealthCookieHeader("down", rawHs, ts);
        Assert.StartsWith("_cfuvid=", cookie, StringComparison.Ordinal);
        Assert.DoesNotContain("X-Obx", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(thumb, cookie, StringComparison.OrdinalIgnoreCase);

        Assert.True(ObxodkaFraming.TryUnpackStealthCookieHeader(cookie, out var mode, out var unpackedHs));
        Assert.Equal("down", mode);
        Assert.Equal(rawHs, unpackedHs);
    }

    [Fact]
    public void CleanEdgeScanner_IdentifiesThrottled188114PoolAndGeneratesCleanSubnets()
    {
        Assert.True(CleanEdgeScanner.IsThrottledDefaultEdgeIp(IPAddress.Parse("188.114.96.3")));
        Assert.True(CleanEdgeScanner.IsThrottledDefaultEdgeIp(IPAddress.Parse("188.114.98.224")));
        Assert.False(CleanEdgeScanner.IsThrottledDefaultEdgeIp(IPAddress.Parse("104.18.32.7")));
        Assert.False(CleanEdgeScanner.IsThrottledDefaultEdgeIp(IPAddress.Parse("172.64.155.2")));

        var batch = CleanEdgeScanner.GenerateProbeBatch(6);
        Assert.Equal(6, batch.Length);
        Assert.All(batch, ip => Assert.False(CleanEdgeScanner.IsThrottledDefaultEdgeIp(ip)));
    }

    [Fact]
    public async Task Tls13ClientHello_AlwaysEmitsUniqueExtensionsAndMatchingGreaseKeyShareAsync()
    {
        for (var iteration = 0; iteration < 64; iteration++)
        {
            var captured = new MemoryStream(1024);
            using var probeStream = new CaptureOnlyStream(captured);
            try
            {
                _ = await Tls13CryptoStream.ConnectClientAsync(
                    probeStream,
                    "cdn.jsdelivr.net",
                    ReadOnlyMemory<byte>.Empty,
                    null,
                    leaveInnerOpen: true,
                    CancellationToken.None);
            }
            catch (EndOfStreamException)
            {
                //поток возвращает eof сразу после записи clienthello
            }

            var record = captured.ToArray();
            Assert.True(record.Length > 100);
            Assert.Equal(0x16, record[0]);

            var msg = record.AsSpan(5);
            var off = 4 + 2 + 32; // type+len + version + random
            var sidLen = msg[off++];
            off += sidLen;
            var ciphersLen = (msg[off] << 8) | msg[off + 1];
            off += 2 + ciphersLen;
            var compLen = msg[off++];
            off += compLen;
            var extTotalLen = (msg[off] << 8) | msg[off + 1];
            off += 2;
            var extEnd = off + extTotalLen;

            var seenExtTypes = new HashSet<ushort>();
            var supportedGroups = new HashSet<ushort>();
            List<ushort> keyShareGroups = [];

            while (off + 4 <= extEnd)
            {
                var extType = (ushort)((msg[off] << 8) | msg[off + 1]);
                var extLen = (msg[off + 2] << 8) | msg[off + 3];
                off += 4;
                Assert.True(seenExtTypes.Add(extType), $"Duplicate TLS extension 0x{extType:X4} on iteration {iteration}.");

                var body = msg.Slice(off, extLen);
                if (extType == 0x000a && body.Length >= 2)
                {
                    var listLen = (body[0] << 8) | body[1];
                    for (var p = 2; p + 2 <= 2 + listLen; p += 2)
                    {
                        _ = supportedGroups.Add((ushort)((body[p] << 8) | body[p + 1]));
                    }
                }
                else if (extType == 0x0033 && body.Length >= 2)
                {
                    var sharesLen = (body[0] << 8) | body[1];
                    var p = 2;
                    while (p + 4 <= 2 + sharesLen)
                    {
                        var group = (ushort)((body[p] << 8) | body[p + 1]);
                        var kLen = (body[p + 2] << 8) | body[p + 3];
                        keyShareGroups.Add(group);
                        p += 4 + kLen;
                    }
                }

                off += extLen;
            }

            Assert.NotEmpty(keyShareGroups);
            Assert.All(keyShareGroups, g => Assert.Contains(g, supportedGroups));
        }
    }

    private sealed class CaptureOnlyStream(MemoryStream sink) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => sink.Length;
        public override long Position { get => sink.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => sink.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            sink.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
