// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Framework.Tests.TestSupport;
using Xunit;

namespace Nalix.Framework.Tests.Tasks;

/// <summary>
/// Provides unit tests for the public API exposed by <see cref="TaskManager"/>.
/// </summary>
public sealed partial class TaskManagerTests : IDisposable
{
    private readonly TaskManagerTestHost _host = new();

    /// <inheritdoc />
    public void Dispose()
        => _host.Dispose();

    [Fact]
    public void Ctor_WithExplicitOptions_InitializesEmptyState()
    {
        using TaskManager manager = this.CreateManager();

        Assert.Equal("Workers: 0 running / 0 total | Recurring: 0", manager.Title);
        Assert.Equal(0, manager.WorkerErrorCount);
        Assert.Equal(0, manager.RecurringErrorCount);
        Assert.True(manager.AverageWorkerUptime >= 0);
        Assert.True(manager.AverageRecurringExecutionTime >= 0);
    }

    [Fact]
    public void Ctor_WhenParameterless_CreatesInstance()
    {
        using TaskManager manager = new();

        Assert.NotNull(manager);
    }

    [Fact]
    public async Task RunOnceAsync_WhenDelegateCompletes_ExecutesDelegate()
    {
        using TaskManager manager = this.CreateManager();
        bool executed = false;

        await manager.RunOnceAsync("run-once", ct =>
        {
            executed = true;
            return ValueTask.CompletedTask;
        });

        Assert.True(executed);
    }

    [Fact]
    public async Task RunOnceAsync_WhenDelegateThrows_RethrowsOriginalException()
    {
        using TaskManager manager = this.CreateManager();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await manager.RunOnceAsync("run-once-fail", _ => ValueTask.FromException(new InvalidOperationException("boom"))).ConfigureAwait(false));

