// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Internal.Pooling;
using Xunit;

namespace Nalix.Network.Tests.Internal.Pooling;

public sealed class LocalPoolTests
{
    private sealed class Item : IPoolable
    {
        public int Owner;

        public void ResetForPool()
        {
        }
    }

    private sealed class Holder(ObjectPoolManager manager)
    {
        public LocalPool<Item> Pool = new(manager);
    }

    [Fact]
    [Trait("Category", "Stress")]
    public void Acquire_WhenRacingWithDestroyAndSlotArrayReuse_NeverReturnsAnotherPoolsItem()
    {
        ObjectPoolManager manager = ObjectPoolManager.Shared;
        Stopwatch clock = Stopwatch.StartNew();
        int foreign = 0;
        int iterations = 0;
        int acquirers = Math.Max(4, System.Environment.ProcessorCount * 2);

        while (clock.Elapsed < TimeSpan.FromSeconds(5) && Volatile.Read(ref foreign) == 0)
        {
            int tag = ++iterations;
            Holder first = new(manager);
            Holder second = new(manager);

            // Warm up so the slot array exists and both slots are idle.
            Item warm = first.Pool.Acquire(tag, static (item, owner) => item.Owner = owner)!;
            first.Pool.Return(warm);

            using ManualResetEventSlim go = new(false);
            int destroyed = 0;

            Task[] tasks = new Task[acquirers + 1];
            for (int t = 0; t < acquirers; t++)
            {
                tasks[t] = Task.Factory.StartNew(() =>
                {
                    go.Wait();
                    while (Volatile.Read(ref destroyed) == 0)
                    {
                        Item? item = first.Pool.Acquire(tag, static (i, owner) => i.Owner = owner);
                        if (item is null)
                        {
                            continue;
                        }

                        if (item.Owner != tag)
                        {
                            _ = Interlocked.Increment(ref foreign);
                        }

                        first.Pool.Return(item);
                    }
                }, TaskCreationOptions.LongRunning);
            }

            tasks[acquirers] = Task.Factory.StartNew(() =>
            {
                go.Wait();
                Thread.SpinWait(200);
                first.Pool.Destroy();

                // Another connection's pool re-rents the slot array that Destroy just gave back.
                Item? other = second.Pool.Acquire(-tag, static (item, owner) => item.Owner = owner);
                if (other is not null)
                {
                    second.Pool.Return(other);
                }

                Volatile.Write(ref destroyed, 1);
            }, TaskCreationOptions.LongRunning);

            go.Set();
            Task.WaitAll(tasks);
            second.Pool.Destroy();
        }

        foreign.Should().Be(0, "an acquirer that won the slot must never observe an item owned by another connection's pool");
    }
}
