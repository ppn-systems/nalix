using System;
using System.Buffers;
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
}
