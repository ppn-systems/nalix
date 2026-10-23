// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Internal.PoolTypes;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Memory.Pools;
using Nalix.Framework.Options;

namespace Nalix.Framework.Tests.Memory;

[Collection("TypePoolPhase1")]
public sealed class TypePoolPhase1Tests
{
    [Fact]
    public void TypePool_TryPush_And_TryPop_BasicRoundTrip()
    {
        TypePool pool = new(maxCapacity: 16);
        var obj = new TestPoolable { Value = 42 };

        Assert.True(pool.TryPush(obj));
        Assert.Equal(1, pool.AvailableCount);

        Assert.True(pool.TryPop(out IPoolable? popped));
        Assert.NotNull(popped);
        Assert.Same(obj, popped);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_TryPop_EmptyPool_ReturnsFalse()
    {
        TypePool pool = new(maxCapacity: 8);

        Assert.False(pool.TryPop(out IPoolable? obj));
        Assert.Null(obj);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_PushAndPop_MultipleObjects_RoundTrip()
    {
        TypePool pool = new(maxCapacity: 8);
        var obj1 = new TestPoolable { Value = 1 };
        var obj2 = new TestPoolable { Value = 2 };
        var obj3 = new TestPoolable { Value = 3 };

        Assert.True(pool.TryPush(obj1));
        Assert.True(pool.TryPush(obj2));
        Assert.True(pool.TryPush(obj3));
        Assert.Equal(3, pool.AvailableCount);

        Assert.True(pool.TryPop(out IPoolable? p1));
        Assert.True(pool.TryPop(out IPoolable? p2));
        Assert.True(pool.TryPop(out IPoolable? p3));

        Assert.NotNull(p1);
        Assert.NotNull(p2);
        Assert.NotNull(p3);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_TryPush_AtCapacity_ReturnsFalse()
    {
        TypePool pool = new(maxCapacity: 2);

        Assert.True(pool.TryPush(new TestPoolable()));
        Assert.True(pool.TryPush(new TestPoolable()));
        Assert.False(pool.TryPush(new TestPoolable()));
        Assert.Equal(2, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_TryPush_Overflow_DoesNotExceedCapacity()
    {
        TypePool pool = new(maxCapacity: 4);

        for (int i = 0; i < 4; i++)
        {
            Assert.True(pool.TryPush(new TestPoolable { Value = i }));
        }

        for (int i = 0; i < 10; i++)
        {
            Assert.False(pool.TryPush(new TestPoolable()));
        }

        Assert.Equal(4, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_SetMaxCapacity_ReduceCapacity_TriggersTrim()
    {
        TypePool pool = new(maxCapacity: 8);

        for (int i = 0; i < 8; i++)
        {
            pool.TryPush(new TestPoolable());
        }
        Assert.Equal(8, pool.AvailableCount);

        pool.SetMaxCapacity(4);
        Assert.Equal(4, pool.MaxCapacity);
        Assert.True(pool.AvailableCount <= 4, $"Expected AvailableCount <= 4 after capacity reduction, got {pool.AvailableCount}");
    }

    [Fact]
    public void TypePool_Clear_RemovesAllObjects()
    {
        TypePool pool = new(maxCapacity: 16);

        for (int i = 0; i < 10; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int removed = pool.Clear();

        Assert.Equal(10, removed);
        Assert.Equal(0, pool.AvailableCount);

        Assert.True(pool.TryPush(new TestPoolable()));
        Assert.Equal(1, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Clear_EmptyPool_ReturnsZero()
    {
        TypePool pool = new(maxCapacity: 8);

        int removed = pool.Clear();

        Assert.Equal(0, removed);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Trim_PercentageZero_ReturnsZeroNoTrim()
    {
        TypePool pool = new(maxCapacity: 8);
        for (int i = 0; i < 6; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int before = pool.AvailableCount;
        int removed = pool.Trim(percentage: 0);

        Assert.Equal(0, removed);
        Assert.Equal(before, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Trim_Percentage100_RemovesNothing()
    {
        TypePool pool = new(maxCapacity: 10);

        for (int i = 0; i < 10; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int removed = pool.Trim(percentage: 100);
        Assert.Equal(0, removed);
        Assert.Equal(10, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Trim_Percentage50_RemovesCorrectExcess()
    {
        TypePool pool = new(maxCapacity: 10);

        for (int i = 0; i < 10; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int removed = pool.Trim(percentage: 50);
        Assert.Equal(5, removed);
        Assert.Equal(5, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Trim_NegativePercentage_ClearsAll()
    {
        TypePool pool = new(maxCapacity: 8);

        for (int i = 0; i < 6; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int removed = pool.Trim(percentage: -1);
        Assert.Equal(6, removed);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_Trim_DecayFactor_RemovesFractionalExcess()
    {
        TypePool pool = new(maxCapacity: 100);

        for (int i = 0; i < 80; i++)
        {
            pool.TryPush(new TestPoolable());
        }

        int removed = pool.Trim(percentage: 50, decayFactor: 0.5);
        Assert.Equal(15, removed);
        Assert.Equal(65, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_ToArray_ReturnsCurrentObjects()
    {
        TypePool pool = new(maxCapacity: 8);

        var obj1 = new TestPoolable { Value = 10 };
        var obj2 = new TestPoolable { Value = 20 };
        pool.TryPush(obj1);
        pool.TryPush(obj2);

        IPoolable[] arr = pool.ToArray();

        Assert.Equal(2, arr.Length);
        Assert.Contains(arr, o => ReferenceEquals(o, obj1));
        Assert.Contains(arr, o => ReferenceEquals(o, obj2));
    }

    [Fact]
    public void TypePool_ToArray_EmptyPool_ReturnsEmptyArray()
    {
        TypePool pool = new(maxCapacity: 8);

        IPoolable[] arr = pool.ToArray();

        Assert.Empty(arr);
    }

    [Fact]
    public void TypePool_ConcurrentPushPop_NoDuplication()
    {
        const int capacity = 64;
        const int threadCount = 8;
        const int opsPerThread = 1000;

        TypePool pool = new(maxCapacity: capacity);
        var pushed = new ConcurrentBag<TestPoolable>();
        var popped = new ConcurrentBag<IPoolable>();

        for (int i = 0; i < capacity / 2; i++)
        {
            var obj = new TestPoolable { Value = i };
            pool.TryPush(obj);
            pushed.Add(obj);
        }

        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            int threadIdx = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                var localPushed = new List<TestPoolable>();
                var localPopped = new List<IPoolable>();

                for (int i = 0; i < opsPerThread; i++)
                {
                    if (i % 2 == 0)
                    {
                        var obj = new TestPoolable { Value = threadIdx * opsPerThread + i };
                        if (pool.TryPush(obj))
                        {
                            localPushed.Add(obj);
                        }
                    }
                    else
                    {
                        if (pool.TryPop(out IPoolable? obj))
                        {
                            localPopped.Add(obj!);
                        }
                    }
                }

                foreach (var p in localPushed)
                {
                    pushed.Add(p);
                }

                foreach (var p in localPopped)
                {
                    popped.Add(p);
                }
            });
        }

        Task.WaitAll(tasks);

        var pushedSet = new HashSet<TestPoolable>(pushed);
        foreach (IPoolable obj in popped)
        {
            Assert.True(pushedSet.Contains((TestPoolable)obj), "Popped object was never pushed or was duplicated.");
        }

        var poppedSet = new HashSet<IPoolable>(ReferenceEqualityComparer.Instance);
        foreach (IPoolable obj in popped)
        {
            Assert.True(poppedSet.Add(obj), "Object was popped more than once (duplication).");
        }
    }

    [Fact]
    public void TypePool_ConcurrentPushPop_AvailableCountStaysNonNegative()
    {
        const int capacity = 32;
        const int threadCount = 8;
        const int opsPerThread = 2000;

        TypePool pool = new(maxCapacity: capacity);
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();

                for (int i = 0; i < opsPerThread; i++)
                {
                    if (i % 2 == 0)
                    {
                        pool.TryPush(new TestPoolable());
                    }
                    else
                    {
                        pool.TryPop(out _);
                    }

                    int count = pool.AvailableCount;
                    Assert.True(count >= 0, $"AvailableCount went negative: {count}");
                }
            });
        }

        Task.WaitAll(tasks);
    }

    [Fact]
    public void TypePool_ConcurrentPush_FillsExactlyToCapacity()
    {
        const int capacity = 16;
        const int threadCount = 8;

        TypePool pool = new(maxCapacity: capacity);
        int successCount = 0;
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];
        int pushesPerThread = capacity / threadCount;

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < pushesPerThread; i++)
                {
                    if (pool.TryPush(new TestPoolable()))
                    {
                        Interlocked.Increment(ref successCount);
                    }
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.Equal(capacity, successCount);
        Assert.Equal(capacity, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_ConcurrentPopUntilEmpty_NoException()
    {
        const int capacity = 32;
        const int threadCount = 8;

        TypePool pool = new(maxCapacity: capacity);

        for (int i = 0; i < capacity; i++)
        {
            pool.TryPush(new TestPoolable { Value = i });
        }

        int popCount = 0;
        var barrier = new Barrier(threadCount);
        var tasks = new Task[threadCount];

        for (int t = 0; t < threadCount; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();

                while (pool.TryPop(out _))
                {
                    Interlocked.Increment(ref popCount);
                }
            });
        }

        Task.WaitAll(tasks);

        Assert.Equal(capacity, popCount);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_PushPop_PushedObjectsNotLost()
    {
        const int capacity = 16;
        TypePool pool = new(maxCapacity: capacity);
        var objects = new List<TestPoolable>();

        for (int i = 0; i < capacity; i++)
        {
            var obj = new TestPoolable { Value = i };
            objects.Add(obj);
            Assert.True(pool.TryPush(obj));
        }

        var popped = new List<IPoolable>();
        for (int i = 0; i < capacity; i++)
        {
            Assert.True(pool.TryPop(out IPoolable? obj));
            popped.Add(obj!);
        }

        var pushedSet = new HashSet<TestPoolable>(objects, ReferenceEqualityComparer.Instance);
        foreach (IPoolable p in popped)
        {
            Assert.True(pushedSet.Remove((TestPoolable)p), "Unexpected or duplicate popped object.");
        }
        Assert.Empty(pushedSet);
    }

    [Fact]
    public void TypePool_PushPop_PoolFull_RejectedObjectNotStored()
    {
        TypePool pool = new(maxCapacity: 2);

        var obj1 = new TestPoolable { Value = 1 };
        var obj2 = new TestPoolable { Value = 2 };
        var obj3 = new TestPoolable { Value = 3 };

        Assert.True(pool.TryPush(obj1));
        Assert.True(pool.TryPush(obj2));
        Assert.False(pool.TryPush(obj3));

        var popped = new HashSet<IPoolable>(ReferenceEqualityComparer.Instance);
        while (pool.TryPop(out IPoolable? o))
        {
            Assert.True(popped.Add(o!), "Duplication detected.");
        }

        Assert.Equal(2, popped.Count);
        Assert.Contains(obj1, popped);
        Assert.Contains(obj2, popped);
        Assert.DoesNotContain(obj3, popped);
    }

    [Fact]
    public void ObjectPoolManager_Get_Return_RoundTrip_PreservesSemantics()
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
    public void ObjectPoolManager_Get_Return_MultipleTypes_Independent()
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
    public void ObjectPoolManager_MetricsInitialized_OnFirstGet()
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
    public void ObjectPoolManager_MetricsInitialized_OnFirstReturn()
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
    public void ObjectPoolManager_PeakOutstanding_TrackedCorrectly()
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
    public void ObjectPoolManager_ConcurrentRentReturn_NoDuplication()
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
    public void ObjectPoolManager_ResetMetrics_ClearsAllCounters()
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
    public void ObjectPoolManager_ClearPool_RemovesObjects()
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
    public void ObjectPoolManager_GetTypeInfo_UnknownType_ReturnsDefaults()
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
    public void ObjectPoolManager_DefaultPreallocate_WarmsPool()
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
    public void TypePool_PoolFull_SubsequentPopsReturnFalse()
    {
        const int capacity = 4;
        TypePool pool = new(maxCapacity: capacity);

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(pool.TryPush(new TestPoolable()));
        }

        for (int i = 0; i < capacity; i++)
        {
            Assert.True(pool.TryPop(out _));
        }

        Assert.False(pool.TryPop(out _));
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TypePool_PushPopCycle_RepeatsConsistently()
    {
        TypePool pool = new(maxCapacity: 4);

        for (int cycle = 0; cycle < 100; cycle++)
        {
            var obj = new TestPoolable { Value = cycle };
            Assert.True(pool.TryPush(obj));
            Assert.True(pool.TryPop(out IPoolable? popped));
            Assert.Same(obj, popped);
            Assert.Equal(0, pool.AvailableCount);
        }
    }

    [Fact]
    public void ObjectPool_GetReturn_ViaTypePool_Works()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 8);

        TestPoolable first = pool.Get<TestPoolable>();
        first.Value = 77;
        pool.Return(first);

        TestPoolable second = pool.Get<TestPoolable>();

        Assert.Equal(0, second.Value);
        Assert.True(pool.TotalCreatedCount >= 1);
        Assert.True(pool.TotalRentedCount >= 2);
        Assert.True(pool.TotalReturnedCount >= 1);
    }

    [Fact]
    public void ObjectPool_Prealloc_ViaTypePool_Works()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 16);

        int preallocated = pool.Prealloc<TestPoolable>(8);

        Assert.Equal(8, preallocated);
        Assert.Equal(8, pool.TotalAvailableCount);

        for (int i = 0; i < 8; i++)
        {
            TestPoolable obj = pool.Get<TestPoolable>();
            Assert.Equal(0, obj.Value);
        }
    }

    [Fact]
    public void ObjectPool_Clear_ViaTypePool_Works()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 16);

        pool.Prealloc<TestPoolable>(8);
        Assert.Equal(8, pool.TotalAvailableCount);

        int cleared = pool.Clear();
        Assert.Equal(8, cleared);
        Assert.Equal(0, pool.TotalAvailableCount);
    }
}

