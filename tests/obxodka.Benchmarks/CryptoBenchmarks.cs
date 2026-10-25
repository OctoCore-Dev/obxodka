namespace obxodka.Benchmarks;

[MemoryDiagnoser]
public class CryptoBenchmarks : IDisposable
{
    private byte[] _payload1420 = null!;
    private byte[] _payload64 = null!;
    private byte[] _packed1420 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload1420 = new byte[1420];
        Random.Shared.NextBytes(_payload1420);

        _payload64 = new byte[64];
        Random.Shared.NextBytes(_payload64);

        _packed1420 = Obfuscator.Pack(_payload1420, _payload1420.Length, out _);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (_packed1420 != null)
        {
            ArrayPool<byte>.Shared.Return(_packed1420);
        }
    }

    public void Dispose()
    {
        Cleanup();
        GC.SuppressFinalize(this);
    }

    [Benchmark(Description = "Obfuscator Pack MTU (1420 B)")]
    public void PackMtu()
    {
        var packed = Obfuscator.Pack(_payload1420, _payload1420.Length, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "Obfuscator TryDecodeLengths MTU (1420 B)")]
    public bool DecodeMtu() => Obfuscator.TryDecodeLengths(_packed1420.AsSpan(0, 8), out _, out _);

    [Benchmark(Description = "Obfuscator Pack Small (64 B)")]
    public void PackSmall()
    {
        var packed = Obfuscator.Pack(_payload64, _payload64.Length, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "Obfuscator.PackSmart (1420 B)")]
    public void ObfuscateMtu()
    {
        var packed = Obfuscator.PackSmart(_payload1420, _payload1420.Length, out _, isProxied: false);
        ArrayPool<byte>.Shared.Return(packed);
    }
}