        Assert.Equal("boom", exception.Message);
    }

    [Fact]
    public async Task RunOnceAsync_WhenNameIsNull_ThrowsArgumentNullException()
    {
        using TaskManager manager = this.CreateManager();

        _ = await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await manager.RunOnceAsync(null!, _ => ValueTask.CompletedTask).ConfigureAwait(false));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RunOnceAsync_WhenNameIsWhitespace_ThrowsArgumentException(string name)
    {
        using TaskManager manager = this.CreateManager();

        _ = await Assert.ThrowsAsync<ArgumentException>(
            async () => await manager.RunOnceAsync(name, _ => ValueTask.CompletedTask).ConfigureAwait(false));
    }

    [Fact]
    public void ScheduleWorker_WhenArgumentsAreInvalid_ThrowsExpectedException()
    {
        using TaskManager manager = this.CreateManager();

        _ = Assert.Throws<ArgumentException>(() => manager.ScheduleWorker("", "group", (_, _) => ValueTask.CompletedTask));
        _ = Assert.Throws<ArgumentNullException>(() => manager.ScheduleWorker("worker", "group", null!));
    }

    [Fact]
    public async Task ScheduleWorker_WhenWorkCompletes_UpdatesHandleAndLookups()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<IWorkerHandle> completion = TaskManagerTestHost.CreateCompletionSource<IWorkerHandle>();

        IWorkerHandle handle = manager.ScheduleWorker(
            "worker.complete",
            "group-a",
            (context, cancellationToken) =>
            {
                context.Advance(3, "step-1");
                context.Beat();
                return ValueTask.CompletedTask;
            },
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1),
                OnCompleted = worker => completion.TrySetResult(worker)
            });

        IWorkerHandle completedHandle = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Same(handle, completedHandle);

        await TaskManagerTestHost.WaitUntilAsync(
            () => !handle.IsRunning && handle.TotalRuns == 1,
            TimeSpan.FromSeconds(15));

        Assert.Equal("worker.complete", handle.Name);
        Assert.Equal("group-a", handle.Group);
        Assert.Equal(3, handle.Progress);
        Assert.Equal("step-1", handle.LastNote);
        Assert.True(handle.StartedUtc <= DateTimeOffset.UtcNow);
        _ = Assert.NotNull(handle.LastHeartbeatUtc);

        Assert.True(manager.TryGetWorker(handle.Id, out IWorkerHandle? foundHandle));
        Assert.Same(handle, foundHandle);

        IReadOnlyCollection<IWorkerHandle> allWorkers = manager.GetWorkers(runningOnly: false);
        Assert.Contains(handle, allWorkers);
    }

    [Fact]
    public async Task ScheduleWorker_WhenWorkThrows_IncrementsWorkerErrorCount()
    {
        using TaskManager manager = this.CreateManager();

        IWorkerHandle handle = manager.ScheduleWorker(
            "worker.fail",
            "group-a",
            (_, _) => ValueTask.FromException(new InvalidOperationException("worker failure")),
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1)
            });

        await TaskManagerTestHost.WaitUntilAsync(
            () => !handle.IsRunning && handle.TotalRuns == 1,
            TimeSpan.FromSeconds(15));

        Assert.Equal(1, manager.WorkerErrorCount);
    }

    [Fact]
    public async Task ScheduleWorker_WhenGroupConcurrencyLimitIsOne_RunsWorkersSequentially()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> firstStarted = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> releaseFirst = TaskManagerTestHost.CreateCompletionSource<bool>();
        int running = 0;
        int maxRunning = 0;

        ValueTask Work(IWorkerContext context, CancellationToken cancellationToken)
            => RunWorkAsync(cancellationToken);

        async ValueTask RunWorkAsync(CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref running);
            maxRunning = Math.Max(maxRunning, current);
            _ = firstStarted.TrySetResult(true);

            try
            {
                _ = await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            finally
            {
                _ = Interlocked.Decrement(ref running);
            }
        }

        IWorkerHandle first = manager.ScheduleWorker(
            "worker.seq.1",
            "group-seq",
            Work,
            new WorkerOptions { GroupConcurrencyLimit = 1, RetainFor = TimeSpan.FromMinutes(1) });

        _ = await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        IWorkerHandle second = manager.ScheduleWorker(
            "worker.seq.2",
            "group-seq",
            Work,
            new WorkerOptions { GroupConcurrencyLimit = 1, RetainFor = TimeSpan.FromMinutes(1) });

        await Task.Delay(100).ConfigureAwait(true);
        Assert.True(first.IsRunning);
        Assert.False(second.IsRunning);

        _ = releaseFirst.TrySetResult(true);

        await TaskManagerTestHost.WaitUntilAsync(() => !first.IsRunning && !second.IsRunning && second.TotalRuns == 1, TimeSpan.FromSeconds(15));
        Assert.Equal(1, maxRunning);
    }

    [Fact]
    public async Task ScheduleWorker_WhenGroupLimitConflicts_DoesNotLeakGlobalPermits()
    {
        using TaskManager manager = this.CreateManager(new TaskManagerOptions
        {
            CleanupInterval = TimeSpan.FromSeconds(5),
            DynamicAdjustmentEnabled = false,
            MaxWorkers = 3
        });
        TaskCompletionSource<bool> release = TaskManagerTestHost.CreateCompletionSource<bool>();

        _ = manager.ScheduleWorker(
            "worker.conflict.first",
            "g",
            async (ctx, ct) => { try { _ = await release.Task.WaitAsync(ct).ConfigureAwait(false); } catch (OperationCanceledException) { } },
            new WorkerOptions { GroupConcurrencyLimit = 2 });

        for (int i = 0; i < 2; i++)
        {
            _ = Assert.Throws<InvalidOperationException>(() => manager.ScheduleWorker(
                "worker.conflict.bad",
                "g",
                (_, _) => ValueTask.CompletedTask,
                new WorkerOptions { GroupConcurrencyLimit = 3 }));
        }

        TaskCompletionSource<bool> ran = TaskManagerTestHost.CreateCompletionSource<bool>();
        _ = manager.ScheduleWorker("worker.conflict.plain", "plain", (_, _) =>
        {
            _ = ran.TrySetResult(true);
            return ValueTask.CompletedTask;
        });

        _ = await ran.Task.WaitAsync(TimeSpan.FromSeconds(5));
        _ = release.TrySetResult(true);
    }

    [Fact]
    public async Task ScheduleWorker_WhenHigherPriorityIsQueued_StartsBeforeLowerPriority()
    {
        using TaskManager manager = this.CreateManager(new TaskManagerOptions
        {
            CleanupInterval = TimeSpan.FromSeconds(5),
            DynamicAdjustmentEnabled = false,
            MaxWorkers = 1,
            IsEnableLatency = true
        });

        TaskCompletionSource<bool> blockerStarted = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> releaseBlocker = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> highStarted = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> lowStarted = TaskManagerTestHost.CreateCompletionSource<bool>();
        List<string> executionOrder = [];

        _ = manager.ScheduleWorker(
            "worker.blocker",
            "group-priority",
            async (context, cancellationToken) =>
            {
                _ = blockerStarted.TrySetResult(true);
                _ = await releaseBlocker.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
            },
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        _ = await blockerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        DateTime enqueueStartedUtc = DateTime.UtcNow;

        IWorkerHandle low = manager.ScheduleWorker(
            "worker.low",
            "group-priority",
            (context, cancellationToken) =>
            {
                executionOrder.Add("low");
                _ = lowStarted.TrySetResult(true);
                return ValueTask.CompletedTask;
            },
            new WorkerOptions
            {
                Priority = WorkerPriority.LOW,
                RetainFor = TimeSpan.FromMinutes(1)
            });

        IWorkerHandle high = manager.ScheduleWorker(
            "worker.high",
            "group-priority",
            (context, cancellationToken) =>
            {
                executionOrder.Add("high");
                _ = highStarted.TrySetResult(true);
                return ValueTask.CompletedTask;
            },
            new WorkerOptions
            {
                Priority = WorkerPriority.HIGH,
                RetainFor = TimeSpan.FromMinutes(1)
            });

        Assert.True(DateTime.UtcNow - enqueueStartedUtc < TimeSpan.FromMilliseconds(250));
        Assert.False(low.IsRunning);
        Assert.False(high.IsRunning);

        _ = releaseBlocker.TrySetResult(true);

        _ = await highStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        _ = await lowStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await TaskManagerTestHost.WaitUntilAsync(() => low.TotalRuns == 1 && high.TotalRuns == 1, TimeSpan.FromSeconds(15));

        Assert.Equal(["high", "low"], executionOrder);
    }

    [Fact]
    public async Task ScheduleWorker_WhenCompletionCallbackThrows_IncrementsWorkerErrorCount()
    {
        using TaskManager manager = this.CreateManager();

        IWorkerHandle handle = manager.ScheduleWorker(
            "worker.callback.complete",
            "group-a",
            (_, _) => ValueTask.CompletedTask,
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1),
                OnCompleted = _ => throw new InvalidOperationException("callback failure")
            });

        await TaskManagerTestHost.WaitUntilAsync(
            () => !handle.IsRunning && handle.TotalRuns == 1 && manager.WorkerErrorCount == 1,
            TimeSpan.FromSeconds(15));

        Assert.Equal(1, manager.WorkerErrorCount);
    }

    [Fact]
    public async Task ScheduleWorker_WhenCompletionCallbackRuns_ObservesFinalWorkerState()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<(bool IsRunning, long TotalRuns, DateTimeOffset? LastHeartbeatUtc)> callbackState =
            TaskManagerTestHost.CreateCompletionSource<(bool, long, DateTimeOffset?)>();

        _ = manager.ScheduleWorker(
            "worker.callback.state",
            "group-a",
            (_, _) => ValueTask.CompletedTask,
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1),
                OnCompleted = worker => callbackState.TrySetResult((worker.IsRunning, worker.TotalRuns, worker.LastHeartbeatUtc))
            });

        (bool isRunning, long totalRuns, DateTimeOffset? lastHeartbeatUtc) =
            await callbackState.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.False(isRunning);
        Assert.Equal(1, totalRuns);
        _ = Assert.NotNull(lastHeartbeatUtc);
    }

    [Fact]
    public async Task ScheduleWorker_WhenWorkRuns_TracksAverageUptimeFromRuntime()
    {
        using TaskManager manager = this.CreateManager(new TaskManagerOptions
        {
            CleanupInterval = TimeSpan.FromSeconds(5),
            DynamicAdjustmentEnabled = false,
            IsEnableLatency = true,
            MaxWorkers = 1
        });

        TaskCompletionSource<IWorkerHandle> completion = TaskManagerTestHost.CreateCompletionSource<IWorkerHandle>();

        _ = manager.ScheduleWorker(
            "worker.latency",
            "group-latency",
            async (_, cancellationToken) =>
            {
                await Task.Delay(120, cancellationToken).ConfigureAwait(true);
            },
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1),
                OnCompleted = worker => completion.TrySetResult(worker)
            });

        _ = await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await TaskManagerTestHost.WaitUntilAsync(
            () => manager.GetWorkers(runningOnly: false).Count == 1 && manager.AverageWorkerUptime >= 100,
            TimeSpan.FromSeconds(15));

        Assert.InRange(manager.AverageWorkerUptime, 100, 3000);
    }

    [Fact]
    public void ScheduleWorker_WhenGroupConcurrencyLimitConflicts_ThrowsInvalidOperationException()
    {
        using TaskManager manager = this.CreateManager();

        _ = manager.ScheduleWorker(
            "worker.group.limit.1",
            "group-conflict",
            async (_, cancellationToken) =>
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(true);
            },
            new WorkerOptions
            {
                GroupConcurrencyLimit = 1,
                RetainFor = TimeSpan.FromMinutes(1)
            });

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            manager.ScheduleWorker(
                "worker.group.limit.2",
                "group-conflict",
                (_, _) => ValueTask.CompletedTask,
                new WorkerOptions
                {
                    GroupConcurrencyLimit = 2,
                    RetainFor = TimeSpan.FromMinutes(1)
                }));

        Assert.Contains("group-conflict", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScheduleWorker_WhenFailureCallbackThrows_IncrementsWorkerErrorCountAgain()
    {
        using TaskManager manager = this.CreateManager();

        IWorkerHandle handle = manager.ScheduleWorker(
            "worker.callback.fail",
            "group-a",
            (_, _) => ValueTask.FromException(new InvalidOperationException("worker failure")),
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1),
                OnFailed = (_, _) => throw new InvalidOperationException("failure callback")
            });

        await TaskManagerTestHost.WaitUntilAsync(
            () => !handle.IsRunning && handle.TotalRuns == 1 && manager.WorkerErrorCount == 2,
            TimeSpan.FromSeconds(15));

        Assert.Equal(2, manager.WorkerErrorCount);
    }

    [Fact]
    public async Task CancelWorker_WhenWorkerExists_CancelsWork()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> started = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> cancelled = TaskManagerTestHost.CreateCompletionSource<bool>();

        IWorkerHandle handle = manager.ScheduleWorker(
            "worker.cancel",
            "group-a",
            async (context, cancellationToken) =>
            {
                _ = started.TrySetResult(true);

                using CancellationTokenRegistration registration = cancellationToken.Register(() => cancelled.TrySetResult(true));

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            },
            new WorkerOptions
            {
                RetainFor = TimeSpan.FromMinutes(1)
            });

        _ = await started.Task.WaitAsync(TimeSpan.FromSeconds(15));

        manager.CancelWorker(handle.Id);

        _ = await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void CancelWorker_WhenWorkerDoesNotExist_DoesNotThrow()
    {
        using TaskManager manager = this.CreateManager();

        manager.CancelWorker(Nalix.Framework.Identifiers.Snowflake.NewId(SnowflakeType.Unknown));
    }

    [Fact]
    public async Task CancelGroup_WhenGroupThenAllCancelled_ReturnsExpectedCounts()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> groupAStarted = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> groupBStarted = TaskManagerTestHost.CreateCompletionSource<bool>();

        _ = manager.ScheduleWorker(
            "worker.a",
            "group-a",
            async (context, cancellationToken) =>
            {
                _ = groupAStarted.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            },
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        _ = manager.ScheduleWorker(
            "worker.b",
            "group-b",
            async (context, cancellationToken) =>
            {
                _ = groupBStarted.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            },
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        _ = await Task.WhenAll(
            groupAStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)),
            groupBStarted.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        int groupCancelled = manager.CancelGroup("group-a");
        int allCancelled = manager.CancelAllWorkers();

        Assert.Equal(1, groupCancelled);
        Assert.Equal(1, allCancelled);
    }

    [Fact]
    public async Task GetWorkers_WhenFilteredByGroupAndRunningState_ReturnsExpectedWorkers()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> runningWorkerStarted = TaskManagerTestHost.CreateCompletionSource<bool>();

        IWorkerHandle completedWorker = manager.ScheduleWorker(
            "worker.completed",
            "group-a",
            (_, _) => ValueTask.CompletedTask,
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        IWorkerHandle runningWorker = manager.ScheduleWorker(
            "worker.running",
            "group-b",
            async (context, cancellationToken) =>
            {
                _ = runningWorkerStarted.TrySetResult(true);
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
            },
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        _ = await runningWorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await TaskManagerTestHost.WaitUntilAsync(() => !completedWorker.IsRunning, TimeSpan.FromSeconds(15));

        IReadOnlyCollection<IWorkerHandle> runningOnly = manager.GetWorkers();
        IReadOnlyCollection<IWorkerHandle> allInGroupA = manager.GetWorkers(runningOnly: false, group: "group-a");

        Assert.Contains(runningWorker, runningOnly);
        Assert.DoesNotContain(completedWorker, runningOnly);
        _ = Assert.Single(allInGroupA);
        Assert.Contains(completedWorker, allInGroupA);
    }

    private TaskManager CreateManager(TaskManagerOptions? options = null) => _host.CreateManager(options);
}
