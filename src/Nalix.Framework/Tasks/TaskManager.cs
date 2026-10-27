// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Injection;
using Nalix.Environment.Configuration;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Options;

namespace Nalix.Framework.Tasks;

/// <summary>
/// Coordinates background worker tasks and recurring jobs with shared concurrency,
/// cancellation, timing, and reporting support.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[DebuggerDisplay("TaskManager (Workers={_workers.Count}, Recurring={_recurring.Count})")]
[Injectable(typeof(ITaskManager))]
public sealed partial class TaskManager : ITaskManager, IDisposable
{
    #region Fields

    private readonly Task _cleanupTask;
    private readonly PeriodicTimer _cleanupPeriodicTimer;
    private readonly CancellationTokenSource _cleanupCts;

    private readonly Lock _pendingWorkersLock;
    private readonly Task _workerDispatcherTask;
    private readonly TaskManagerOptions _options;
    private readonly SemaphoreSlim _pendingWorkersSignal;
    private readonly SemaphoreSlim _globalConcurrencyGate;
    private readonly CancellationTokenSource _workerDispatcherCts;
    private readonly PriorityQueue<WorkerState, (int priorityKey, long sequence)> _pendingWorkers;
    private readonly ConcurrentDictionary<string, Gate> _groupGates;
    private readonly ConcurrentDictionary<ISnowflake, WorkerState> _workers;
    private readonly ConcurrentDictionary<string, RecurringState> _recurring;
    private readonly ConcurrentDictionary<(string Group, WorkerPriority Priority, string Tag), WorkerStartBatch> _startBatches;

    private int _workerErrorCount;
    private int _runningWorkerCount;
    private int _peakRunningWorkerCount;
    private int _recurringErrorCount;
    private long _workerWaitTicks;
    private long _workerUptimeTicks;
    private long _workerCompletionCount;
    private long _workerScheduleSequence;
    private long _recurringExecutionTicks;
    private long _recurringExecutionCount;
    private readonly long[] _workerUptimeBuckets = new long[11];

    private volatile bool _disposed;
    private volatile int _currentConcurrencyLimit;
    private double _concurrencyLimitRatio;
    private int _concurrencyDeficiency;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets a compact status line for consoles and diagnostics.
    /// </summary>
    /// <remarks>
    /// The title is intentionally short so it can be shown in window titles or
    /// one-line dashboards without formatting or truncation logic.
    /// </remarks>
    public string Title
    {
        get
        {
            int recurring = _recurring.Count;
            int totalWorkers = _workers.Count;
            int runningWorkers = Volatile.Read(ref _runningWorkerCount);
            return $"Workers: {runningWorkers} running / {totalWorkers} total | Recurring: {recurring}";
        }
    }

    /// <inheritdoc/>
    public double ConcurrencyLimitRatio => Volatile.Read(ref _concurrencyLimitRatio);

    /// <summary>
    /// Gets the average execution time for worker tasks in milliseconds.
    /// </summary>
    /// <remarks>
    /// The value is derived from the accumulated stopwatch ticks and the number
    /// of completed executions, so it reflects the long-term average rather than
    /// the most recent run.
    /// </remarks>
    public double AverageWorkerUptime =>
        _workerCompletionCount == 0 ? 0 : _workerUptimeTicks / (double)_workerCompletionCount / Stopwatch.Frequency * 1000;

    /// <summary>
    /// Gets the average execution time for recurring tasks in milliseconds.
    /// </summary>
    /// <remarks>
    /// This uses the same tick-based accounting as worker timings so the report
    /// stays consistent across both task types.
    /// </remarks>
    public double AverageRecurringExecutionTime =>
        _recurringExecutionCount == 0 ? 0 : _recurringExecutionTicks / (double)_recurringExecutionCount / Stopwatch.Frequency * 1000;

    /// <summary>
    /// Gets the total worker errors observed.
    /// </summary>
    /// <remarks>
    /// The counter is read with <see langword="Volatile.Read"/> because it
    /// may be incremented from worker completion paths on different threads.
    /// </remarks>
    public int WorkerErrorCount => Volatile.Read(ref _workerErrorCount);

