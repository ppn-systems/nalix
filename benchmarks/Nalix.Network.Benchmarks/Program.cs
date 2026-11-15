using BenchmarkDotNet.Running;

namespace Nalix.Network.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--probe")
        {
            Connections.TcpLifecycleAllocationProbe.Run();
            return;
        }

        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
