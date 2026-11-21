using System;
using System.Buffers;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nalix.Environment.Configuration;
using Nalix.Environment.Memory;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Options;
using Nalix.Benchmarks.Shared;

namespace Nalix.Framework.Benchmarks.Memory;

[Config(typeof(NalixBenchmarkConfig))]
public class BufferPoolBenchmarks
{
    private BufferPoolManager _bufferPoolManager = null!;

    [Params(64, 1024, 16384)]
    public int Size;

    [GlobalSetup]
    public void Setup()
    {
        // Leak tracking is off so the measurement is the pool itself. BufferPoolManager no longer
        // schedules a recurring trim, so there is nothing else here to keep off the benchmark.
        var options = ConfigurationManager.Instance.Get<BufferOptions>();
        options.EnableBufferLeakDetection = false;
        options.EnableBufferLeakStackTrace = false;

        _bufferPoolManager = new BufferPoolManager(options);

        // Configure BufferLease ByteArrayPool
        BufferLease.ByteArrayPool.Configure(_bufferPoolManager);
    }

    [Benchmark(Baseline = true)]
    public byte[] RawAllocation()
    {
        return new byte[Size];
    }

    [Benchmark]
    public void ArrayPool_Shared()
    {
        byte[] arr = ArrayPool<byte>.Shared.Rent(Size);
        ArrayPool<byte>.Shared.Return(arr);
    }

    [Benchmark]
    public void BufferPoolManager_RentReturn()
    {
        byte[] arr = _bufferPoolManager.Rent(Size);
        _bufferPoolManager.Return(arr);
    }

    [Benchmark]
    public void BufferLease_RentDispose()
    {
        using BufferLease lease = BufferLease.Rent(Size);
        lease.SpanFull[0] = 1;
    }

    private const int Workers = 4;
    private const int PerWorker = 10_000;

    /// <summary>Four threads renting and returning at once: what a multi-core server does on every packet.</summary>
    [Benchmark(OperationsPerInvoke = Workers * PerWorker)]
    public void ArrayPool_Shared_Parallel()
        => _ = Parallel.For(0, Workers, _ =>
        {
            for (int i = 0; i < PerWorker; i++)
            {
                ArrayPool<byte>.Shared.Return(ArrayPool<byte>.Shared.Rent(Size));
            }
        });

    /// <inheritdoc cref="ArrayPool_Shared_Parallel"/>
    [Benchmark(OperationsPerInvoke = Workers * PerWorker)]
    public void BufferPoolManager_RentReturn_Parallel()
        => _ = Parallel.For(0, Workers, _ =>
        {
            for (int i = 0; i < PerWorker; i++)
            {
                _bufferPoolManager.Return(_bufferPoolManager.Rent(Size));
            }
        });
}