    /// <summary>
    /// Gets the total recurring task errors observed.
    /// </summary>
    /// <remarks>
    /// The counter is read with <see langword="Volatile.Read"/> because it
    /// may be updated concurrently by recurring execution paths.
    /// </remarks>
    public int RecurringErrorCount => Volatile.Read(ref _recurringErrorCount);

    /// <summary>
    /// Gets the peak number of concurrently running workers observed.
    /// </summary>
    public int PeakRunningWorkerCount => Volatile.Read(ref _peakRunningWorkerCount);

    /// <summary>
    /// Gets the average time workers spent in the queue before starting, in milliseconds.
    /// </summary>
    public double AverageWorkerWaitTime =>
        _workerCompletionCount == 0 ? 0 : (double)_workerWaitTicks / _workerCompletionCount / 10000.0;

    /// <summary>
    /// Gets the 95th percentile worker uptime in milliseconds (approximation).
    /// </summary>
    public double P95WorkerUptime => this.GET_WORKER_UPTIME_PERCENTILE(0.95);

    /// <summary>
    /// Gets the 99th percentile worker uptime in milliseconds (approximation).
    /// </summary>
    public double P99WorkerUptime => this.GET_WORKER_UPTIME_PERCENTILE(0.99);

    #endregion Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskManager"/> class.
    /// </summary>
    /// <remarks>
    /// The constructor wires up shared concurrency gates, periodic cleanup, and
    /// optional monitoring workers before any user-defined task is scheduled.
    /// </remarks>
    /// <param name="options">Optional configuration options for the TaskManager.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="options"/> contains invalid scheduling limits.</exception>
    /// <exception cref="InvalidOperationException">Thrown when startup monitoring workers cannot be scheduled.</exception>
    public TaskManager(TaskManagerOptions? options = null)
    {
        _options = options ?? new TaskManagerOptions();
        _options.Validate();

        _workers = new();
        _recurring = new();
        _startBatches = new();
        _cleanupCts = new();
        _groupGates = new(StringComparer.Ordinal);
        _pendingWorkers = new();
        _pendingWorkersLock = new();
        _pendingWorkersSignal = new SemaphoreSlim(0, int.MaxValue);
        _workerDispatcherCts = new();
        _currentConcurrencyLimit = _options.MaxWorkers;
        // Starts at full concurrency (limit == MaxWorkers), so the initial ratio is 1.0.
        _concurrencyLimitRatio = 1.0;
        _globalConcurrencyGate = new SemaphoreSlim(_currentConcurrencyLimit, _options.MaxWorkers);
        _workerDispatcherTask = this.WORKER_DISPATCH_LOOP_ASYNC(_workerDispatcherCts.Token);

        _cleanupPeriodicTimer = new PeriodicTimer(_options.CleanupInterval);
        _cleanupTask = Task.Run(async () =>
        {
            try
            {
                while (await _cleanupPeriodicTimer.WaitForNextTickAsync(_cleanupCts.Token)
                                                 .ConfigureAwait(false))
                {
                    if (_disposed)
                    {
                        break;
                    }

                    this.CLEANUP_WORKERS();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", "cleanup-loop-error", ex));
                }
            }
        }, _cleanupCts.Token);

        if (_options.DynamicAdjustmentEnabled)
        {
            _ = this.ScheduleWorker(
                "task.monitor",
                "task",
                async (ctx, ct) => await this.MONITOR_CONCURRENCY_ASYNC(ctx, ct).ConfigureAwait(false),
                new WorkerOptions
                {
                    // Keep the monitor worker around longer so transient spikes can still be reported.
                    RetainFor = TimeSpan.FromMinutes(10)
                }
            );
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
        {
            string cleanupSec = _options.CleanupInterval.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture);
            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"init cleanup-interval-sec={cleanupSec} concurrency={_currentConcurrencyLimit}"));
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskManager"/> class.
    /// </summary>
    public TaskManager() : this(ConfigurationManager.Instance.Get<TaskManagerOptions>())
    {
    }

    #endregion Constructors

