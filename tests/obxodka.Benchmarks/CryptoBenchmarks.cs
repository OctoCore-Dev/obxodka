namespace obxodka.Benchmarks;

[MemoryDiagnoser]
public sealed class CryptoBenchmarks : IDisposable
{
    private byte[] _payload1420 = null!;
    private byte[] _payload64 = null!;
    private byte[] _packed1420 = null!;

    [GlobalSetup]
    public void Setup()
    {
        _payload1420 = new byte[1420];
        RandomNumberGenerator.Fill(_payload1420);

        _payload64 = new byte[64];
        RandomNumberGenerator.Fill(_payload64);

        _packed1420 = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, _payload1420, out _);
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

    [Benchmark(Description = "ObxodkaFraming Pack MTU (1420 B)")]
    public void PackMtu()
    {
        var packed = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, _payload1420, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }

    [Benchmark(Description = "ObxodkaFraming TryDecodeHeader MTU (1420 B)")]
    public bool DecodeMtu() => ObxodkaFraming.TryDecodeHeader(_packed1420.AsSpan(0, ObxodkaFraming.HeaderSize), out _, out _, out _, out _);

    [Benchmark(Description = "ObxodkaFraming Pack Small (64 B)")]
    public void PackSmall()
    {
        var packed = ObxodkaFraming.Pack(ObxodkaFraming.CmdData, _payload64, out _);
        ArrayPool<byte>.Shared.Return(packed);
    }
}
