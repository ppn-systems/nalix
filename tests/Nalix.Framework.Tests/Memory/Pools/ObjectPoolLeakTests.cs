// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Memory.Pools;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Pools;

[Collection(ThrowingPoolableCollection.Name)]
public sealed class ObjectPoolLeakTests
{
    [Fact]
    public void GetMultiple_WhenCreationThrows_ReturnsAcquiredObjectsToPool()
    {
        // Clear any leftover thread-local cache for this test run
        _ = PoolThreadLocalCacheProbe.Pop<CreationThrowingPoolable>();

        var pool = new ObjectPool(10);

        // 1. Pre-fill the pool with 5 healthy objects
        CreationThrowingPoolable.ShouldThrowInNextAction = false;
        pool.Prealloc<CreationThrowingPoolable>(5);

        Assert.Equal(5, pool.TotalAvailableCount);

        // 2. Make the next creation throw
        CreationThrowingPoolable.ShouldThrowInNextAction = true;

        // 3. Try to get 10 objects (5 from pool, 6th will fail)
        var ex = Record.Exception(() => pool.GetMultiple<CreationThrowingPoolable>(10));
        Assert.NotNull(ex);

        // 4. Verify that the 5 objects taken from the pool were RETURNED
        Assert.Equal(5, pool.TotalAvailableCount);

        // Cleanup
        CreationThrowingPoolable.ShouldThrowInNextAction = false;
    }
}
