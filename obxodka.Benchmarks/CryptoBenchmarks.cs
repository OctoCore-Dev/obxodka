namespace obxodka.Benchmarks;

[MemoryDiagnoser]
public class CryptoBenchmarks : IDisposable
{
    private byte[] _masterKey = null!;
    private AesGcm _crypto = null!;
    private byte[] _payload1420 = null!;
    private byte[] _payload64 = null!;
    private byte[] _packed1420 = null!;
    private byte[] _packedAuth = null!;
    private int _packed1420Len;
    private int _packedAuthLen;
    private const string Thumbprint = "0123456789ABCDEF0123456789ABCDEF01234567";

    [GlobalSetup]
    public void Setup()
    {
        _masterKey = SHA256.HashData("BenchmarkSecretKey2026_HighLoad"u8.ToArray());
        _crypto = new AesGcm(_masterKey, 16);

        _payload1420 = new byte[1420];
        Random.Shared.NextBytes(_payload1420);

        _payload64 = new byte[64];
        Random.Shared.NextBytes(_payload64);

        _packed1420 = FechsueCodec.Pack(_payload1420, _payload1420.Length, 12345, _crypto, out _packed1420Len);
        _packedAuth = FechsueCodec.PackAuth(Thumbprint, 0, out _packedAuthLen);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_packed1420 != null)
        {
            ArrayPool<byte>.Shared.Return(_packed1420);
        }

        if (_packedAuth != null)
        {
            ArrayPool<byte>.Shared.Return(_packedAuth);
        }

        _crypto?.Dispose();
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "AES-GCM Pack MTU (1420 B)")]
    public void PackMtu()
    {
        var packed = FechsueCodec.Pack(_payload1420, _payload1420.Length, 12345, _crypto, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "AES-GCM Unpack MTU (1420 B)")]
    public void UnpackMtu()
    {
        if (FechsueCodec.TryUnpack(_packed1420, _packed1420Len, _crypto, out _, out var unp, out _))
        {
            if (unp != null)
            {
                ArrayPool<byte>.Shared.Return(unp);
            }
        }
    }

    [Benchmark(Description = "AES-GCM Pack Small (64 B)")]
    public void PackSmall()
    {
        var packed = FechsueCodec.Pack(_payload64, _payload64.Length, 12345, _crypto, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "Auth Handshake PackAuth")]
    public void PackAuth()
    {
        var packed = FechsueCodec.PackAuth(Thumbprint, 0, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "Auth Handshake TryUnpackAuth")]
    public bool UnpackAuth() => FechsueCodec.TryUnpackAuth(_packedAuth.AsSpan(0, _packedAuthLen), out _, out _, out _);

    [Benchmark(Description = "Obfuscator.PackSmart (1420 B)")]
    public void ObfuscateMtu()
    {
        var packed = Obfuscator.PackSmart(_payload1420, _payload1420.Length, out _, isProxied: false);
        ArrayPool<byte>.Shared.Return(packed);
    }
}
