// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Objects;

[Collection(TestCollections.TypePoolPhase1)]
public sealed class ObjectPoolManagerTests
{
    [Fact]
    public void Get_AfterReturn_TracksStatisticsAndReports()
    {
        ObjectPoolOptions options = new() { DefaultMaxPoolSize = 1024 };
        ObjectPoolManager manager = new(options);

        TestPoolable first = manager.Get<TestPoolable>();
        first.Value = 9;
        manager.Return(first);
        TestPoolable second = manager.Get<TestPoolable>();
        int preallocated = manager.Prealloc<TestPoolable>(2);
        bool setCapacity = manager.SetMaxCapacity<TestPoolable>(4);
        Dictionary<string, object> typeInfo = manager.GetTypeInfo<TestPoolable>();
        string report = manager.GenerateReport();

        Assert.Equal(0, second.Value);
        Assert.True(manager.PoolCount >= 1);
        Assert.True(manager.PeakPoolCount >= 1);
        Assert.True(manager.TotalGetOperations >= 2);
        Assert.True(manager.TotalReturnOperations >= 1);
        Assert.True(manager.TotalCacheHits >= 1);
        Assert.True(manager.TotalCacheMisses >= 0);
        Assert.True(manager.CacheHitRate >= 0);
        Assert.True(manager.Uptime >= TimeSpan.Zero);
        Assert.True(preallocated >= 0);
        Assert.True(setCapacity);
        Assert.Equal(nameof(TestPoolable), typeInfo["TypeName"]);
        Assert.Contains("ObjectPoolManager Status", report);
    }

    [Fact]
    public void Get_WhenDefaultPreallocateIsSet_WarmsNewTypePool()
    {
        ObjectPoolManager manager = new(new ObjectPoolOptions
        {
            EnableObjectTrimming = false,
            DefaultPreallocate = 3
        });

        HealthCheckPoolable item = manager.Get<HealthCheckPoolable>();
        Dictionary<string, object> afterGet = manager.GetTypeInfo<HealthCheckPoolable>();

        Assert.Equal(2, afterGet["AvailableCount"]);
        Assert.Equal(1L, manager.TotalGetOperations);
        Assert.Equal(1L, manager.TotalCacheHits);

        manager.Return(item);
    }

