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

public sealed partial class TaskManagerTests
{
    [Fact]
    public void ScheduleRecurring_WhenArgumentsAreInvalid_ThrowsExpectedException()
    {
        using TaskManager manager = this.CreateManager();

        _ = Assert.Throws<ArgumentException>(() => manager.ScheduleRecurring("", TimeSpan.FromMilliseconds(10), _ => ValueTask.CompletedTask));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => manager.ScheduleRecurring("recurring", TimeSpan.Zero, _ => ValueTask.CompletedTask));
        _ = Assert.Throws<ArgumentNullException>(() => manager.ScheduleRecurring("recurring", TimeSpan.FromMilliseconds(10), null!));
    }

    [Fact]
    public void ScheduleRecurring_WhenNameAlreadyExists_ThrowsAndIncrementsErrorCount()
    {
        using TaskManager manager = this.CreateManager();

        _ = manager.ScheduleRecurring(
            "recurring.duplicate",
            TimeSpan.FromMilliseconds(50),
            _ => ValueTask.CompletedTask,
            new RecurringOptions
            {
                Jitter = TimeSpan.Zero,
                NonReentrant = false
            });

        _ = Assert.Throws<InternalErrorException>(() =>
            manager.ScheduleRecurring(
                "recurring.duplicate",
                TimeSpan.FromMilliseconds(50),
                _ => ValueTask.CompletedTask,
                new RecurringOptions
                {
                    Jitter = TimeSpan.Zero,
                    NonReentrant = false
                }));

        Assert.Equal(1, manager.RecurringErrorCount);
    }

    [Fact]
    public async Task ScheduleRecurring_WhenScheduled_RunsAndSupportsLookupAndCancel()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> executed = TaskManagerTestHost.CreateCompletionSource<bool>();

        IRecurringHandle handle = manager.ScheduleRecurring(
            "recurring.run",
            TimeSpan.FromMilliseconds(50),
            cancellationToken =>
            {
                _ = executed.TrySetResult(true);
                return ValueTask.CompletedTask;
            },
            new RecurringOptions
            {
                Jitter = TimeSpan.Zero,
                NonReentrant = false
            });

        _ = await executed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await TaskManagerTestHost.WaitUntilAsync(() => handle.TotalRuns > 0, TimeSpan.FromSeconds(15));

        Assert.True(manager.TryGetRecurring("recurring.run", out IRecurringHandle? foundHandle));
        Assert.Same(handle, foundHandle);
        Assert.Equal("recurring.run", handle.Name);
        Assert.True(handle.TotalRuns > 0);
        _ = Assert.NotNull(handle.LastRunUtc);
        _ = Assert.NotNull(handle.NextRunUtc);

        manager.CancelRecurring("recurring.run");
    }

    [Fact]
    public async Task ScheduleRecurring_WhenNonReentrant_DoesNotOverlapRuns()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> entered = TaskManagerTestHost.CreateCompletionSource<bool>();
        TaskCompletionSource<bool> release = TaskManagerTestHost.CreateCompletionSource<bool>();
        int running = 0;
        int maxRunning = 0;

        IRecurringHandle handle = manager.ScheduleRecurring(
            "recurring.nonreentrant",
            TimeSpan.FromMilliseconds(20),
            async cancellationToken =>
            {
                int current = Interlocked.Increment(ref running);
                maxRunning = Math.Max(maxRunning, current);
                _ = entered.TrySetResult(true);
                try
                {
                    _ = await release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    _ = Interlocked.Decrement(ref running);
                }
            },
            new RecurringOptions
            {
                Jitter = TimeSpan.Zero,
                NonReentrant = true
            });

        _ = await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(120).ConfigureAwait(true);
        _ = release.TrySetResult(true);
        await TaskManagerTestHost.WaitUntilAsync(() => handle.TotalRuns >= 1 && !handle.IsRunning, TimeSpan.FromSeconds(15));

        Assert.Equal(1, maxRunning);
        manager.CancelRecurring(handle.Name);
    }

    [Fact]
    public async Task GenerateReport_WhenWorkersAndRecurringTasksExist_ContainsSummaryAndNames()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<bool> recurringExecuted = TaskManagerTestHost.CreateCompletionSource<bool>();

        IWorkerHandle worker = manager.ScheduleWorker(
            "worker.report",
            "group-report",
            (_, _) => ValueTask.CompletedTask,
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        IRecurringHandle recurring = manager.ScheduleRecurring(
            "recurring.report",
            TimeSpan.FromMilliseconds(50),
            cancellationToken =>
            {
                _ = recurringExecuted.TrySetResult(true);
                return ValueTask.CompletedTask;
            },
            new RecurringOptions
            {
                Jitter = TimeSpan.Zero,
                NonReentrant = false
            });

        _ = await recurringExecuted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await TaskManagerTestHost.WaitUntilAsync(() => worker.TotalRuns == 1, TimeSpan.FromSeconds(15));

        string report = manager.GenerateReport();

        Assert.Contains("TaskManager:", report);
        Assert.Contains("recurring.report", report);
        Assert.Contains("Workers by Group:", report);
        Assert.Contains("Recurring:", report);
        Assert.Contains("group-report", report);
        Assert.Contains("worker.report", manager.GetWorkers(runningOnly: false).Select(static workerHandle => workerHandle.Name));
        Assert.True(manager.AverageWorkerUptime >= 0);
        Assert.True(manager.AverageRecurringExecutionTime >= 0);

        manager.CancelRecurring(recurring.Name);
    }

    [Fact]
    public void Dispose_WhenCalled_ClearsTrackedStateAndRejectsScheduling()
    {
        TaskManager manager = this.CreateManager();

        _ = manager.ScheduleRecurring(
            "recurring.dispose",
            TimeSpan.FromMilliseconds(50),
            _ => ValueTask.CompletedTask,
            new RecurringOptions
            {
                Jitter = TimeSpan.Zero,
                NonReentrant = false
            });

        _ = manager.ScheduleWorker(
            "worker.dispose",
            "group-dispose",
            (_, _) => ValueTask.CompletedTask,
            new WorkerOptions { RetainFor = TimeSpan.FromMinutes(1) });

        manager.Dispose();

        Assert.Empty(manager.GetRecurring());
        Assert.Empty(manager.GetWorkers(runningOnly: false));
        _ = Assert.Throws<ObjectDisposedException>(() => manager.ScheduleWorker("after-dispose", "group", (_, _) => ValueTask.CompletedTask));
        _ = Assert.Throws<ObjectDisposedException>(() => manager.ScheduleRecurring("after-dispose", TimeSpan.FromMilliseconds(10), _ => ValueTask.CompletedTask));
    }

    [Fact]
    public async Task ScheduleWorker_WhenStateOverloadUsed_ExecutesAndUpdatesMetrics()
    {
        using TaskManager manager = this.CreateManager();
        TaskCompletionSource<int> tcs = TaskManagerTestHost.CreateCompletionSource<int>();

        manager.ScheduleWorker(
            name: "test.background.work",
            work: static state =>
            {
                var (t, val) = state;
                t.TrySetResult(val);
            },
            state: (Tcs: tcs, Value: 42)
        );

        int result = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(42, result);

        // Verify that global metrics/Title were updated
        Assert.True(manager.Title.Contains("Workers:"));
    }

    [Fact]
    public async Task ScheduleWorker_WhenStartedDisabled_LogsAggregatedDispatcherMessage()
    {
        using TaskManager manager = this.CreateManager();
        List<KeyValuePair<string, object?>> events = [];

        using IDisposable subscription = DiagnosticsEvents.Source.Subscribe(
            new DelegateObserver<KeyValuePair<string, object?>>(ev =>
            {
                lock (events)
                {
                    events.Add(ev);
                }
            }),
            name => name == DiagnosticsEvents.Tasks.Dispatcher);

        for (int i = 0; i < 4; i++)
        {
            _ = manager.ScheduleWorker(
                $"worker.batch.{i}",
                "group-batch-test",
                (_, _) => ValueTask.CompletedTask,
                new WorkerOptions { Tag = "batch-test", RetainFor = TimeSpan.Zero });
        }

        // The aggregated message is written by a pool task that waits 100 ms before flushing, so wait for it
        // instead of assuming it is there after a fixed delay (a busy thread pool can delay it for much longer).
        await TaskManagerTestHost.WaitUntilAsync(() => AggregatedStartLogs(events).Sum(AggregatedStartCount) >= 4, TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        lock (events)
        {
            List<DiagnosticLog> batchLogs = AggregatedStartLogs(events);
            Assert.NotEmpty(batchLogs);
            Assert.Equal(4, batchLogs.Sum(AggregatedStartCount));
            Assert.True(batchLogs.Count < 4, "The four worker starts must be reported as an aggregate, not one message per worker.");

            DiagnosticLog log = batchLogs[0];
            Assert.Contains("group=group-batch-test", log.Message);
            Assert.Contains("priority=normal", log.Message);
            Assert.Contains("tag=batch-test", log.Message);

            var startedLogs = events.Where(ev => ev.Key == DiagnosticsEvents.Tasks.Started).ToList();
            Assert.Empty(startedLogs);
        }

        await TaskManagerTestHost.WaitUntilAsync(() => manager.GetWorkers(runningOnly: false, group: "group-batch-test").Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private static List<DiagnosticLog> AggregatedStartLogs(List<KeyValuePair<string, object?>> events)
    {
        lock (events)
        {
            return [.. events
                .Where(ev => ev.Key == DiagnosticsEvents.Tasks.Dispatcher && ev.Value is DiagnosticLog)
                .Select(ev => (DiagnosticLog)ev.Value!)
                .Where(log => log.Message.Contains("workers-started", StringComparison.Ordinal) && log.Message.Contains("group=group-batch-test", StringComparison.Ordinal))];
        }
    }

    private static int AggregatedStartCount(DiagnosticLog log)
    {
        const string Marker = "count=";
        int start = log.Message.IndexOf(Marker, StringComparison.Ordinal) + Marker.Length;
        int end = log.Message.IndexOf(' ', start);
        return int.Parse(log.Message.AsSpan(start, end - start), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task ScheduleWorker_WhenStartedEnabled_LogsDetailedStartMessagesOnly()
    {
        using TaskManager manager = this.CreateManager();
        List<KeyValuePair<string, object?>> events = [];

        using IDisposable subscription = DiagnosticsEvents.Source.Subscribe(
            new DelegateObserver<KeyValuePair<string, object?>>(ev =>
            {
                lock (events)
                {
                    events.Add(ev);
                }
            }),
            name => name == DiagnosticsEvents.Tasks.Started || name == DiagnosticsEvents.Tasks.Dispatcher);

        for (int i = 0; i < 4; i++)
        {
            _ = manager.ScheduleWorker(
                $"worker.batch.{i}",
                "group-batch-test",
                (_, _) => ValueTask.CompletedTask,
                new WorkerOptions { Tag = "batch-test", RetainFor = TimeSpan.Zero });
        }

        await Task.Delay(250).ConfigureAwait(false);

        lock (events)
        {
            var startedLogs = events
                .Where(ev => ev.Key == DiagnosticsEvents.Tasks.Started)
                .Select(ev => (DiagnosticLog)ev.Value!)
                .Where(log => log.Message.Contains("group-batch-test"))
                .ToList();
            Assert.Equal(4, startedLogs.Count);
            foreach (var log in startedLogs)
            {
                Assert.Contains("worker-start", log.Message);
            }

            var batchLog = events.FirstOrDefault(ev => ev.Key == DiagnosticsEvents.Tasks.Dispatcher && ev.Value is DiagnosticLog log && log.Message.Contains("workers-started") && log.Message.Contains("group-batch-test"));
            Assert.Null(batchLog.Value);
        }

        await TaskManagerTestHost.WaitUntilAsync(() => manager.GetWorkers(runningOnly: false, group: "group-batch-test").Count == 0, TimeSpan.FromSeconds(5)).ConfigureAwait(false);
    }

    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        private readonly Action<T> _onNext = onNext;

        public void OnNext(T value) => _onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
