// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Runtime.Internal.Routing;
using Xunit;

namespace Nalix.Runtime.Tests;

#if DEBUG
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class WorkerWakeSignalTests
{
    [Fact]
    public void WaitAsync_WhenSetBeforeWait_CompletesSynchronously()
    {
        WorkerWakeSignal signal = new();

        Assert.False(signal.Set());
        Assert.True(signal.IsSet);

        ValueTask wait = signal.WaitAsync();

        Assert.True(wait.IsCompletedSuccessfully);
        Assert.False(signal.IsSet);
    }

    [Fact]
    public void Set_IsCoalesced_OnlyOnePendingWake()
    {
        WorkerWakeSignal signal = new();
        _ = signal.Set();
        _ = signal.Set();
        _ = signal.Set();

        Assert.True(signal.WaitAsync().IsCompletedSuccessfully);
        Assert.False(signal.WaitAsync().IsCompleted);
    }

    [Fact]
    public async Task WaitAsync_WhenNotSet_CompletesAfterSet()
    {
        WorkerWakeSignal signal = new();

        ValueTask wait = signal.WaitAsync();
        Assert.False(wait.IsCompleted);

        Assert.True(signal.Set());
        await wait.AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Clear_DropsPendingSignal()
    {
        WorkerWakeSignal signal = new();
        _ = signal.Set();

        signal.Clear();

        Assert.False(signal.IsSet);
        Assert.False(signal.WaitAsync().IsCompleted);
    }

    [Fact]
    public async Task WaitAsync_IsReusable_AcrossManyCycles_WithoutLosingWakes()
    {
        const int cycles = 20_000;
        WorkerWakeSignal signal = new();
        int consumed = 0;

        Task consumer = Task.Run(async () =>
        {
            for (int i = 0; i < cycles; i++)
            {
                await signal.WaitAsync();
                _ = Interlocked.Increment(ref consumed);
            }
        });

        // Producer: one Set per cycle, never ahead by more than one (the signal coalesces).
        for (int i = 0; i < cycles; i++)
        {
            SpinWait spin = default;
            while (Volatile.Read(ref consumed) < i)
            {
                spin.SpinOnce();
            }

            _ = signal.Set();
        }

        await consumer.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(cycles, Volatile.Read(ref consumed));
    }

    [Fact]
    public void SetAndSynchronousWait_DoNotAllocate()
    {
        WorkerWakeSignal signal = new();

        // Warm up (JIT).
        for (int i = 0; i < 100; i++)
        {
            _ = signal.Set();
            _ = signal.WaitAsync();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = signal.Set();
            ValueTask wait = signal.WaitAsync();
            wait.GetAwaiter().GetResult();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void PendingWait_ThenSet_DoesNotAllocate()
    {
        WorkerWakeSignal signal = new();

        for (int i = 0; i < 100; i++)
        {
            ValueTask w = signal.WaitAsync();
            _ = signal.Set();
            w.GetAwaiter().GetResult();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            ValueTask w = signal.WaitAsync();
            _ = signal.Set();
            w.GetAwaiter().GetResult();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
    /// <summary>
    /// Many concurrent setters racing one waiter that also calls Clear(): every wait must still
    /// complete once a later Set() arrives, and the core must never be reset under a pending
    /// SetResult (which would throw or hang).
    /// </summary>
    [Fact]
    public async Task ConcurrentSetClearWait_Stress_NeverHangsOrThrows()
    {
        WorkerWakeSignal signal = new();
        using CancellationTokenSource stop = new();
        const int iterations = 20_000;

        Task[] setters = new Task[3];
        for (int t = 0; t < setters.Length; t++)
        {
            setters[t] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    _ = signal.Set();
                    Thread.SpinWait(16);
                }
            });
        }

        Task waiter = Task.Run(async () =>
        {
            for (int i = 0; i < iterations; i++)
            {
                if ((i & 3) == 0)
                {
                    signal.Clear();
                }

                await signal.WaitAsync();
            }
        });

        Task done = await Task.WhenAny(waiter, Task.Delay(TimeSpan.FromSeconds(30)));
        stop.Cancel();
        await Task.WhenAll(setters);

        Assert.Same(waiter, done);
        await waiter;
    }

    /// <summary>
    /// Models the dispatch worker park handshake (publish parked -> re-check work -> wait, vs.
    /// publish work -> claim parked flag -> Set) under stress: no work item may be stranded while
    /// the consumer sleeps, and the consumer must actually park when there is no work (bounded
    /// number of loop turns, i.e. no busy spin).
    /// </summary>
    [Fact]
    public async Task ParkHandshake_Stress_NoLostWakeAndNoSpin()
    {
        WorkerWakeSignal signal = new();
        int parked = 0;
        long pending = 0;
        long consumed = 0;
        long loopTurns = 0;
        const int items = 50_000;
        using CancellationTokenSource stop = new();

        Task consumer = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                _ = Interlocked.Increment(ref loopTurns);
                if (Interlocked.Read(ref pending) > 0)
                {
                    _ = Interlocked.Decrement(ref pending);
                    _ = Interlocked.Increment(ref consumed);
                    continue;
                }

                _ = Interlocked.Exchange(ref parked, 1);
                if (Interlocked.Read(ref pending) > 0 || stop.IsCancellationRequested)
                {
                    if (Interlocked.Exchange(ref parked, 0) == 0)
                    {
                        signal.Clear();
                    }

                    continue;
                }

                await signal.WaitAsync();
                _ = Interlocked.Exchange(ref parked, 0);
            }
        });

        Task producer = Task.Run(() =>
        {
            for (int i = 0; i < items; i++)
            {
                _ = Interlocked.Increment(ref pending);
                if (Volatile.Read(ref parked) == 1 && Interlocked.CompareExchange(ref parked, 0, 1) == 1)
                {
                    _ = signal.Set();
                }

                if ((i & 63) == 0)
                {
                    Thread.Yield();
                }
            }
        });

        await producer;
        using (CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30)))
        {
            while (Interlocked.Read(ref consumed) < items)
            {
                await Task.Delay(1, timeout.Token);
            }
        }

        // Idle: the consumer must be parked, so loop turns stop growing.
        await Task.Delay(100);
        long turns0 = Interlocked.Read(ref loopTurns);
        await Task.Delay(300);
        long turns1 = Interlocked.Read(ref loopTurns);

        stop.Cancel();
        _ = signal.Set();
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(items, Interlocked.Read(ref consumed));
        Assert.True(turns1 - turns0 <= 1, $"consumer kept looping while idle ({turns1 - turns0} turns)");
    }
}
#endif