    #region APIs

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown if the name is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown if the work delegate is null.</exception>
    /// <exception cref="InternalErrorException">Thrown if the worker cannot be added.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IWorkerHandle ScheduleWorker(string name, string group, Func<IWorkerContext, CancellationToken, ValueTask> work, IWorkerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));

        if (string.IsNullOrWhiteSpace(group))
        {
            group = "-";
        }

        ArgumentNullException.ThrowIfNull(work);

        options ??= new WorkerOptions();

        ISnowflake id = Snowflake.NewId(options.IdType, options.MachineId);
#pragma warning disable CA2000 // Ownership is transferred to WorkerState and disposed during worker cleanup/manager teardown.
        CancellationTokenSource cts = options.CancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken)
            : new CancellationTokenSource();
#pragma warning restore CA2000

        WorkerState st = new(id, name, group, options, cts, work);

        if (!_workers.TryAdd(id, st))
        {
            cts.Dispose();
            throw new InternalErrorException($"[{nameof(TaskManager)}:{nameof(ScheduleWorker)}] cannot add worker");
        }

        bool startedFast = this.TRY_START_WORKER_FAST(st);
        if (!startedFast)
        {
            this.ENQUEUE_WORKER(st);
        }

        return st;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IWorkerHandle ScheduleWorker(IWorker worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));

        Type type = worker.GetType();
        WorkerAttribute? attr = type.GetCustomAttribute<WorkerAttribute>();

        string name = attr?.Name ?? type.Name.ToLowerInvariant();
        string group = attr?.Group ?? "hosted";

        if (attr is { Enabled: false })
        {
            return NoOpWorkerHandle.Instance;
        }

        WorkerOptions options = new()
        {
            Tag = attr?.Tag ?? attr?.Group ?? "hosted",
            IdType = (SnowflakeType)(attr?.IdType ?? 1),
            RetainFor = TimeSpan.FromMilliseconds(attr?.RetainForMs ?? 0),
            Priority = (WorkerPriority)(attr?.Priority ?? 0),
            ProcessorAffinity = attr?.ProcessorAffinity >= 0 ? attr?.ProcessorAffinity : null
        };

        if (attr is not null && attr.GroupConcurrencyLimit > 0)
        {
            options.GroupConcurrencyLimit = attr.GroupConcurrencyLimit;
        }

        return this.ScheduleWorker(name, group, worker.ExecuteAsync, options);
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ScheduleWorker<TState>(string name, Action<TState> work, TState state)
    {
        ArgumentNullException.ThrowIfNull(work, nameof(work));
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));

        // Completion count is incremented on the actual completion path (EXECUTE_WORKER_SAFE),
        // alongside the uptime/wait ticks it is the denominator for. Do NOT increment at schedule
        // time — that double-counts and skews AverageWorkerUptime/WaitTime.
        _ = ThreadPool.UnsafeQueueUserWorkItem(static tuple =>
        {
            (Action<TState>? w, TState? s, string? n, TaskManager? m) = tuple;
            m.EXECUTE_WORKER_SAFE(w, s, n);
        }, (work, state, name, this), preferLocal: false);
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown if the name is null or whitespace.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if the interval is less than or equal to zero.</exception>
    /// <exception cref="ArgumentNullException">Thrown if the work delegate is null.</exception>
    /// <exception cref="InternalErrorException">Thrown if a recurring task with the same name already exists.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public IRecurringHandle ScheduleRecurring([StringSyntax("identifier")] string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, IRecurringOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(name, nameof(name));
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        options ??= new RecurringOptions();
#pragma warning disable CA2000 // Ownership is transferred to RecurringState and disposed after its loop stops.
        CancellationTokenSource cts = new();
        RecurringState st = new(name, interval, options, cts) { Owner = this };
