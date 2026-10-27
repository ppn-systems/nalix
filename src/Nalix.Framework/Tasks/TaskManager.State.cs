// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Identity;

namespace Nalix.Framework.Tasks;

public partial class TaskManager
{
    [DebuggerDisplay("Worker {Name} (Running={IsRunning}, Runs={TotalRuns})")]
    private sealed class WorkerState(
        ISnowflake id,
        string name,
        string group,
        IWorkerOptions opt,
        CancellationTokenSource cts,
        Func<IWorkerContext, CancellationToken, ValueTask> work) : IWorkerHandle
    {
        #region Backing fields (thread-safe)

        // Monotonic progress counter updated by the worker body and reported through IWorkerHandle.
        private long _progress;

        /// <summary>
        /// Stores whether the worker is currently executing.
        /// <para>0 means idle, 1 means running.</para>
        /// </summary>
        private int _isRunning;

        /// <summary>
        /// Total number of completed attempts, including success, cancellation, and failure.
        /// </summary>
        private long _totalRuns;

        /// <summary>
        /// Start timestamp for the most recent execution, stored as UTC ticks.
        /// </summary>
        private long _startedUtcTicks;

        /// <summary>
        /// Last heartbeat timestamp for the current or most recent execution.
        /// <para>0 means there is no heartbeat yet.</para>
        /// </summary>
        private long _lastHeartbeatUtcTicks;

        /// <summary>
        /// Completion timestamp for cleanup retention.
        /// <para>0 means the worker has not completed yet.</para>
        /// </summary>
        private long _completedUtcTicks;

        /// <summary>
        /// Timestamp of when the worker was scheduled (added to queue).
        /// </summary>
        private long _scheduledUtcTicks;

        #endregion Backing fields (thread-safe)

        #region Properties

        /// <summary>
        /// Holds the actual scheduled task so the manager can observe or clean it up.
        /// </summary>
        public Task? Task;

        public ISnowflake Id { get; } = id;

        public string Name { get; } = name;

        public string Group { get; } = group;

        public IWorkerOptions Options { get; } = opt;

        public CancellationTokenSource Cts { get; } = cts;

        public Func<IWorkerContext, CancellationToken, ValueTask> Work { get; } = work;

        public string? LastNote
        {
            get => Volatile.Read(ref field);
            private set => Volatile.Write(ref field, value);
        }

        public bool IsRunning
        {
            get => Volatile.Read(ref _isRunning) != 0;
            private set => Volatile.Write(ref _isRunning, value ? 1 : 0);
        }

        public long TotalRuns
        {
            get => Interlocked.Read(ref _totalRuns);
            private set => Interlocked.Exchange(ref _totalRuns, value);
        }

        public DateTimeOffset StartedUtc
            => new(Interlocked.Read(ref _startedUtcTicks), TimeSpan.Zero);

        public DateTimeOffset? LastHeartbeatUtc
        {
            get
            {
                long t = Interlocked.Read(ref _lastHeartbeatUtcTicks);
                return t == 0 ? null : new System.DateTimeOffset(t, System.TimeSpan.Zero);
            }
            private set
            {
                long t = value?.UtcDateTime.Ticks ?? 0L;
                _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, t);
            }
        }

        /// <summary>
        /// Gets the completion time used by retention and cleanup logic.
        /// </summary>
        internal DateTimeOffset? CompletedUtc
        {
            get
            {
                long t = Interlocked.Read(ref _completedUtcTicks);
                return t == 0 ? null : new System.DateTimeOffset(t, System.TimeSpan.Zero);
            }
            private set
            {
                long t = value?.UtcDateTime.Ticks ?? 0L;
                _ = Interlocked.Exchange(ref _completedUtcTicks, t);
            }
        }

        internal bool HasCompleted => Interlocked.Read(ref _completedUtcTicks) != 0;

        /// <summary>
        /// Gets the time when the worker was scheduled.
        /// </summary>
        internal DateTimeOffset ScheduledUtc
            => new(Interlocked.Read(ref _scheduledUtcTicks), TimeSpan.Zero);

        #endregion Properties

        #region Computed Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkScheduled()
        {
            long nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _ = Interlocked.Exchange(ref _scheduledUtcTicks, nowTicks);
        }

