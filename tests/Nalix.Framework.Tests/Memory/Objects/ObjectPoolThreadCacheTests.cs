// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Objects;

/// <summary>
/// With a thread-local cache (<see cref="ObjectPoolOptions.ThreadCacheDepth"/>), a Return feeds the central pool
/// first while it is empty, so other threads are not starved, and the thread's own slot after that.
/// </summary>
[Collection(TestCollections.TypePoolPhase1)]
public sealed class ObjectPoolThreadCacheTests
{
    [Fact]
    public void Return_WithAThreadCache_FillsTheCentralPoolFirstThenTheThreadSlot()
    {
        _ = PoolThreadLocalCacheProbe.Pop<TestPoolable>();
        using ObjectPoolManager manager = new(new ObjectPoolOptions { EnableObjectTrimming = false, DefaultPreallocate = 0, ThreadCacheDepth = 1 });

        TestPoolable first = manager.Get<TestPoolable>();
        TestPoolable second = manager.Get<TestPoolable>();

        manager.Return(first);  // The central pool is empty: it gets the object.
        manager.Return(second); // Now the thread keeps one for itself.
        Assert.Equal(1, (int)manager.GetTypeInfo<TestPoolable>()["AvailableCount"]);

        // The thread's slot is used first, then the central pool.
        Assert.Same(second, manager.Get<TestPoolable>());
        Assert.Same(first, manager.Get<TestPoolable>());
    }
}
