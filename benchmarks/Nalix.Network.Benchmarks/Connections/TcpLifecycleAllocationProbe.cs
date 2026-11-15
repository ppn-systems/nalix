using System;
using System.Diagnostics;

namespace Nalix.Network.Benchmarks.Connections;

/// <summary>
/// Direct allocation probe, run with <c>--probe</c>. Measures bytes allocated on the calling thread and GC collection
/// counts around fixed loops, independently of BenchmarkDotNet's allocation column.
/// </summary>
internal static class TcpLifecycleAllocationProbe
{
    public static void Run()
    {
        if (System.Environment.GetEnvironmentVariable("PROBE_SEND_ONLY") == "1")
        {
            RunSendMedians();
            return;
        }

        TcpTransportLifecycleBenchmarks b = new();
        b.Setup();
        try
        {
            Measure("CreateAndDispose_Connection", 20_000, b.CreateAndDispose_Connection);
            Measure("CreateBindDispose_Passthrough", 20_000, b.CreateBindDispose_Passthrough);
            Measure("Churn_TcpConnections (per connection)", 20_000 / 256, b.Churn_TcpConnections, opsPerCall: 256);
            Measure("Send_Steady", 200_000, b.Send_Steady);
            Measure("SendAsync_Steady", 200_000, () => _ = b.SendAsync_Steady());
        }
        finally
        {
            b.Cleanup();
        }
    }

    /// <summary>Median of 9 repetitions of 200k sends each, for interleaved A/B runs.</summary>
    private static void RunSendMedians()
    {
        TcpTransportLifecycleBenchmarks b = new();
        b.Setup();
        try
        {
            MedianNs("Send_Steady", b.Send_Steady);
            MedianNs("SendAsync_Steady", () => _ = b.SendAsync_Steady());
        }
        finally
        {
            b.Cleanup();
        }
    }

    private static void MedianNs(string name, Action action)
    {
        for (int i = 0; i < 200_000; i++) action();

        double[] samples = new double[9];
        for (int r = 0; r < samples.Length; r++)
        {
            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < 200_000; i++) action();
            samples[r] = (Stopwatch.GetTimestamp() - t0) * 1e9 / Stopwatch.Frequency / 200_000;
        }

        Array.Sort(samples);
        Console.WriteLine($"MEDIAN | {name,-18} | ns/op={samples[4],8:F1} | min={samples[0],8:F1} | max={samples[8],8:F1}");
    }

    private static void Measure(string name, int calls, Action action, int opsPerCall = 1)
    {
        for (int i = 0; i < 200; i++) action();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long bytes0 = GC.GetAllocatedBytesForCurrentThread();
        long t0 = Stopwatch.GetTimestamp();

        for (int i = 0; i < calls; i++) action();

        long ticks = Stopwatch.GetTimestamp() - t0;
        long bytes = GC.GetAllocatedBytesForCurrentThread() - bytes0;
        long ops = (long)calls * opsPerCall;

        Console.WriteLine($"PROBE | {name,-40} | ops={ops,8} | bytes/op={(double)bytes / ops,9:F1} | ns/op={ticks * 1e9 / Stopwatch.Frequency / ops,10:F1} | gc0={GC.CollectionCount(0) - g0} gc1={GC.CollectionCount(1) - g1} gc2={GC.CollectionCount(2) - g2}");
    }
}
