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
}
#endif