        /// <summary>
        /// Marks the worker as running and resets completion metadata for a new execution.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkStart()
        {
            // Capture one UTC timestamp and reuse it for all start-related fields so the
            // execution snapshot stays internally consistent.
            long nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _ = Interlocked.Exchange(ref _startedUtcTicks, nowTicks);
            _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, nowTicks);
            _ = Interlocked.Exchange(ref _completedUtcTicks, 0);
            this.IsRunning = true;
        }

        /// <summary>
        /// Marks the worker as finished after a successful execution.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkStop()
        {
            // Stop first so observers stop counting this worker as active before the
            // completion timestamp is published.
            this.IsRunning = false;
            _ = Interlocked.Increment(ref _totalRuns);
            long nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _ = Interlocked.Exchange(ref _completedUtcTicks, nowTicks);
            _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, nowTicks);
        }

        /// <summary>
        /// Marks the worker as finished after an error path.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkError(Exception __)
        {
            // Error completion uses the same lifecycle fields as success so cleanup
            // and reporting do not need a separate branch.
            this.IsRunning = false;
            long ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;

            _ = Interlocked.Increment(ref _totalRuns);
            _ = Interlocked.Exchange(ref _completedUtcTicks, ticks);
            _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, ticks);
        }

        /// <summary>
        /// Refreshes the heartbeat timestamp to show that the worker is still alive.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Beat()
        {
            long ticks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, ticks);
        }

        /// <summary>
        /// Gets the total amount of progress accumulated by the worker.
        /// </summary>
        public long Progress => Interlocked.Read(ref _progress);

        /// <summary>
        /// Adds progress and optionally stores a human-readable note for diagnostics.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public void Add(long delta, string? note)
        {
            if (delta != 0)
            {
                _ = Interlocked.Add(ref _progress, delta);
            }

            if (note is not null)
            {
                this.LastNote = note;
            }

            long nowTicks = DateTimeOffset.UtcNow.UtcDateTime.Ticks;
            _ = Interlocked.Exchange(ref _lastHeartbeatUtcTicks, nowTicks);
        }

        /// <summary>
        /// Requests cancellation of the worker token source.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Cancel()
        {
            try
            {
                this.Cts?.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void IDisposable.Dispose() => this.Cancel();

        #endregion Computed Methods

        #region IWorkerHandle

        ISnowflake IWorkerHandle.Id => this.Id;

        string IWorkerHandle.Name => this.Name;

        string IWorkerHandle.Group => this.Group;

        bool IWorkerHandle.IsRunning => this.IsRunning;

        long IWorkerHandle.TotalRuns => this.TotalRuns;

        DateTimeOffset IWorkerHandle.StartedUtc => this.StartedUtc;

        [MaybeNull]
        DateTimeOffset? IWorkerHandle.LastHeartbeatUtc => this.LastHeartbeatUtc;

        long IWorkerHandle.Progress => this.Progress;

        [MaybeNull]
        string? IWorkerHandle.LastNote => this.LastNote;

        IWorkerOptions IWorkerHandle.Options => this.Options;

        #endregion IWorkerHandle
    }

    [DebuggerDisplay("Recurring {Name} (Every={Interval}, Runs={TotalRuns}, Failures={ConsecutiveFailures})")]
    private sealed class RecurringState(
        string name,
        TimeSpan iv,
        IRecurringOptions opt,
        CancellationTokenSource cts) : IRecurringHandle
    {
        #region Properties / fields

        private int _isPaused;

        // Recurring jobs use a one-per-loop gate when NonReentrant is enabled so an
        // overrun does not overlap with the next scheduled tick.
        public readonly SemaphoreSlim Gate = new(1, 1);

        // Owning manager, set once the state is registered; lets handle disposal unregister it.
        public TaskManager? Owner;

        // The running task reference is tracked only for diagnostics and cleanup.
        public Task? Task;

        public string Name { get; } = name;

        public TimeSpan Interval { get; } = iv;

        public IRecurringOptions Options { get; } = opt;

        public CancellationTokenSource CancellationTokenSource { get; } = cts;

        // Precompute the interval in stopwatch ticks once so the loop does not repeat
        // the TimeSpan conversion on every iteration.
        public long IntervalTicks { get; } = (long)(iv.TotalSeconds * Stopwatch.Frequency) switch
        {
            <= 0 => 1,
            var x => x
        };

        /// <summary>
        /// Total number of times the recurring job has been attempted.
        /// </summary>
        private long _totalRuns;

        // Consecutive failure counter used to decide when exponential backoff starts.
        private int _consecutiveFailures;

        /// <summary>
        /// Stores whether the recurring job is currently executing.
        /// <para>0 means idle, 1 means running.</para>
        /// </summary>
        private int _isRunning;

        /// <summary>
        /// Timestamp of the most recent execution start in UTC ticks.
        /// </summary>
        private long _lastRunUtcTicks;

        public long TotalRuns
        {
            get => Interlocked.Read(ref _totalRuns);
            private set => Interlocked.Exchange(ref _totalRuns, value);
        }

        public int ConsecutiveFailures
        {
            get => Volatile.Read(ref _consecutiveFailures);
            private set => Volatile.Write(ref _consecutiveFailures, value);
        }

        public bool IsRunning
        {
            get => Volatile.Read(ref _isRunning) != 0;
            private set => Volatile.Write(ref _isRunning, value ? 1 : 0);
        }

        public DateTimeOffset? LastRunUtc
        {
            get
            {
                long t = Interlocked.Read(ref _lastRunUtcTicks);
                return t == 0 ? null : new System.DateTimeOffset(t, System.TimeSpan.Zero);
            }
            private set
            {
                long t = value?.UtcDateTime.Ticks ?? 0L;
                _ = Interlocked.Exchange(ref _lastRunUtcTicks, t);
            }
        }

        public bool IsPaused
        {
            get => Volatile.Read(ref _isPaused) != 0;
            private set => Volatile.Write(ref _isPaused, value ? 1 : 0);
        }

        public DateTimeOffset? NextRunUtc => this.LastRunUtc?.Add(this.Interval);

        #endregion Properties / fields

        #region Computed Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Pause() => this.IsPaused = true;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Resume() => this.IsPaused = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkStart()
        {
            this.IsRunning = true;
            this.LastRunUtc = DateTimeOffset.UtcNow;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkSuccess()
        {
            this.IsRunning = false;
            this.ConsecutiveFailures = 0;
            _ = Interlocked.Increment(ref _totalRuns);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void MarkFailure()
        {
            this.IsRunning = false;
            _ = Interlocked.Increment(ref _consecutiveFailures);
            _ = Interlocked.Increment(ref _totalRuns);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Cancel()
        {
            try
            {
                this.CancellationTokenSource.Cancel();
            }
            catch (ObjectDisposedException) { }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        void IDisposable.Dispose()
        {
            // While registered, route through the owner so the entry leaves _recurring and the
            // CTS/Gate are disposed only after the loop observed cancellation.
            TaskManager? owner = this.Owner;
            if (owner is not null
                && owner._recurring.TryGetValue(this.Name, out RecurringState? current)
                && ReferenceEquals(current, this))
            {
                owner.CancelRecurring(this.Name);
            }
            else
            {
                this.Cancel();
            }

            this.IsPaused = false;
        }

        #endregion Computed Methods

        #region IRecurringHandle

        string IRecurringHandle.Name => this.Name;

        bool IRecurringHandle.IsPaused => this.IsPaused;

        bool IRecurringHandle.IsRunning => this.IsRunning;

        long IRecurringHandle.TotalRuns => this.TotalRuns;

        int IRecurringHandle.ConsecutiveFailures => this.ConsecutiveFailures;

        [MaybeNull]
        DateTimeOffset? IRecurringHandle.LastRunUtc => this.LastRunUtc;

        [MaybeNull]
        DateTimeOffset? IRecurringHandle.NextRunUtc => this.NextRunUtc;

        TimeSpan IRecurringHandle.Interval => this.Interval;

        IRecurringOptions IRecurringHandle.Options => this.Options;

        #endregion IRecurringHandle
    }

    [SkipLocalsInit]
    private sealed class WorkerContext(
        WorkerState st,
        TaskManager owner) : IWorkerContext
    {
        private readonly WorkerState _st = st;

        [SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "<Pending>")]
        private readonly TaskManager _owner = owner;

        public ISnowflake Id => _st.Id;
        public string Name => _st.Name;
        public string Group => _st.Group;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Beat() => _st.Beat();

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Advance(long delta, string? note = null) => _st.Add(delta, note);

        public bool IsCancellationRequested => _st.Cts.IsCancellationRequested;
    }

    private sealed class NoOpWorkerHandle : IWorkerHandle
    {
        public static readonly NoOpWorkerHandle Instance = new();

        private NoOpWorkerHandle() { }

        public ISnowflake Id => Identifiers.Snowflake.Empty;
        public string Name => "noop";
        public string Group => "noop";
        public long TotalRuns => 0;
        public long Progress => 0;
        public string? LastNote => null;
        public IWorkerOptions Options => new Nalix.Framework.Options.WorkerOptions { Tag = "noop" };
        public bool IsRunning => false;
        public DateTimeOffset StartedUtc => DateTimeOffset.UnixEpoch;
        public DateTimeOffset? LastHeartbeatUtc => null;

        public void Dispose() { }
    }

    private sealed class WorkerStartBatch
    {
        public readonly string Group;
        public readonly WorkerPriority Priority;
        public readonly string Tag;
        public int Count;
        public readonly object Lock = new();
        public Task? FlushTask;

        public WorkerStartBatch(string group, WorkerPriority priority, string tag)
        {
            Group = group;
            Priority = priority;
            Tag = tag;
        }
    }
}
