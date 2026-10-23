// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Memory.Pools;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Objects;

public sealed class TypedObjectPoolTests
{
    [Fact]
    public void Get_WithTypedOperations_ReturnsExpectedResults()
    {
        ObjectPool pool = new(defaultMaxItemsPerType: 4);
        TypedObjectPool<TestPoolable> typedPool = pool.CreateTypedPool<TestPoolable>();

        int preallocated = typedPool.Prealloc(2);
        TestPoolable item = typedPool.Get();
        item.Value = 77;
        typedPool.Return(item);
        List<TestPoolable> rented = typedPool.GetMultiple(2);
        int returned = typedPool.ReturnMultiple(rented);
        typedPool.SetMaxCapacity(3);
        Dictionary<string, object> info = typedPool.GetInfo();
        int cleared = typedPool.Clear();

        Assert.True(preallocated >= 0);
        Assert.Equal(2, rented.Count);
        Assert.Equal(2, returned);
        Assert.Equal(nameof(TestPoolable), info["TypeName"]);
        Assert.True(cleared >= 0);
    }

    [Fact]
    public void Return_WhenArgumentsAreNull_ThrowsArgumentNullException()
    {
        ObjectPoolManager manager = new();
        TypedObjectPool<TestPoolable> adapter = manager.GetTypedPool<TestPoolable>();

        _ = Assert.Throws<ArgumentNullException>(() => adapter.Return(null!));
        _ = Assert.Throws<ArgumentNullException>(() => adapter.ReturnMultiple(null!));
    }
}
