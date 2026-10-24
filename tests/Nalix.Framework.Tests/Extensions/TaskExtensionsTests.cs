// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Extensions;
using Nalix.Environment.Extensions;
using Nalix.Framework.Extensions;

namespace Nalix.Framework.Tests.Extensions;

public sealed class TaskExtensionsTests
{
    [Fact]
    public void Await_WhenTaskIsNull_ThrowsArgumentNullException()
    {
        Task? task = null;

        Assert.Throws<ArgumentNullException>(() => task!.Await());
    }

    [Fact]
    public void Await_WhenTaskFaults_RethrowsOriginalException()
    {
        InvalidOperationException expected = new("boom");
        Task faulted = Task.FromException(expected);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => faulted.Await());
        Assert.Same(expected, actual);
    }

    [Fact]
    public void Await_WhenTaskHasResult_ReturnsValue()
    {
        Task<int> task = Task.FromResult(123);

        int value = task.Await();

        Assert.Equal(123, value);
    }

    [Fact]
    public void Await_WithContinueOnCapturedContextFalse_Completes()
    {
        Task task = Task.CompletedTask;

        task.Await(continueOnCapturedContext: false);
    }

    [Fact]
    public void Await_WhenValueTaskHasNoResult_Completes()
    {
        ValueTask task = ValueTask.CompletedTask;

        task.Await();
    }

    [Fact]
    public void Await_WhenValueTaskHasResult_ReturnsValue()
    {
        ValueTask<int> task = ValueTask.FromResult(42);

        int value = task.Await();

        Assert.Equal(42, value);
    }

    [Fact]
    public async Task WithTimeout_WhenTaskCompletesBeforeDeadline_ReturnsResult()
    {
        Task<int> task = Task.FromResult(9);

        int? result = await task.WithTimeout(100);

        Assert.Equal(9, result);
    }

    [Fact]
    public async Task WithTimeout_WhenValueTypeTaskTimesOut_ReturnsDefaultValue()
    {
        // Increase delay to 5s to ensure timeout (50ms) always wins even on slow runners
        Task<int> task = Task.Delay(5000).ContinueWith(_ => 7, TaskScheduler.Default);

        int? result = await task.WithTimeout(50);

        // For value types in unconstrained generics, default is 0, not null
        Assert.Equal(0, result);
    }

    [Fact]
    public async Task WithTimeout_WhenReferenceTypeTaskTimesOut_ReturnsNull()
    {
        // Increase delay to 5000ms to ensure 50ms timeout wins
        Task<string> task = Task.Delay(5000).ContinueWith(_ => "done", TaskScheduler.Default);

        string? result = await task.WithTimeout(50);

        Assert.Null(result);
    }

    [Fact]
    public async Task WithTimeout_WhenTaskIsNull_ThrowsArgumentNullException()
    {
        Task<int>? task = null;

        await Assert.ThrowsAsync<ArgumentNullException>(() => task!.WithTimeout(10));
    }

    [Fact]
    public async Task WithTimeout_WhenTimeoutIsBelowMinusOne_ThrowsArgumentOutOfRangeException()
    {
        Task<int> task = Task.FromResult(1);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => task.WithTimeout(-2));
    }

    [Fact]
    public void LinkCancellation_WhenTokenIsNotCancelable_DoesNotCancelTask()
    {
        TaskCompletionSource<int> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

        using IDisposable registration = tcs.LinkCancellation(CancellationToken.None);

        Assert.False(tcs.Task.IsCanceled);
    }

    [Fact]
    public void LinkCancellation_WhenTokenCancels_CancelsTask()
    {
        TaskCompletionSource<int> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cts = new();
        using IDisposable registration = tcs.LinkCancellation(cts.Token);

        cts.Cancel();

        Assert.True(tcs.Task.IsCanceled);
    }

    [Fact]
    public void LinkCancellation_WhenTcsIsNull_ThrowsArgumentNullException()
    {
        TaskCompletionSource<int>? tcs = null;

        Assert.Throws<ArgumentNullException>(() => tcs!.LinkCancellation(CancellationToken.None));
    }
}
