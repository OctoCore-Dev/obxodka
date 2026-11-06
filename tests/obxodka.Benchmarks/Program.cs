namespace obxodka.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        _ = switcher.Run(args);
    }
}
