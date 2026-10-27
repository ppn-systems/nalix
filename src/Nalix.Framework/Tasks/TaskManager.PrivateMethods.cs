// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Environment.Random;
using Nalix.Environment.Time;
using Nalix.Framework.Options;

namespace Nalix.Framework.Tasks;

public partial class TaskManager
{
    #region Types

    private sealed class Gate
    {
        public SemaphoreSlim SemaphoreSlim { get; }
        public int Capacity { get; private set; }
        private int _deficiency;

        public Gate(int capacity)
        {
            this.Capacity = Math.Max(1, capacity);
            this.SemaphoreSlim = new SemaphoreSlim(this.Capacity, int.MaxValue);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal void Adjust(int newCapacity)
        {
            newCapacity = Math.Max(1, newCapacity);
            int previous = this.Capacity;
            if (previous == newCapacity)
            {
                return;
            }

            this.Capacity = newCapacity;
            int delta = newCapacity - previous;

            if (delta > 0)
            {
                int toRelease = delta;
                int def = Volatile.Read(ref _deficiency);
                while (def > 0 && toRelease > 0)
                {
                    int consumed = Math.Min(def, toRelease);
                    if (Interlocked.CompareExchange(ref _deficiency, def - consumed, def) == def)
                    {
                        toRelease -= consumed;
                    }

                    def = Volatile.Read(ref _deficiency);
                }
                if (toRelease > 0)
                {
                    _ = this.SemaphoreSlim.Release(toRelease);
                }
            }
            else if (delta < 0)
            {
                int toSteal = -delta;
                for (int i = 0; i < toSteal; i++)
                {
                    if (!this.SemaphoreSlim.Wait(0))
                    {
                        _ = Interlocked.Increment(ref _deficiency);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Snapshot of CPU metrics for safe concurrent access.
    /// </summary>
    private sealed class CpuMetricsSnapshot
    {
        public double CurrentUsagePercent { get; set; }
        public long LastUpdateUtc { get; set; }
        public double ProcessorCount { get; set; }
    }

    #endregion Types

    #region Fields (CPU Monitoring)

    private long _lastCpuWallClockMs;
    private long _lastCpuProcessorTime;
    private readonly Stopwatch _cpuMeasureStopwatch = Stopwatch.StartNew();

    private int _lowCpuStreak;
    private int _highCpuStreak;

    private volatile bool _cpuWarmupDone;

    #endregion Fields (CPU Monitoring)

    #region Internal Cleanup

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void CLEANUP_WORKERS()
    {
        if (_disposed)
        {
            return;
        }

        // Cleanup only removes workers that already completed and stayed alive long
        // enough for their RetainFor window to expire.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<ISnowflake, WorkerState> kv in _workers)
        {
            WorkerState st = kv.Value;
            TimeSpan? keep = st.Options.RetainFor;

            if (!st.HasCompleted)
            {
                continue;
            }

            if (keep is null || keep <= TimeSpan.Zero)
            {
                continue;
            }

            if (st.IsRunning)
            {
                continue;
            }

            if (now - st.CompletedUtc < keep.Value)
            {
                continue;
            }

            if (_workers.TryRemove(st.Id, out _))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Cleanup", $"cleanup-remove-ok id={st.Id}"));
                }
                try
                {
                    st.Cts.Dispose();
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Cleanup", $"cleanup-cts-dispose-error id={st.Id}", ex));
                    }
                }

                this.TRY_DISPOSE_GROUP_GATE_IF_UNUSED(st.Group);
            }
        }
    }

    #endregion Internal Cleanup

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void EXECUTE_WORKER_SAFE<TState>(Action<TState>? w, TState? s, string? n)
    {
        try
        {
            _ = Interlocked.Increment(ref _runningWorkerCount);
            int currentRunning = Volatile.Read(ref _runningWorkerCount);
            int peak = Volatile.Read(ref _peakRunningWorkerCount);

            while (currentRunning > peak)
            {
                _ = Interlocked.CompareExchange(ref _peakRunningWorkerCount, currentRunning, peak);
                peak = Volatile.Read(ref _peakRunningWorkerCount);
            }

            w!(s!);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            _ = Interlocked.Increment(ref _workerErrorCount);
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"worker-failed name={n ?? "-"}", ex));
            }
        }
        finally
        {
            _ = Interlocked.Decrement(ref _runningWorkerCount);
        }
    }

