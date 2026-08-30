namespace obxodka.Benchmarks;

[MemoryDiagnoser]
public class MemoryPoolBenchmarks
{
    private const int PacketSize = 1420;

    [Benchmark(Baseline = true, Description = "Traditional GC Allocation (new byte[1420])")]
    public byte[] AllocateGcArray()
    {
        var array = new byte[PacketSize];
        array[0] = 0x45;
        array[PacketSize - 1] = 0xFF;
        return array;
    }

    [Benchmark(Description = "Zero-Allocation ArrayPool (Rent & Return)")]
    public void ArrayPoolRentReturn()
    {
        var array = ArrayPool<byte>.Shared.Rent(PacketSize);
        array[0] = 0x45;
        array[PacketSize - 1] = 0xFF;
        ArrayPool<byte>.Shared.Return(array);
    }
}
