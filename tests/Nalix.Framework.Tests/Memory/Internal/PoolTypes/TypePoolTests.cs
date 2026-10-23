// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Internal.PoolTypes;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Internal.PoolTypes;

[Collection(TestCollections.TypePoolPhase1)]
public sealed class TypePoolTests
{
    [Fact]
    public void TryPushAndTryPop_WhenObjectPushed_ReturnsSameObject()
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
    public void TryPop_WhenPoolIsEmpty_ReturnsFalse()
    {
        TypePool pool = new(maxCapacity: 8);

        Assert.False(pool.TryPop(out IPoolable? obj));
        Assert.Null(obj);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void TryPushAndTryPop_WithMultipleObjects_RoundTrips()
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
    public void TryPush_WhenAtCapacity_ReturnsFalse()
    {
        TypePool pool = new(maxCapacity: 2);

        Assert.True(pool.TryPush(new TestPoolable()));
        Assert.True(pool.TryPush(new TestPoolable()));
        Assert.False(pool.TryPush(new TestPoolable()));
        Assert.Equal(2, pool.AvailableCount);
    }

    [Fact]
    public void TryPush_WhenOverflowing_DoesNotExceedCapacity()
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
    public void SetMaxCapacity_WhenCapacityReduced_TrimsAvailableObjects()
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
    public void Clear_WhenObjectsPooled_RemovesAllObjects()
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
    public void Clear_WhenPoolIsEmpty_ReturnsZero()
    {
        TypePool pool = new(maxCapacity: 8);

        int removed = pool.Clear();

        Assert.Equal(0, removed);
        Assert.Equal(0, pool.AvailableCount);
    }

    [Fact]
    public void Trim_WhenPercentageIsZero_RemovesNothing()
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
    public void Trim_WhenPercentageIs100_RemovesNothing()
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
    public void Trim_WhenPercentageIs50_RemovesHalf()
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
    public void Trim_WhenPercentageIsNegative_ClearsAll()
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
    public void Trim_WithDecayFactor_RemovesFractionalExcess()
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
    public void ToArray_WhenObjectsPooled_ReturnsCurrentObjects()
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
    public void ToArray_WhenPoolIsEmpty_ReturnsEmptyArray()
    {
        TypePool pool = new(maxCapacity: 8);

        IPoolable[] arr = pool.ToArray();

        Assert.Empty(arr);
    }

    [Fact]
    public void TryPushAndTryPop_WhenCalledConcurrently_DoesNotDuplicateObjects()
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
    public void AvailableCount_WhenPushedAndPoppedConcurrently_NeverGoesNegative()
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
    public void TryPush_WhenCalledConcurrently_FillsExactlyToCapacity()
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
    public void TryPop_WhenDrainedConcurrently_PopsEachObjectOnce()
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
    public void TryPushAndTryPop_WhenFilledToCapacity_DoesNotLoseObjects()
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
    public void TryPush_WhenPoolIsFull_DoesNotStoreRejectedObject()
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
    public void TryPop_WhenPoolDrained_ReturnsFalse()
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
    public void TryPushAndTryPop_WhenRepeated_StaysConsistent()
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
}
