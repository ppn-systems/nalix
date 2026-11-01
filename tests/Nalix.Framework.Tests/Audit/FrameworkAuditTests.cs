// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Linq;
using System.Reflection;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Buffers;
using Nalix.Framework.Memory.Pools;
using Nalix.Framework.Options;
using Xunit;

namespace Nalix.Framework.Tests.Audit;

/// <summary>
/// Reproduction tests for audit findings in Nalix.Framework.
/// Each test is EXPECTED TO FAIL on the current code and documents a defect.
/// See docs/audit/framework.md for the finding IDs.
/// </summary>
[Trait("Category", "Audit")]
public sealed class FrameworkAuditTests
{
    /// <summary>A plain poolable type that does NOT implement IPoolStateTracked.</summary>
    private sealed class PlainPoolable : IPoolable
    {
        public int Payload;

        public void ResetForPool() => this.Payload = 0;
    }

    /// <summary>
    /// FW-01: ObjectPool.Return accepts the same instance twice for IPoolable types that do not
    /// implement IPoolStateTracked, so two later renters receive the same live object.
    /// </summary>
    [Fact]
    public void FW01_ObjectPool_DoubleReturn_PlainPoolable_ShouldNotHandOutSameInstanceTwice()
    {
        ObjectPool pool = new(8);

        PlainPoolable a = pool.Get<PlainPoolable>();
        pool.Return(a);
        pool.Return(a); // caller bug: double return

        PlainPoolable b = pool.Get<PlainPoolable>();
        PlainPoolable c = pool.Get<PlainPoolable>();

        Assert.False(
            ReferenceEquals(b, c),
            "The same instance was handed to two independent renters after a double return.");
    }

    /// <summary>
    /// FW-02: BufferPoolManager.Return has no double-return guard when leak detection is disabled
    /// (the default). The same byte[] is pushed into ArrayPool twice and handed to two renters.
    /// </summary>
    [Fact]
    public void FW02_BufferPoolManager_DoubleReturn_ShouldNotHandOutSameBufferTwice()
    {
        BufferPoolManager manager = new(new BufferOptions());

        byte[] a = manager.Rent(4096);
        manager.Return(a);
        manager.Return(a); // caller bug: double return

        // Rent several buffers while holding them so every copy of `a` in the pool is observed.
        byte[][] rented = new byte[16][];
        for (int i = 0; i < rented.Length; i++)
        {
            rented[i] = manager.Rent(4096);
        }

        int distinct = rented.Distinct(ReferenceEqualityComparer.Instance).Count();
        Assert.Equal(rented.Length, distinct);
    }

    /// <summary>
    /// FW-03: TypePool allocates its slot array once at construction, so a later
    /// ObjectPool.SetMaxCapacity increase has no effect beyond the original capacity.
    /// </summary>
    [Fact]
    public void FW03_ObjectPool_SetMaxCapacity_Increase_ShouldRetainReturnedObjects()
    {
        ObjectPool pool = new(2);

        // Create the per-type pool first with the default capacity (2).
        _ = pool.Get<PlainPoolable>();

        Assert.True(pool.SetMaxCapacity<PlainPoolable>(8));

        PlainPoolable[] objs = new PlainPoolable[8];
        for (int i = 0; i < objs.Length; i++)
        {
            objs[i] = pool.Get<PlainPoolable>();
        }

        foreach (PlainPoolable o in objs)
        {
            pool.Return(o);
        }

        Assert.Equal(0, pool.TotalDroppedCount);
    }

    /// <summary>
    /// FW-04: Snowflake.NewId(type, machineId) silently masks machineId with 0x3FF.
    /// An out-of-range machine id (1024) is converted to 0 instead of being rejected, so it
    /// collides with machine 0 and with the 1023/1024 pair across nodes. WorkerOptions.MachineId
    /// has no range validation and is passed straight through this path.
    /// </summary>
    [Fact]
    public void FW04_Snowflake_NewId_MachineIdOutOfRange_ShouldBeRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Snowflake.NewId(SnowflakeType.System, 1024));
    }

    /// <summary>
    /// FW-05: With leak detection enabled, BufferPoolManager.Rent appends a WeakReference to
    /// _sentinelTracker for every rent. Entries are only pruned inside Report(), so the tracker
    /// grows with every rent even when every buffer has been returned.
    /// </summary>
    [Fact]
    public void FW05_BufferPoolManager_LeakDetection_ReturnedBuffers_ShouldNotAccumulateTrackerEntries()
    {
        BufferOptions options = new() { EnableBufferLeakDetection = true };
        BufferPoolManager manager = new(options);

        for (int i = 0; i < 5000; i++)
        {
            byte[] buf = manager.Rent(256);
            manager.Return(buf);
        }

        FieldInfo field = typeof(BufferPoolManager).GetField(
            "_sentinelTracker", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_sentinelTracker field not found.");

        object tracker = field.GetValue(manager)
            ?? throw new InvalidOperationException("_sentinelTracker is null.");

        int count = ((System.Collections.ICollection)tracker).Count;

        Assert.True(count <= 1000, $"Tracker retained {count} entries for 5000 fully returned buffers.");
    }
}
