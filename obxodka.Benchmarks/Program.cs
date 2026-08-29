namespace obxodka.Benchmarks;

public class Program
{
    public static void Main(string[] args)
    {
        var switcher = BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly);
        _ = switcher.Run(args);
    }
}