    [Fact]
    public void GetAndReturn_WhenRoundTripped_ResetsInstanceAndCountsOperations()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            DefaultPreallocate = 0,
            EnableMetrics = true
        };

        using ObjectPoolManager manager = new(options);

        TestPoolable first = manager.Get<TestPoolable>();
        first.Value = 99;
        manager.Return(first);

        TestPoolable second = manager.Get<TestPoolable>();

        Assert.Equal(0, second.Value);
        Assert.Equal(2L, manager.TotalGetOperations);
        Assert.Equal(1L, manager.TotalReturnOperations);
        Assert.True(manager.TotalCacheHits >= 1);
    }

    [Fact]
    public void GetAndReturn_FromManyThreads_CountsEveryOperationAndResetMetricsClearsThem()
    {
        using ObjectPoolManager manager = new(new ObjectPoolOptions { EnableObjectTrimming = false, DefaultPreallocate = 0 });
        const int Threads = 8, PerThread = 5_000;

        _ = System.Threading.Tasks.Parallel.For(0, Threads, new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = Threads }, _ =>
        {
            for (int i = 0; i < PerThread; i++)
            {
                manager.Return(manager.Get<TestPoolable>());
            }
        });

        // The counters are spread over one slot per processor; their sums must still be exact.
        Assert.Equal(Threads * PerThread, manager.TotalGetOperations);
        Assert.Equal(Threads * PerThread, manager.TotalReturnOperations);
        Assert.Equal(manager.TotalGetOperations, manager.TotalCacheHits + manager.TotalCacheMisses);
        Assert.Equal((long)(Threads * PerThread), manager.GetTypeInfo<TestPoolable>()["TotalGets"]);

        manager.ResetMetrics();
        Assert.Equal(0, manager.TotalGetOperations);
        Assert.Equal(0, manager.TotalReturnOperations);
    }

    [Fact]
    public void ResetStatistics_RestartsTheManagerTotalsButKeepsThePerTypeCounts()
    {
        using ObjectPoolManager manager = new(new ObjectPoolOptions { EnableObjectTrimming = false, DefaultPreallocate = 0 });
        for (int i = 0; i < 10; i++)
        {
            manager.Return(manager.Get<TestPoolable>());
        }

        manager.ResetStatistics();
        Assert.Equal(0, manager.TotalGetOperations);
        Assert.Equal(0, manager.TotalReturnOperations);
        Assert.Equal(0, manager.TotalCacheHits + manager.TotalCacheMisses);
        Assert.Equal(10L, manager.GetTypeInfo<TestPoolable>()["TotalGets"]); // Per-type counts are not reset.

        // The totals count again from there.
        for (int i = 0; i < 3; i++)
        {
            manager.Return(manager.Get<TestPoolable>());
        }

        Assert.Equal(3, manager.TotalGetOperations);
        Assert.Equal(3, manager.TotalReturnOperations);
        Assert.Equal(3, manager.TotalCacheHits);
    }

    [Fact]
    public void GetAndReturn_WithMultipleTypes_KeepsPoolsIndependent()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        TestPoolable testObj = manager.Get<TestPoolable>();
        HealthCheckPoolable hcObj = manager.Get<HealthCheckPoolable>();

        Assert.NotSame((object)testObj, (object)hcObj);

        manager.Return(testObj);
        manager.Return(hcObj);

        TestPoolable testObj2 = manager.Get<TestPoolable>();
        HealthCheckPoolable hcObj2 = manager.Get<HealthCheckPoolable>();

        Assert.Equal(0, testObj2.Value);
        Assert.Equal(4L, manager.TotalGetOperations);
        Assert.Equal(2L, manager.TotalReturnOperations);
    }

    [Fact]
    public void Get_OnFirstCall_InitializesMetrics()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        _ = manager.Get<TestPoolable>();

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();

        Assert.Equal(nameof(TestPoolable), info["TypeName"]);
        Assert.True((long)info["TotalGets"] >= 1);
        Assert.True((long)info["CacheMisses"] >= 0);
        Assert.True((double)info["CacheHitRate"] >= 0.0);
        Assert.True((long)info["Outstanding"] >= 1);
    }

    [Fact]
    public void Return_OnFirstCall_InitializesMetrics()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        TestPoolable obj = manager.Get<TestPoolable>();
        manager.Return(obj);

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();

        Assert.True((long)info["TotalReturns"] >= 1);
        Assert.True((long)info["Outstanding"] >= 0);
    }

    [Fact]
    public void Get_WhenObjectsOutstanding_TracksPeakOutstanding()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        var a = manager.Get<TestPoolable>();
        var b = manager.Get<TestPoolable>();
        var c = manager.Get<TestPoolable>();

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();
        Assert.Equal(3L, (long)info["PeakOutstanding"]);

        manager.Return(a);
        manager.Return(b);

        info = manager.GetTypeInfo<TestPoolable>();
        Assert.Equal(3L, (long)info["PeakOutstanding"]);
        Assert.Equal(1L, (long)info["Outstanding"]);
    }

    [Fact]
    public void GetAndReturn_WhenCalledConcurrently_KeepsCountsConsistent()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);
        const int threadCount = 8;
        const int opsPerThread = 500;

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        var warmup = new List<TestPoolable>();
        for (int i = 0; i < 32; i++)
        {
            warmup.Add(manager.Get<TestPoolable>());
        }
        foreach (var o in warmup)
        {
            manager.Return(o);
        }

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var rented = new List<TestPoolable>();

                for (int i = 0; i < opsPerThread; i++)
                {
                    if (i % 3 == 0 && rented.Count > 0)
                    {
                        manager.Return(rented[0]);
                        rented.RemoveAt(0);
                    }
                    else
                    {
                        TestPoolable obj = manager.Get<TestPoolable>();
                        rented.Add(obj);
                    }
                }

                foreach (var o in rented)
                {
                    manager.Return(o);
                }
            });
        }

        Task.WaitAll(tasks);

        long totalGets = manager.TotalGetOperations;
        long totalReturns = manager.TotalReturnOperations;

        Assert.True(totalGets > 0, "Expected some Get operations.");
        Assert.True(totalReturns > 0, "Expected some Return operations.");
        Assert.True(totalReturns <= totalGets, "Returns should not exceed Gets.");
    }

    [Fact]
    public void ResetMetrics_AfterOperations_ClearsAllCounters()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        var obj = manager.Get<TestPoolable>();
        manager.Return(obj);

        Assert.True(manager.TotalGetOperations > 0);
        Assert.True(manager.TotalReturnOperations > 0);

        manager.ResetMetrics();

        Assert.Equal(0L, manager.TotalGetOperations);
        Assert.Equal(0L, manager.TotalReturnOperations);
    }

    [Fact]
    public void ClearPool_WhenObjectsPooled_RemovesObjects()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            EnableMetrics = true,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        var objs = new List<TestPoolable>();
        for (int i = 0; i < 5; i++)
        {
            objs.Add(manager.Get<TestPoolable>());
        }
        foreach (var o in objs)
        {
            manager.Return(o);
        }

        int cleared = manager.ClearPool<TestPoolable>();

        Assert.True(cleared >= 1, $"Expected at least 1 cleared, got {cleared}");
    }

    [Fact]
    public void GetTypeInfo_WhenTypeIsUnknown_ReturnsDefaults()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            DefaultPreallocate = 0
        };

        using ObjectPoolManager manager = new(options);

        Dictionary<string, object> info = manager.GetTypeInfo<HealthCheckPoolable>();

        Assert.Equal(nameof(HealthCheckPoolable), info["TypeName"]);
        Assert.Equal(0, info["AvailableCount"]);
        Assert.Equal(false, info["IsActive"]);
    }

    [Fact]
    public void Get_WithDefaultPreallocateOfFive_LeavesFourAvailable()
    {
        ObjectPoolOptions options = new()
        {
            EnableObjectTrimming = false,
            DefaultPreallocate = 5,
            EnableMetrics = true
        };

        using ObjectPoolManager manager = new(options);

        _ = manager.Get<TestPoolable>();

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();

        Assert.Equal(4, info["AvailableCount"]);
    }

    [Fact]
    public void SetMaxCapacity_WhenPoolNotCreatedAndCapacityIsZero_HonoursZero()
    {
        ObjectPoolManager manager = new(new ObjectPoolOptions { DefaultMaxPoolSize = 1024 });

        bool set = manager.SetMaxCapacity<TestPoolable>(0);
        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();

        Assert.True(set);
        Assert.Equal(0, Convert.ToInt32(info["MaxCapacity"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Return_WhenLifetimeReservoirIndexWrapsNegative_DoesNotThrow()
    {
        ObjectPoolManager manager = new(new ObjectPoolOptions { EnableDiagnostics = true });
        TestPoolable first = manager.Get<TestPoolable>();

        // Simulate ~2^31 diagnostic returns: the next Interlocked.Increment lands in the negative range.
        object metricsDict = typeof(ObjectPoolManager)
            .GetField("_metricsDict", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(manager)!;
        object metrics = ((IDictionary)metricsDict)[typeof(TestPoolable)]!;
        metrics.GetType().GetField("ReservoirIndex")!.SetValue(metrics, int.MinValue);

        Exception? ex = Record.Exception(() => manager.Return(first));

        Assert.Null(ex);
    }
    /// <summary>
    /// Per-type metrics no longer depend on EnableMetrics: the outstanding count, the peak and the last activity are
    /// reported with it off (the default), without Get or Return writing a counter shared by every thread.
    /// </summary>
    [Fact]
    public void GetTypeInfo_WithMetricsOff_ReportsOutstandingPeakAndLastActivity()
    {
        using ObjectPoolManager manager = new(new ObjectPoolOptions
        {
            EnableObjectTrimming = false,
            EnableMetrics = false,
            DefaultPreallocate = 0,
        });

        Assert.Equal(DateTime.MinValue, (DateTime)manager.GetTypeInfo<TestPoolable>()["LastAccessUtc"]);

        TestPoolable a = manager.Get<TestPoolable>();
        TestPoolable b = manager.Get<TestPoolable>();
        TestPoolable c = manager.Get<TestPoolable>();
        manager.Return(a);

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();
        Assert.Equal(2L, (long)info["Outstanding"]);
        Assert.Equal(3L, (long)info["PeakOutstanding"]);
        Assert.Equal("Return", (string)info["LastAccessType"]);
        Assert.NotEqual(DateTime.MinValue, (DateTime)info["LastAccessUtc"]);

        manager.Return(b);
        manager.Return(c);
        info = manager.GetTypeInfo<TestPoolable>();
        Assert.Equal(0L, (long)info["Outstanding"]);
        Assert.Equal(3L, (long)info["PeakOutstanding"]);
    }

    /// <summary>
    /// The peak is sampled when the pool misses, so a working set that came and went between two reads is not lost.
    /// </summary>
    [Fact]
    public void PeakOutstanding_WhenWorkingSetIsReturnedBeforeAnyRead_IsStillReported()
    {
        using ObjectPoolManager manager = new(new ObjectPoolOptions { EnableObjectTrimming = false, DefaultPreallocate = 0 });

        List<TestPoolable> rented = [];
        for (int i = 0; i < 10; i++)
        {
            rented.Add(manager.Get<TestPoolable>());
        }

        foreach (TestPoolable item in rented)
        {
            manager.Return(item);
        }

        Dictionary<string, object> info = manager.GetTypeInfo<TestPoolable>();
        Assert.Equal(10L, (long)info["PeakOutstanding"]);
        Assert.Equal(0L, (long)info["Outstanding"]);
    }

    /// <summary>A read with no Get or Return since the previous one does not move the last activity.</summary>
    [Fact]
    public void LastAccessUtc_WithoutActivitySinceLastRead_DoesNotMove()
    {
        using ObjectPoolManager manager = new(new ObjectPoolOptions { EnableObjectTrimming = false, DefaultPreallocate = 0 });
        manager.Return(manager.Get<TestPoolable>());

        DateTime first = (DateTime)manager.GetTypeInfo<TestPoolable>()["LastAccessUtc"];
        System.Threading.Thread.Sleep(30);
        DateTime second = (DateTime)manager.GetTypeInfo<TestPoolable>()["LastAccessUtc"];

        // LastAccessUtc is rebuilt from a tick count on every read, so allow a few milliseconds of rounding.
        Assert.InRange((second - first).TotalMilliseconds, -10, 10);
    }
}
