using System;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nalix.Abstractions;
using Nalix.Environment.Configuration;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Benchmarks.Shared;

namespace Nalix.Framework.Benchmarks.Memory;

[Config(typeof(NalixBenchmarkConfig))]
public class ObjectPoolBenchmarks
{
    private ObjectPoolManager _poolManager = null!;

    public class SamplePoolable : IPoolable
    {
        public int Value1 { get; set; }
        public string? Value2 { get; set; }

        public void ResetForPool()
        {
            Value1 = 0;
            Value2 = null;
        }
    }

    [GlobalSetup]
    public void Setup()
    {
        // Disable trimming to avoid scheduling recurring jobs that depend on TaskManager/InstanceManager
        var options = ConfigurationManager.Instance.Get<ObjectPoolOptions>();
        options.EnableObjectTrimming = false;
        options.EnableDiagnostics = false;
        options.EnableLeakDetection = false;

        _poolManager = new ObjectPoolManager(options);

        // Preallocate (warmup) to ensure the pool has cached instances ready
        var obj = _poolManager.Get<SamplePoolable>();
        _poolManager.Return(obj);
    }

    [Benchmark(Baseline = true)]
    public SamplePoolable RawAllocation()
    {
        return new SamplePoolable { Value1 = 42, Value2 = "baseline" };
    }

    [Benchmark]
    public void RentAndReturn_ObjectPool()
    {
        var obj = _poolManager.Get<SamplePoolable>();
        obj.Value1 = 42;
        obj.Value2 = "pooled";
        _poolManager.Return(obj);
    }

    private const int Workers = 4;
    private const int PerWorker = 10_000;

    /// <summary>Four threads renting and returning at once: every packet and lease on a multi-core server.</summary>
    [Benchmark(OperationsPerInvoke = Workers * PerWorker)]
    public void RentAndReturn_ObjectPool_Parallel()
        => _ = Parallel.For(0, Workers, _ =>
        {
            for (int i = 0; i < PerWorker; i++)
            {
                SamplePoolable obj = _poolManager.Get<SamplePoolable>();
                obj.Value1 = i;
                _poolManager.Return(obj);
            }
        });

    /// <summary>
    /// A type rented and never returned (a client's responses are handed to its caller): the pool stays empty.
    /// </summary>
    [Benchmark]
    public SamplePoolable Rent_NeverReturned() => _poolManager.Get<SamplePoolable>();
}
