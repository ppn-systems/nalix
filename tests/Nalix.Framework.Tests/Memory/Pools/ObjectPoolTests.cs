// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Nalix.Framework.Memory.Pools;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Pools;

[Collection(TestCollections.TypePoolPhase1)]
public sealed class ObjectPoolTests
{
    [Fact]
    public void Get_AfterRoundTrip_ResetsReturnedInstance()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 8);

        TestPoolable instance = pool.Get<TestPoolable>();
        instance.Value = 42;
        pool.Return(instance);
        TestPoolable reused = pool.Get<TestPoolable>();
        Dictionary<string, object> stats = pool.GetStatistics();

        Assert.Equal(0, reused.Value);
        Assert.True(pool.TotalCreatedCount >= 1);
        Assert.True(pool.TotalRentedCount >= 2);
        Assert.True(pool.TotalReturnedCount >= 1);
        Assert.True(pool.TotalAvailableCount >= 0);
        Assert.True(pool.TypeCount >= 1);
        Assert.True(pool.UptimeMs >= 0);
        Assert.Equal(pool.TypeCount, ((IReadOnlyCollection<Dictionary<string, object>>)pool.GetAllTypeInfo()).Count);
        Assert.Equal(pool.TotalCreatedCount, stats["TotalCreatedCount"]);
        Assert.NotNull(ObjectPool.Default);
    }

    [Fact]
    public void Return_WhenInstanceReturnedTwice_RejectsSecondReturn()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 8);

        TestPoolable instance = pool.Get<TestPoolable>();
        pool.Return(instance);
        pool.Return(instance);

        TestPoolable first = pool.Get<TestPoolable>();
        TestPoolable second = pool.Get<TestPoolable>();

        Assert.Same(instance, first);
        Assert.NotSame(first, second);
        Assert.Equal(1, pool.TotalRejectedReturnCount);
    }

    [Fact]
    public void SetMaxCapacity_WithBatchOperations_ReturnsExpectedResults()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 4);

        bool negativeCapacity = pool.SetMaxCapacity<TestPoolable>(-1);
        bool configuredCapacity = pool.SetMaxCapacity<TestPoolable>(2);
        int preallocated = pool.Prealloc<TestPoolable>(3);
        List<TestPoolable> rented = pool.GetMultiple<TestPoolable>(2);
        int returned = pool.ReturnMultiple(rented);
        int trimmed = pool.Trim(0);
        Dictionary<string, object> typeInfo = pool.GetTypeInfo<TestPoolable>();
        int clearedType = pool.ClearType<TestPoolable>();
        int clearedAll = pool.Clear();
        pool.ResetStatistics();

        Assert.False(negativeCapacity);
        Assert.True(configuredCapacity);
        Assert.True(preallocated >= 0);
        Assert.Equal(2, rented.Count);
        Assert.Equal(2, returned);
        Assert.True(trimmed >= 0);
        Assert.Equal(nameof(TestPoolable), typeInfo["TypeName"]);
        Assert.True(clearedType >= 0);
        Assert.True(clearedAll >= 0);
        Assert.Equal(0L, pool.TotalCreatedCount);
        Assert.Equal(0L, pool.TotalRentedCount);
        Assert.Equal(0L, pool.TotalReturnedCount);
    }

    [Fact]
    public void Get_WhenSecondPoolCreated_DoesNotReuseOtherPoolsInstance()
    {
        ObjectPool sourcePool = new(defaultMaxItemsPerType: 4);
        TestPoolable source = sourcePool.Get<TestPoolable>();
        source.Value = 123;
        sourcePool.Return(source);

        ObjectPool targetPool = new(defaultMaxItemsPerType: 4);
        TestPoolable target = targetPool.Get<TestPoolable>();

        Assert.NotSame(source, target);
        Assert.Equal(1, targetPool.TotalCreatedCount);
    }

    [Fact]
    public void GetMultiple_WhenCountIsInvalid_ThrowsArgumentException()
    {
        ObjectPool pool = new();

        static void Act(ObjectPool objectPool) => _ = objectPool.GetMultiple<TestPoolable>(0);

        ArgumentException exception = Assert.Throws<ArgumentException>(() => Act(pool));

        Assert.Equal("count", exception.ParamName);
    }

    [Fact]
    public void Return_ThenGet_ResetsInstanceAndCountsOperations()
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
    public void Prealloc_WhenCalled_FillsPoolWithRequestedCount()
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
    public void Clear_AfterPrealloc_RemovesAllObjects()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 16);

        pool.Prealloc<TestPoolable>(8);
        Assert.Equal(8, pool.TotalAvailableCount);

        int cleared = pool.Clear();
        Assert.Equal(8, cleared);
        Assert.Equal(0, pool.TotalAvailableCount);
    }
}
