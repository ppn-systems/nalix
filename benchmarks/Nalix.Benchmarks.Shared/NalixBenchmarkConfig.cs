using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Jobs;

namespace Nalix.Benchmarks.Shared;

public class NalixBenchmarkConfig : ManualConfig
{
    public NalixBenchmarkConfig()
    {
        AddDiagnoser(MemoryDiagnoser.Default);
        AddDiagnoser(ThreadingDiagnoser.Default);
        AddColumn(StatisticColumn.P95);
        AddJob(Job.Default
            .WithGcServer(true)
            .WithGcConcurrent(true)
            .WithWarmupCount(10)
            .WithIterationCount(25));
    }
}