    /// <summary>
    /// Calculates an approximate percentile for worker execution time based on histogram buckets.
    /// </summary>
    /// <param name="percentile">The percentile to calculate (e.g., 0.95 for P95).</param>
    /// <returns>The approximate latency in milliseconds.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private double GET_WORKER_UPTIME_PERCENTILE(double percentile)
    {
        long[] snapshot = new long[_workerUptimeBuckets.Length];
        long total = 0;

        for (int i = 0; i < snapshot.Length; i++)
        {
            snapshot[i] = Volatile.Read(ref _workerUptimeBuckets[i]);
            total += snapshot[i];
        }

        if (total == 0)
        {
            return 0;
        }

        long target = (long)(total * percentile);
        long accumulated = 0;

        double[] thresholds = [1.0, 5.0, 10.0, 25.0, 50.0, 100.0, 250.0, 500.0, 1000.0, 2500.0, 5000.0];

        for (int i = 0; i < snapshot.Length; i++)
        {
            accumulated += snapshot[i];
            if (accumulated >= target)
            {
                return thresholds[i];
            }
        }

        return thresholds[^1];
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ENQUEUE_WORKER(WorkerState worker)
    {
        worker.MarkScheduled();
        long sequence = Interlocked.Increment(ref _workerScheduleSequence);
        (int priorityKey, long sequence) priority = (-(int)worker.Options.Priority, sequence);

        lock (_pendingWorkersLock)
        {
            _pendingWorkers.Enqueue(worker, priority);
        }

        _ = _pendingWorkersSignal.Release();
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TRY_START_WORKER_FAST(WorkerState worker)
    {
        lock (_pendingWorkersLock)
        {
            if (_pendingWorkers.Count != 0)
            {
                return false;
            }

            if (!_globalConcurrencyGate.Wait(0))
            {
                return false;
            }

            worker.MarkScheduled(); // Mark even for fast-start so wait time is 0
        }

        try
        {
            this.START_WORKER_EXECUTION(worker);
        }
        catch
        {
            // Start failed before the worker body owned the global permit: return it and drop the worker.
            this.RELEASE_GLOBAL_GATE();
            _ = _workers.TryRemove(worker.Id, out _);
            throw;
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private async Task WORKER_DISPATCH_LOOP_ASYNC(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _pendingWorkersSignal.WaitAsync(ct).ConfigureAwait(false);
                await _globalConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);

                WorkerState? worker = null;

                try
                {
                    lock (_pendingWorkersLock)
                    {
                        if (_pendingWorkers.Count > 0)
                        {
                            worker = _pendingWorkers.Dequeue();
                        }
                    }

                    if (worker is null)
                    {
                        this.RELEASE_GLOBAL_GATE();
                        continue;
                    }

                    this.START_WORKER_EXECUTION(worker);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    this.RELEASE_GLOBAL_GATE();

                    if (worker is not null)
                    {
                        worker.MarkError(ex);
                        _ = Interlocked.Increment(ref _workerErrorCount);
                        this.RETAIN_OR_REMOVE(worker);
                    }

                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", "worker-dispatch-error", ex));
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", "dispatch-loop-error", ex));
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void ADJUST_GROUP_CONCURRENCY(string group, int newLimit)
    {
        if (string.IsNullOrWhiteSpace(group))
        {
            return;
        }

        if (_groupGates.TryGetValue(group, out Gate? gate))
        {
            gate.Adjust(newLimit);
        }
        else
        {
            _ = _groupGates.TryAdd(group, new Gate(newLimit));
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"group-concurrency-adjusted group={group} new-limit={newLimit}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void START_WORKER_EXECUTION(WorkerState st)
    {
        string name = st.Name;
        string group = st.Group;
        ISnowflake id = st.Id;
        IWorkerOptions options = st.Options;
        CancellationTokenSource cts = st.Cts;

        Gate? gate = null;
        if (options.GroupConcurrencyLimit is int cap && cap > 0)
        {
            gate = _groupGates.GetOrAdd(group, static (_, capacity) => new Gate(capacity), cap);
            if (gate.Capacity != cap)
            {
                throw new InvalidOperationException(
                    $"Worker group '{group}' already uses concurrency limit {gate.Capacity}, which conflicts with requested limit {cap}.");
            }
        }

        if (options.OSPriority is ThreadPriority osPriority)
        {
            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            st.Task = tcs.Task;

            Thread thread = new(() =>
            {
                try
                {
                    Thread.CurrentThread.Priority = osPriority;

                    if (options.ProcessorAffinity is int coreIndex)
                    {
                        SET_THREAD_AFFINITY(coreIndex, name);
                    }

                    Task execution = this.EXECUTE_WORKER_ASYNC(st, gate, cts);
                    if (execution.IsCompletedSuccessfully)
                    {
                        _ = tcs.TrySetResult();
                        return;
                    }

                    _ = execution.ContinueWith(static (task, state) =>
                    {
                        if (state is not TaskCompletionSource source)
                        {
                            return;
                        }

                        if (task.IsCanceled)
                        {
                            _ = source.TrySetCanceled();
                            return;
                        }

                        if (task.Exception?.GetBaseException() is Exception ex)
                        {
                            _ = source.TrySetException(ex);
                            return;
                        }

                        _ = source.TrySetResult();
                    }, tcs, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    _ = tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = name
            };
            thread.Start();

            bool isDebugEnabled = DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Started);
            if (isDebugEnabled)
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Started, new DiagnosticLog("FW.TaskManager", $"worker-start-dedicated id={id} name={name} group={group} os-priority={osPriority} tag={options.Tag ?? "-"}"));
            }
            else if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
            {
                this.LOG_WORKER_START_AGGREGATED(group, options.Priority, options.Tag ?? "-");
            }
        }
        else
        {
            st.Task = this.EXECUTE_WORKER_ASYNC(st, gate, cts);
            bool isDebugEnabled = DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Started);
            if (isDebugEnabled)
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Started, new DiagnosticLog("FW.TaskManager", $"worker-start id={id} name={name} group={group} priority={options.Priority} tag={options.Tag ?? "-"}"));
            }
            else if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
            {
                this.LOG_WORKER_START_AGGREGATED(group, options.Priority, options.Tag ?? "-");
            }
        }
    }

    private async Task EXECUTE_WORKER_ASYNC(WorkerState st, Gate? gate, CancellationTokenSource cts)
    {
        string name = st.Name;
        string group = st.Group;
        ISnowflake id = st.Id;
        IWorkerOptions options = st.Options;
        WorkerOptions? concreteOptions = options as WorkerOptions;

        bool acquired = false;
        bool startedExecution = false;
        CancellationToken ct = cts.Token;
        bool completedSuccessfully = false;
        bool wasCancelled = false;
        Exception? failure = null;
        long executionStartTicks = 0;

        try
        {
            if (gate is not null)
            {
                if (options.TryAcquireSlotImmediately)
                {
                    acquired = await gate.SemaphoreSlim.WaitAsync(0, ct)
                                                       .ConfigureAwait(false);
                    if (!acquired)
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager", $"worker-reject name={name} group={group} error=group-cap"));
                        }

                        _ = _workers.TryRemove(id, out _);
                        try
                        {
                            cts.Dispose();
                        }
                        catch (ObjectDisposedException) { }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager", $"cts-dispose-error-reject id={id}", ex));
                            }
                        }

                        return;
                    }
                }
                else
                {
                    await gate.SemaphoreSlim.WaitAsync(ct)
                                            .ConfigureAwait(false);
                    acquired = true;
                }
            }

            st.MarkStart();
            startedExecution = true;

            int currentRunning = Interlocked.Increment(ref _runningWorkerCount);
            int peak = Volatile.Read(ref _peakRunningWorkerCount);
            while (currentRunning > peak)
            {
                _ = Interlocked.CompareExchange(ref _peakRunningWorkerCount, currentRunning, peak);
                peak = Volatile.Read(ref _peakRunningWorkerCount);
            }

            if (_options.IsEnableLatency)
            {
                long waitTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks - st.ScheduledUtc.UtcTicks;
                if (waitTicks > 0)
                {
                    _ = Interlocked.Add(ref _workerWaitTicks, waitTicks);
                }
            }

            executionStartTicks = _options.IsEnableLatency ? Stopwatch.GetTimestamp() : 0;
            WorkerContext ctx = new(st, this);

            if (options.ExecutionTimeout is { } to && to > TimeSpan.Zero)
            {
                using CancellationTokenSource wcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wcts.CancelAfter(to);
                await st.Work(ctx, wcts.Token).ConfigureAwait(false);
            }
            else
            {
                await st.Work(ctx, ct).ConfigureAwait(false);
            }

            completedSuccessfully = true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            wasCancelled = true;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            failure = ex;
            _ = Interlocked.Increment(ref _workerErrorCount);

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"worker-error id={id} name={name}", ex));
            }
        }
        finally
        {
            if (failure is not null)
            {
                st.MarkError(failure);
            }
            else if (completedSuccessfully || wasCancelled)
            {
                st.MarkStop();
            }

            try
            {
                if (failure is null)
                {
                    concreteOptions?.OnCompleted?.Invoke(st);
                }
                else
                {
                    concreteOptions?.OnFailed?.Invoke(st, failure);
                }
            }
            catch (Exception cbex) when (ExceptionClassifier.IsNonFatal(cbex))
            {
                _ = Interlocked.Increment(ref _workerErrorCount);

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"worker-callback-error id={id}", cbex));
                }
            }

            if (gate is not null && acquired)
            {
                try
                {
                    _ = gate.SemaphoreSlim.Release();
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    _ = Interlocked.Increment(ref _workerErrorCount);

                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"gate-release-error id={id}", ex));
                    }
                }
            }

            if (startedExecution)
            {
                _ = Interlocked.Decrement(ref _runningWorkerCount);

                if (_options.IsEnableLatency && executionStartTicks != 0)
                {
                    long elapsedTicks = Stopwatch.GetTimestamp() - executionStartTicks;
                    _ = Interlocked.Increment(ref _workerCompletionCount);
                    _ = Interlocked.Add(ref _workerUptimeTicks, elapsedTicks);

                    double ms = (double)elapsedTicks / Stopwatch.Frequency * 1000.0;
                    int bucketIndex = ms switch
                    {
                        < 1.0 => 0,
                        < 5.0 => 1,
                        < 10.0 => 2,
                        < 25.0 => 3,
                        < 50.0 => 4,
                        < 100.0 => 5,
                        < 250.0 => 6,
                        < 500.0 => 7,
                        < 1000.0 => 8,
                        < 2500.0 => 9,
                        _ => 10
                    };
                    _ = Interlocked.Increment(ref _workerUptimeBuckets[bucketIndex]);
                }
            }

            this.RETAIN_OR_REMOVE(st);
            this.RELEASE_GLOBAL_GATE();
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task RECURRING_LOOP_ASYNC(
        RecurringState s, Func<CancellationToken, ValueTask> work)
    {
        CancellationToken ct = s.CancellationTokenSource.Token;

        // Initial jitter spreads recurring jobs out so they do not all wake up together.
        if (s.Options.Jitter is { } j && j > TimeSpan.Zero)
        {
            try
            {
                int maxMs = (int)j.TotalMilliseconds;
                if (maxMs > 0)
                {
                    TimeSpan jitter = TimeSpan.FromMilliseconds(Csprng.GetInt32(0, maxMs));
                    await Task.Delay(jitter, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
        }

        // Work entirely in stopwatch ticks to avoid drift from repeated TimeSpan conversions.
        long step = s.IntervalTicks;
        long freq = Stopwatch.Frequency;
        long next = Stopwatch.GetTimestamp() + step;

        // Busy-wait is only used for tiny gaps where a full Task.Delay would overshoot too much.
        static void BusyWait(long untilTicks, CancellationToken ct)
        {
            SpinWait sw = new();
            while (Stopwatch.GetTimestamp() < untilTicks)
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                sw.SpinOnce(sleep1Threshold: -1);
            }
        }

        // Above this threshold, a normal delay is cheaper and less CPU intensive than spinning.
        double busyWaitMaxSeconds = _options.BusyWaitThreshold.TotalSeconds;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                long now = Clock.MonoTicksNow();
                long delayTicks = next - now;

                if (delayTicks > 0)
                {
                    double delaySeconds = (double)delayTicks / freq;

                    if (delaySeconds <= busyWaitMaxSeconds)
                    {
                        BusyWait(next, ct);
                    }
                    else
                    {
                        TimeSpan ts = TimeSpan.FromSeconds(delaySeconds);
                        await Task.Delay(ts, ct)
                                                         .ConfigureAwait(false);
                    }
                }
                else
                {
                    // If the job fell behind, skip the missed slots instead of replaying every one.
                    long missed = ((-delayTicks) + step - 1) / step;
                    next += (missed + 1) * step;
                }

                if (s.Options.NonReentrant)
                {
                    // A zero-timeout acquire keeps the scheduler from overlapping the same recurring job.
                    if (!await s.Gate.WaitAsync(0, ct).ConfigureAwait(false))
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"gate-acquire-fail name={s.Name}"));
                        }
                        next += step;
                        continue;
                    }
                }

                long executionStartTicks = 0;
                try
                {
                    if (s.IsPaused)
                    {
                        next += step;
                        continue;
                    }

                    s.MarkStart();
                    executionStartTicks = _options.IsEnableLatency ? Stopwatch.GetTimestamp() : 0;

                    if (s.Options.ExecutionTimeout is { } to && to > TimeSpan.Zero)
                    {
                        using CancellationTokenSource rcts =
                            CancellationTokenSource.CreateLinkedTokenSource(ct);

                        rcts.CancelAfter(to);
                        await work(rcts.Token).ConfigureAwait(false);
                    }
                    else
                    {
                        await work(ct).ConfigureAwait(false);
                    }

                    s.MarkSuccess();
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException oce)
                {
                    s.MarkFailure();
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-timeout name={s.Name}", oce));
                    }

                    await this.RECURRING_BACKOFF_ASYNC(s, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    s.MarkFailure();
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-error name={s.Name}", ex));
                    }

                    await this.RECURRING_BACKOFF_ASYNC(s, ct).ConfigureAwait(false);
                }
                finally
                {
                    if (_options.IsEnableLatency && executionStartTicks != 0)
                    {
                        long elapsedTicks = Stopwatch.GetTimestamp() - executionStartTicks;
                        _ = Interlocked.Increment(ref _recurringExecutionCount);
                        _ = Interlocked.Add(ref _recurringExecutionTicks, elapsedTicks);
                    }

                    if (s.Options.NonReentrant)
                    {
                        try
                        {
                            _ = s.Gate.Release();
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                            {
                                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-gate-release-error name={s.Name}", ex));
                            }
                        }
                    }
                    next += step;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                s.MarkFailure();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-loop-error name={s.Name}", ex));
                }

                await this.RECURRING_BACKOFF_ASYNC(s, ct).ConfigureAwait(false);
            }
        }
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async ValueTask RECURRING_BACKOFF_ASYNC(
        RecurringState s,
        CancellationToken ct)
    {
        // Backoff only starts after a configurable number of consecutive failures.
        int n = Math.Max(1, s.Options.FailuresBeforeBackoff);
        if (s.ConsecutiveFailures < n)
        {
            return;
        }

        // Cap the exponential growth so a broken recurring job does not disappear forever.
        int pow = Math.Min(_options.BackoffMaxPower, s.ConsecutiveFailures - n);
        long baseMs = (long)_options.BackoffBaseInterval.TotalMilliseconds << pow;
        int cap = (int)Math.Clamp(s.Options.BackoffCap.TotalMilliseconds, 1, int.MaxValue - 1);
        int maxDelay = (int)Math.Min(baseMs, cap);

        // Full jitter avoids synchronized retries when many jobs fail at the same time.
        // Each retry gets a random delay inside the capped exponential window instead
        // of all workers retrying at the exact same moment.
        int delayMs = Csprng.GetInt32(0, maxDelay + 1);

        try
        {
            await Task.Delay(delayMs, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (OperationCanceledException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", "backoff-cancelled", ex));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RELEASE_GLOBAL_GATE()
    {
        // Check if we need to 'steal' this permit to satisfy a pending limit reduction
        int def = Volatile.Read(ref _concurrencyDeficiency);
        while (def > 0)
        {
            if (Interlocked.CompareExchange(ref _concurrencyDeficiency, def - 1, def) == def)
            {
                // Permit consumed by the deficiency, don't release to semaphore
                return;
            }
            def = Volatile.Read(ref _concurrencyDeficiency);
        }

        // No deficiency, return the permit to the global pool. The gate is disposed by Dispose()
        // while workers may still be finishing, so a late release must be a no-op.
        try
        {
            _ = _globalConcurrencyGate.Release();
        }
        catch (ObjectDisposedException) when (_disposed) { }
    }


    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void RETAIN_OR_REMOVE(WorkerState st)
    {
        TimeSpan? keep = st.Options.RetainFor;
        // If retention is disabled, remove the worker as soon as it finishes.
        if (keep is null || keep <= TimeSpan.Zero)
        {
            _ = _workers.TryRemove(st.Id, out _);

            try
            {
                st.Cts.Dispose();
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Cleanup", $"retain-cts-dispose-error id={st.Id}", ex));
                }
            }

            this.TRY_DISPOSE_GROUP_GATE_IF_UNUSED(st.Group);

            return;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void TRY_DISPOSE_GROUP_GATE_IF_UNUSED(string group)
    {
        foreach (WorkerState other in _workers.Values)
        {
            if (string.Equals(other.Group, group, StringComparison.Ordinal))
            {
                return;
            }
        }

        if (_groupGates.TryRemove(group, out Gate? gate))
        {
            try
            {
                gate.SemaphoreSlim.Dispose();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"group-gate-dispose-ok group={group}"));
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"gate-dispose-error-retain group={group}", ex));
                }
            }
        }
    }

    private async Task MONITOR_CONCURRENCY_ASYNC(IWorkerContext ctx, CancellationToken ct)
    {
        TaskManagerOptions options = _options;

        // Số lần liên tiếp vượt ngưỡng trước khi hành động (hysteresis)
        int streakRequired = options.AdjustmentStreakRequired;

        // Normalize threshold: config là % trên 1 core -> scale lên toàn bộ core
        double coreCount = System.Environment.ProcessorCount;
        double threshHigh = options.ThresholdHighCpu * coreCount;
        double threshLow = options.ThresholdLowCpu * coreCount;

        // Khởi tạo baseline CPU trước khi vòng lặp bắt đầu
        this.INITIALIZE_CPU_MEASUREMENT();

        while (!ct.IsCancellationRequested && options.DynamicAdjustmentEnabled)
        {
            try
            {
                double cpuUsage = this.MEASURE_CPU_USAGE_PERCENT();

                if (cpuUsage > threshHigh)
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
                    {
                        string usageStr = cpuUsage.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        string thresholdStr = threshHigh.ToString(System.Globalization.CultureInfo.InvariantCulture);
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"cpu-high usage={usageStr} threshold={thresholdStr}"));
                    }
                }

                // --- Hysteresis: tích streak, chỉ hành động khi đủ N lần liên tiếp ---
                if (cpuUsage > threshHigh && _currentConcurrencyLimit > 1)
                {
                    _lowCpuStreak = 0;
                    _highCpuStreak++;

                    if (_highCpuStreak >= streakRequired)
                    {
                        _highCpuStreak = 0; // reset sau khi hành động
                        int newLimit = Math.Max(1, _currentConcurrencyLimit - 1);
                        this.ADJUST_CONCURRENCY(newLimit);
                    }
                }
                else if (cpuUsage < threshLow && _currentConcurrencyLimit < options.MaxWorkers)
                {
                    _highCpuStreak = 0;
                    _lowCpuStreak++;

                    if (_lowCpuStreak >= streakRequired)
                    {
                        _lowCpuStreak = 0; // reset sau khi hành động
                        int newLimit = Math.Min(options.MaxWorkers, _currentConcurrencyLimit + 1);
                        this.ADJUST_CONCURRENCY(newLimit);
                    }
                }
                else
                {
                    // CPU trong vùng ổn định -> reset cả hai streak
                    _highCpuStreak = 0;
                    _lowCpuStreak = 0;
                }

                ctx.Beat();
                ctx.Advance(1);

                await Task.Delay(options.ObservingInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", "dynamic-adjustment-error", ex));
                }
            }
        }
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void INITIALIZE_CPU_MEASUREMENT()
    {
        using Process proc = Process.GetCurrentProcess();
        Volatile.Write(ref _lastCpuProcessorTime, (long)proc.TotalProcessorTime.TotalMilliseconds);
        Volatile.Write(ref _lastCpuWallClockMs, _cpuMeasureStopwatch.ElapsedMilliseconds);
        _cpuWarmupDone = false;
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private double MEASURE_CPU_USAGE_PERCENT()
    {
        long currentWallMs = _cpuMeasureStopwatch.ElapsedMilliseconds;

        // Warmup: trong 60 giây đầu, bỏ qua và KHÔNG cập nhật baseline
        // Điều này để lần đo sau warmup tính delta đúng kể từ lúc baseline được init
        if (!_cpuWarmupDone)
        {
            if (currentWallMs < _options.CpuWarmupDuration.TotalMilliseconds)
            {
                return 0.0;
            }

            // Đánh dấu đã xong warmup, cập nhật baseline một lần ngay lúc này
            using Process proc0 = Process.GetCurrentProcess();
            proc0.Refresh();
            Volatile.Write(ref _lastCpuProcessorTime, (long)proc0.TotalProcessorTime.TotalMilliseconds);
            Volatile.Write(ref _lastCpuWallClockMs, currentWallMs);
            _cpuWarmupDone = true;
            return 0.0;
        }

        using Process proc = Process.GetCurrentProcess();
        proc.Refresh();
        long currentCpuMs = (long)proc.TotalProcessorTime.TotalMilliseconds;

        long prevWallMs = Volatile.Read(ref _lastCpuWallClockMs);
        long prevCpuMs = Volatile.Read(ref _lastCpuProcessorTime);

        long wallDelta = currentWallMs - prevWallMs;
        long cpuDelta = currentCpuMs - prevCpuMs;

        // Cập nhật baseline cho lần đo tiếp theo
        Volatile.Write(ref _lastCpuWallClockMs, currentWallMs);
        Volatile.Write(ref _lastCpuProcessorTime, currentCpuMs);

        // Tránh chia cho 0 hoặc delta âm (clock skew, process refresh lag)
        if (wallDelta <= 0 || cpuDelta < 0)
        {
            return 0.0;
        }

        double processorCount = System.Environment.ProcessorCount;

        // cpuDelta / wallDelta = tỷ lệ sử dụng trên 1 core -> nhân processorCount -> % trên toàn bộ core
        double cpuUsagePercent = cpuDelta / (double)wallDelta / processorCount * 100.0;

        return Math.Min(cpuUsagePercent, processorCount * 100.0);
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ADJUST_CONCURRENCY(int newLimit)
    {
        // Safety: clamp to valid range
        newLimit = Math.Clamp(newLimit, 1, _options.MaxWorkers);

        int previousLimit = _currentConcurrencyLimit;

        // No change = skip
        if (previousLimit == newLimit)
        {
            return;
        }

        _currentConcurrencyLimit = newLimit;
        Volatile.Write(ref _concurrencyLimitRatio, _options.MaxWorkers > 0 ? (double)newLimit / _options.MaxWorkers : 1.0);
        int delta = newLimit - previousLimit;

        try
        {
            if (delta > 0)
            {
                // Tăng capacity: ưu tiên bù đắp deficiency trước, sau đó mới release vào semaphore
                int toRelease = delta;
                int def = Volatile.Read(ref _concurrencyDeficiency);
                while (def > 0 && toRelease > 0)
                {
                    int consumed = Math.Min(def, toRelease);
                    if (Interlocked.CompareExchange(ref _concurrencyDeficiency, def - consumed, def) == def)
                    {
                        toRelease -= consumed;
                    }
                    def = Volatile.Read(ref _concurrencyDeficiency);
                }

                if (toRelease > 0)
                {
                    _ = _globalConcurrencyGate.Release(toRelease);
                }
            }
            else if (delta < 0)
            {
                // Giảm capacity: thu hồi slot bằng Wait(0) non-blocking
                // Nếu slot đang bị giữ (worker đang chạy), tăng deficiency để 'trộm' slot ngay khi nó được release
                int toSteal = -delta;
                for (int i = 0; i < toSteal; i++)
                {
                    if (!_globalConcurrencyGate.Wait(0))
                    {
                        // Không thể trộm ngay -> đánh dấu nợ (deficiency)
                        _ = Interlocked.Increment(ref _concurrencyDeficiency);
                    }
                }
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher,
                    new DiagnosticLog("FW.TaskManager:Internal", $"concurrency-limit-adjusted previous-limit={previousLimit} new-limit={_currentConcurrencyLimit} deficiency={_concurrencyDeficiency}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Revert on error
            _currentConcurrencyLimit = previousLimit;
            Volatile.Write(ref _concurrencyLimitRatio, _options.MaxWorkers > 0 ? (double)previousLimit / _options.MaxWorkers : 1.0);

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"failed-adjust-concurrency from={previousLimit} to={newLimit}", ex));
            }
        }
    }

    private void LOG_WORKER_START_AGGREGATED(string group, WorkerPriority priority, string tag)
    {
        (string Group, WorkerPriority Priority, string Tag) key = (group, priority, tag);
        WorkerStartBatch batch = _startBatches.GetOrAdd(key, static k => new WorkerStartBatch(k.Group, k.Priority, k.Tag));

        lock (batch.Lock)
        {
            batch.Count++;
            batch.FlushTask ??= Task.Run(async () =>
            {
                await Task.Delay(100).ConfigureAwait(false);
                int count;
                lock (batch.Lock)
                {
                    count = batch.Count;
                    batch.Count = 0;
                    batch.FlushTask = null;
                }
                if (count > 0 && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager", $"workers-started group={batch.Group} count={count} priority={batch.Priority.ToString().ToLowerInvariant()} tag={batch.Tag}"));
                }
            });
        }
    }
}
