// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Runtime.Internal.Pooling;
using Xunit;

namespace Nalix.Runtime.Tests.Internal.Pooling;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class CancellableValueTaskSourceTests
{
    [Fact]
    public async Task Await_WhenCancelledThenInstanceRentedAgain_LateCompletionDoesNotLeakIntoNewAwaiter()
    {
        TaskCompletionSource<string> tcs1 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<string> tcs2 = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource cts1 = new();
        using CancellationTokenSource cts2 = new();

        ValueTask<string> first = CancellableValueTaskSource<string>.Await(new ValueTask<string>(tcs1.Task), cts1.Token);
        cts1.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);

        // Pool is LIFO, so this normally rents the instance the first awaiter just released.
        ValueTask<string> second = CancellableValueTaskSource<string>.Await(new ValueTask<string>(tcs2.Task), cts2.Token);

        // The abandoned handler finishes late.
        tcs1.SetResult("A");
        await Task.Delay(100);

        Assert.False(second.IsCompleted, "Late completion of an abandoned operation leaked into a new awaiter.");

        tcs2.SetResult("B");
        Assert.Equal("B", await second);
    }

    [Fact]
    public async Task Await_NonGenericWhenCancelledThenInstanceRentedAgain_LateCompletionDoesNotCompleteNewAwaiter()
    {
        TaskCompletionSource tcs1 = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource tcs2 = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using CancellationTokenSource cts1 = new();
        using CancellationTokenSource cts2 = new();

        ValueTask first = CancellableValueTaskSource.Await(new ValueTask(tcs1.Task), cts1.Token);
        cts1.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await first);

        ValueTask second = CancellableValueTaskSource.Await(new ValueTask(tcs2.Task), cts2.Token);

        tcs1.SetResult();
        await Task.Delay(100);

        Assert.False(second.IsCompleted, "Late completion of an abandoned operation completed a new awaiter.");

        tcs2.SetResult();
        await second;
    }

    [Fact]
    public async Task Await_WhenUnderlyingTaskThrowsFatalException_AwaiterIsCompletedWithThatException()
    {
        TaskCompletionSource<string> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new();

        ValueTask<string> pending = CancellableValueTaskSource<string>.Await(new ValueTask<string>(tcs.Task), cts.Token);
        tcs.SetException(new OutOfMemoryException("boom"));

        Task<string> consumer = pending.AsTask();
        Task winner = await Task.WhenAny(consumer, Task.Delay(2000));

        Assert.Same(consumer, winner);
        await Assert.ThrowsAsync<OutOfMemoryException>(async () => await consumer);
    }
}