#pragma warning restore CA2000

        if (!_recurring.TryAdd(name, st))
        {
            _ = Interlocked.Increment(ref _recurringErrorCount);
            throw new InternalErrorException($"[{nameof(TaskManager)}] duplicate recurring name: {name}");
        }

        st.Task = this.RECURRING_LOOP_ASYNC(st, work);

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Started))
        {
            string intervalMs = interval.TotalMilliseconds.ToString("0.000", CultureInfo.InvariantCulture);
            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Started, new DiagnosticLog("FW.TaskManager",
                $"recurring name={name} interval-ms={intervalMs} non-reentrant={options.NonReentrant} tag={options.Tag ?? "-"}"));
        }
        return st;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentException">Thrown if the name is null or whitespace.</exception>
    /// <exception cref="ArgumentNullException">Thrown if the work delegate is null.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the supplied <paramref name="ct"/> is cancelled while <paramref name="work"/> is running.</exception>
    /// <exception cref="Exception">Propagates any exception thrown by <paramref name="work"/> after it is logged.</exception>
    /// <exception cref="ObjectDisposedException">Thrown if the manager has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async ValueTask RunOnceAsync(string name, Func<CancellationToken, ValueTask> work, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));

        try
        {
            await work(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"run-once-failed name={name}", ex));
            }
            throw;
        }
    }

    /// <summary>
    /// Dynamically updates the concurrency limit for the specified task group at runtime.
    /// </summary>
    /// <param name="group">
    /// The name of the task group whose concurrency limit will be updated.
    /// </param>
    /// <param name="limit">
    /// The maximum number of concurrent tasks allowed for the group.
    /// </param>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="group"/> is <see langword="null"/>, empty, or whitespace.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// Thrown when the current <c>TaskManager</c> instance has already been disposed.
    /// </exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void SetGroupConcurrencyLimit(string group, int limit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(group));
        ObjectDisposedException.ThrowIf(_disposed, nameof(TaskManager));

        this.ADJUST_GROUP_CONCURRENCY(group, limit);
    }

    /// <summary>
    /// Pauses the specified recurring job.
    /// </summary>
    /// <param name="name">
    /// The unique name of the recurring job to pause.
    /// </param>
    /// <remarks>
    /// While paused, scheduled executions for the recurring job are skipped
    /// until <see cref="ResumeRecurring(string)"/> is called.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void PauseRecurring(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_recurring.TryGetValue(name, out RecurringState? st))
        {
            st.Pause();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-paused name={name}"));
            }
        }
    }

    /// <summary>
    /// Resumes a previously paused recurring job.
    /// </summary>
    /// <param name="name">
    /// The unique name of the recurring job to resume.
    /// </param>
    /// <remarks>
    /// Once resumed, the recurring job continues executing according
    /// to its configured schedule.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResumeRecurring(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (_recurring.TryGetValue(name, out RecurringState? st))
        {
            st.Resume();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Dispatcher))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Dispatcher, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-resumed name={name}"));
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when a matched worker's cancellation source has already been disposed.</exception>
    [Pure]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CancelAllWorkers()
    {
        int n = 0;
        foreach (KeyValuePair<ISnowflake, WorkerState> kv in _workers)
        {
            if (!kv.Value.Cts.IsCancellationRequested)
            {
                kv.Value.Cancel(); n++;
            }
        }
        if (n > 0)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Cancelled))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Cancelled, new DiagnosticLog("FW.TaskManager:Internal", $"all count={n}"));
            }
        }

        return n;
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when the matched worker's cancellation source has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CancelWorker(ISnowflake id)
    {
        if (!_workers.TryGetValue(id, out WorkerState? st))
        {
            return;
        }

        st.Cancel();

        Task? t = st.Task;

        if (t?.IsCompleted == true)
        {
            try
            {
                st.Cts.Dispose();
            }
            catch (ObjectDisposedException) { } // Ignore if already disposed
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"cts-dispose-error id={id}", ex));
                }
            }
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Cancelled))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Cancelled, new DiagnosticLog("FW.TaskManager:Internal", $"worker-cancelled id={id} name={st.Name} group={st.Group}"));
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">Thrown when a matched worker's cancellation source has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int CancelGroup(string group)
    {
        int n = 0;
        foreach (KeyValuePair<ISnowflake, WorkerState> kv in _workers)
        {
            WorkerState st = kv.Value;
            if (string.Equals(st.Group, group, StringComparison.Ordinal) && !st.Cts.IsCancellationRequested)
            {
                st.Cancel();
                n++;
            }
        }
        if (n > 0)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Cancelled))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Cancelled, new DiagnosticLog("FW.TaskManager:Internal", $"group group={group} count={n}"));
            }
        }

        return n;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">Thrown if the name is null or whitespace.</exception>"
    /// <exception cref="ObjectDisposedException">Thrown when the matched recurring task's cancellation source has already been disposed.</exception>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void CancelRecurring(string? name)
    {
        ArgumentNullException.ThrowIfNull(name, nameof(name));

#pragma warning disable CA2000 // RecurringState is disposed asynchronously after its running task observes cancellation.
        if (!_recurring.TryRemove(name, out RecurringState? st))
#pragma warning restore CA2000
        {
            return;
        }

        st.Cancel();

        Task? t = st.Task;

        if (t is not null)
        {
            _ = t.ContinueWith(_ =>
            {
                try { st.CancellationTokenSource.Dispose(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"cts-dispose-error name={name}", ex));
                    }
                }
                try { st.Gate.Dispose(); }
                catch (ObjectDisposedException) { }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"gate-dispose-error name={name}", ex));
                    }
                }
            },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }
        else
        {
            try
            {
                st.CancellationTokenSource.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"cts-dispose-error-sync name={name}", ex));
                }
            }
            try
            {
                st.Gate.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"gate-dispose-error-sync name={name}", ex));
                }
            }
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Cancelled))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Cancelled, new DiagnosticLog("FW.TaskManager:Internal", $"recurring-cancelled name={name}"));
        }
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public IReadOnlyCollection<IWorkerHandle> GetWorkers(bool runningOnly = true, string? group = null)
    {
        List<IWorkerHandle> list = new(_workers.Count);
        foreach (KeyValuePair<ISnowflake, WorkerState> kv in _workers)
        {
            WorkerState st = kv.Value;
            if (runningOnly && !st.IsRunning)
            {
                continue;
            }

            if (group is not null && !string.Equals(st.Group, group, StringComparison.Ordinal))
            {
                continue;
            }

            list.Add(st);
        }
        return list;
    }

    /// <inheritdoc/>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public IReadOnlyCollection<IRecurringHandle> GetRecurring()
    {
        List<IRecurringHandle> list = new(_recurring.Count);
        foreach (KeyValuePair<string, RecurringState> kv in _recurring)
        {
            list.Add(kv.Value);
        }

        return list;
    }

    /// <inheritdoc/>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetWorker(ISnowflake id, [NotNullWhen(true)] out IWorkerHandle? handle)
    {
        if (_workers.TryGetValue(id, out WorkerState? st)) { handle = st; return true; }
        handle = null;
        return false;
    }

    /// <inheritdoc/>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryGetRecurring(string name, [NotNullWhen(true)] out IRecurringHandle? handle)
    {
        if (_recurring.TryGetValue(name, out RecurringState? st)) { handle = st; return true; }
        handle = null;
        return false;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public async Task WaitGroupAsync(string group, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(group, nameof(group));

        bool isPrefix = group.EndsWith('*');
        string matchGroup = isPrefix ? group[..^1] : group;

        List<Task> tasks = new();
        foreach (KeyValuePair<ISnowflake, WorkerState> kv in _workers)
        {
            WorkerState st = kv.Value;
            bool match = isPrefix
                ? st.Group.StartsWith(matchGroup, StringComparison.Ordinal)
                : string.Equals(st.Group, matchGroup, StringComparison.Ordinal);

            if (match)
            {
                Task? t = st.Task;
                if (t is not null)
                {
                    tasks.Add(t);
                }
            }
        }

        if (tasks.Count == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(tasks).WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // We only care about waiting, not the result of the tasks.
            // If they faulted, it's already logged by the worker loop.
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Tasks.Failed))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Tasks.Failed, new DiagnosticLog("FW.TaskManager:Internal", $"wait-group-async group={group}", ex));
            }
        }
    }

    #endregion APIs
}
