// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Options;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Buffers;

public sealed class BufferPoolManagerTests
{
    [Fact]
    public void Return_WhenArrayIsNull_DoesNotThrow()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));

        Exception? ex = Record.Exception(() => manager.Return(null));

        Assert.Null(ex);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_DoesNotThrow()
    {
        BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));

        Exception? ex = Record.Exception(() =>
        {
            manager.Dispose();
            manager.Dispose();
        });

        Assert.Null(ex);
    }

    [Fact]
    public void Rent_WhenConfiguredWithDefaults_ReturnsSizedBuffer()
    {
        BufferOptions config = MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false);
        using BufferPoolManager manager = new(config);

        byte[] rented = manager.Rent(300);
        manager.Return(rented);

        Assert.True(rented.Length >= 300);
        Assert.Equal(0, manager.MinBufferSize);
        Assert.Equal(0, manager.MaxBufferSize);
    }

    [Fact]
    public void GenerateReport_AfterRent_ContainsStatusHeader()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));
        _ = manager.Rent(256);

        string report = manager.GenerateReport();

        Assert.Contains("BufferPoolManager Status", report);
    }

    [Fact]
    public void Rent_WhenSizeIs2114_ReturnsBufferAtLeastThatLarge()
    {
        // Rent through the manager directly instead of mutating the process-wide
        // ByteArrayPool delegates.  The original code called
        // BufferLease.ByteArrayPool.Configure(manager) which left a disposed
        // delegate target in the global static field after this method returned.
        using var manager = new BufferPoolManager();
        byte[] arr = manager.Rent(2114);
        try
        {
            Assert.True(arr.Length >= 2114, $"Expected >= 2114, got {arr.Length}");
        }
        finally
        {
            manager.Return(arr);
        }
    }

    [Fact]
    public void RentAndReturn_FromManyThreads_KeepExactCountsAndNoOutstandingBuffers()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));
        const int Threads = 8, PerThread = 5_000;

        _ = Parallel.For(0, Threads, new ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                manager.Return(manager.Rent(1500));
            }
        });

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            manager.WriteReportData(writer);
        }

        using JsonDocument report = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(Threads * PerThread, report.RootElement.GetProperty("RentCount").GetInt64());
        Assert.Equal(Threads * PerThread, report.RootElement.GetProperty("ReturnCount").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("OutstandingCount").GetInt64());
    }

    [Fact]
    public void Rent_AfterDoubleReturnsMixedWithOtherTraffic_NeverHandsOutABufferThatIsHeld()
    {
        // The guard must find an earlier return wherever it landed, also after other buffers left the table.
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));
        Random random = new(42);
        HashSet<byte[]> held = new(ReferenceEqualityComparer.Instance);
        List<byte[]> returned = [];

        for (int step = 0; step < 20_000; step++)
        {
            int action = random.Next(3);
            if (action == 0 || held.Count == 0)
            {
                byte[] buffer = manager.Rent(3000);
                Assert.True(held.Add(buffer), "A buffer still held was handed out again.");
                _ = returned.Remove(buffer);
            }
            else if (action == 1)
            {
                byte[] buffer = held.ElementAt(random.Next(held.Count));
                _ = held.Remove(buffer);
                manager.Return(buffer);
                returned.Add(buffer);
            }
            else if (returned.Count > 0)
            {
                manager.Return(returned[random.Next(returned.Count)]); // Caller bug: a second return.
            }
        }
    }

    [Fact]
    public async Task Return_OfTheSameBufferFromSeveralThreadsAtOnce_PoolsItAtMostOnce()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));
        const int threads = 4;

        for (int round = 0; round < 200; round++)
        {
            byte[] buffer = manager.Rent(5000);
            using Barrier barrier = new(threads);
            Task[] returns = [.. Enumerable.Range(0, threads).Select(_ => Task.Run(() =>
            {
                barrier.SignalAndWait();
                manager.Return(buffer);
            }))];
            await Task.WhenAll(returns);

            byte[][] rented = [.. Enumerable.Range(0, 32).Select(_ => manager.Rent(5000))];
            Assert.True(rented.Count(b => ReferenceEquals(b, buffer)) <= 1, "The buffer was pooled more than once.");
            Assert.Equal(rented.Length, rented.Distinct(ReferenceEqualityComparer.Instance).Count());

            foreach (byte[] b in rented)
            {
                manager.Return(b);
            }
        }
    }

    [Fact]
    public async Task WriteReportData_AfterRentsAndReturnsOnSeveralThreads_SumsEveryProcessorsCounters()
    {
        using BufferPoolManager manager = new(MemoryTestSupport.CreateBufferOptions(enableMemoryTrimming: false));

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                manager.Return(manager.Rent(100));
            }
        })));

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            manager.WriteReportData(writer);
        }

        using JsonDocument report = JsonDocument.Parse(stream.ToArray());
        Assert.Equal(4000, report.RootElement.GetProperty("RentCount").GetInt64());
        Assert.Equal(4000, report.RootElement.GetProperty("ReturnCount").GetInt64());
        Assert.Equal(0, report.RootElement.GetProperty("OutstandingCount").GetInt64());
    }
}
