// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tests.TestSupport;

namespace Nalix.Framework.Tests.Memory.Objects;

/// <summary>
/// The trim cycle with and without per-type metrics: an idle pool shrinks to its safety floor, a busy pool keeps
/// its objects, and a recent working set stays protected while an old one is shed. With metrics off (the default)
/// the cycle must still see the traffic, or a pool would keep whatever it once held.
/// </summary>
[Collection(TestCollections.TypePoolPhase1)]
public sealed class ObjectPoolTrimTests
{
    private const int DefaultCapacity = 2048;
    private const int Floor = DefaultCapacity / 12;
    private static readonly MethodInfo s_trim = typeof(ObjectPoolManager).GetMethod("TRIM_EXCESS_OBJECTS", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trim_WhenAPoolGoesIdle_ShrinksItToTheSafetyFloor(bool metrics)
    {
        using ObjectPoolManager manager = Create(metrics);
        Release(manager, Rent(manager, 1000));
        Trim(manager); // The cycle that saw the burst.
        Assert.Equal(1000, Available(manager));

        // No Get for many trim intervals: the recent peak decays and the pool shrinks to max(8, capacity / 12) =
        // 170 (trim keeps a whole percentage of the capacity: up to 1 % more).
        for (int i = 0; i < 16; i++)
        {
            Trim(manager);
        }

        Assert.InRange(Available(manager), Floor, Floor + (DefaultCapacity / 100) + 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trim_WhileThePoolIsBusy_KeepsItsObjects(bool metrics)
    {
        using ObjectPoolManager manager = Create(metrics);
        Release(manager, Rent(manager, 500));

        for (int i = 0; i < 12; i++)
        {
            // Every interval the same 500 objects go out and come back: a hot pool.
            Release(manager, Rent(manager, 500));
            Trim(manager);
        }

        Assert.Equal(500, Available(manager));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Trim_AfterAWorkingSetWasOut_KeepsMostOfItThenShedsItWhenIdle(bool metrics)
    {
        using ObjectPoolManager manager = Create(metrics);

        // 600 objects are rented when a trim runs (without metrics the peak is sampled there), then returned.
        List<TestPoolable> held = Rent(manager, 600);
        Trim(manager);
        Release(manager, held);

        // The next interval is idle: the floor is 1.5 x half the peak (450), so most of the set stays for a burst.
        Trim(manager);
        Assert.InRange(Available(manager), 450, 600);

        // Idle for long, the pool sheds it down to the floor.
        for (int i = 0; i < 16; i++)
        {
            Trim(manager);
        }

        Assert.InRange(Available(manager), Floor, Floor + (DefaultCapacity / 100) + 1);
    }

    private static ObjectPoolManager Create(bool metrics)
        => new(new ObjectPoolOptions { EnableObjectTrimming = false, EnableMetrics = metrics, DefaultPreallocate = 0 });

    private static List<TestPoolable> Rent(ObjectPoolManager manager, int count)
        => [.. Enumerable.Range(0, count).Select(_ => manager.Get<TestPoolable>())];

    private static void Release(ObjectPoolManager manager, List<TestPoolable> objects)
    {
        foreach (TestPoolable obj in objects)
        {
            manager.Return(obj);
        }
    }

    private static void Trim(ObjectPoolManager manager) => s_trim.Invoke(manager, null);

    private static int Available(ObjectPoolManager manager) => (int)manager.GetTypeInfo<TestPoolable>()["AvailableCount"];
}
