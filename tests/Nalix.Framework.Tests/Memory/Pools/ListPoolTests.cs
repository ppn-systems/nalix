// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Nalix.Framework.Memory.Pools;

namespace Nalix.Framework.Tests.Memory.Pools;

public sealed class ListPoolTests
{
    [Fact]
    public void Rent_AfterRentAndReturn_UpdatesStatistics()
    {
        ListPool<int> pool = new(maxPoolSize: 4, initialCapacity: 2);

        List<int> list = pool.Rent(3);
        list.AddRange([1, 2, 3]);
        pool.Return(list);
        Dictionary<string, object> stats = pool.GetStatistics();

        Assert.True(pool.AvailableCount >= 1);
        Assert.True(pool.CreatedCount >= 1);
        Assert.Equal(1L, pool.TotalRentOperations);
        Assert.Equal(1L, pool.TotalReturnOperations);
        Assert.Equal(0L, pool.RentedCount);
        Assert.True(pool.UptimeMs >= 0);
        Assert.Equal(pool.AvailableCount, stats["AvailableCount"]);
        Assert.NotNull(ListPool<int>.Instance);
    }

    [Fact]
    public void Prealloc_ThenTrimClearAndResetStatistics_ZeroesCounters()
    {
        ListPool<int> pool = new(maxPoolSize: 4, initialCapacity: 2);

        pool.Prealloc(3, 5);
        int trimmed = pool.Trim(1);
        int cleared = pool.Clear();
        pool.ResetStatistics();

        Assert.True(trimmed >= 0);
        Assert.True(cleared >= 0);
        Assert.Equal(0L, pool.CreatedCount);
        Assert.Equal(0L, pool.TotalRentOperations);
        Assert.Equal(0L, pool.TotalReturnOperations);
        Assert.Equal(0L, pool.TrimmedCount);
    }
}
