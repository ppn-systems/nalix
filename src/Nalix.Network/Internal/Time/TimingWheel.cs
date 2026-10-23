// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Injection;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Configuration;
using Nalix.Environment.Time;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Options;

namespace Nalix.Network.Internal.Time;

/// <summary>
/// Provides an ultra-lightweight <b>Hashed Wheel Timer</b> for idle connection cleanup,
/// optimized to minimize allocations and avoid duplicate handlers.
/// </summary>
/// <remarks>
/// <para>
/// The timer periodically inspects registered <see cref="IConnection"/> instances and closes those
/// that have been idle longer than the configured threshold
/// (<see cref="TimingWheelOptions.IdleTimeoutMs"/>).
/// </para>
/// <para>
/// It uses a hashed timing wheel:
/// <list type="bullet">
///   <item>
///     <description>Each task is placed into a bucket computed from <c>tick % wheelSize</c>.</description>
///   </item>
///   <item>
///     <description><c>Rounds</c> indicates the number of full wheel rotations before execution.</description>
///   </item>
///   <item>
///     <description>On each tick, only the current bucket is processed for O(k) where k is items in that bucket.</description>
///   </item>
/// </list>
/// </para>
/// <para><b>Thread safety:</b> <see cref="Register(ITimeoutTrackedConnection)"/> and <see cref="Unregister(ITimeoutTrackedConnection)"/> are thread-safe.
/// The background loop is single-consumer and advances the wheel using <see cref="PeriodicTimer"/>.</para>
/// <para><b>Pool ownership:</b> <see cref="RUN_LOOP"/> is the <em>only</em> place that returns
/// <see cref="TimeoutTask"/> to the pool. <see cref="Unregister"/> only removes the connection from
/// <c>_active</c> â€” the task stays in the wheel queue until the loop dequeues it, at which point the
/// version mismatch causes a safe <c>Return</c>. This avoids the race where pool-reset clears
/// <c>task.Conn</c> before the loop has a chance to read it.</para>
/// </remarks>
/// <example>
/// <code>
/// var timer = new TimingWheel();
/// timer.Activate();
/// timer.Register(connection);
/// // ...
/// timer.Deactivate();
/// </code>
/// </example>
/// <seealso cref="IActivatable"/>
[Injectable]
[SkipLocalsInit]
[DebuggerNonUserCode]
internal sealed class TimingWheel : IActivatable
{
    #region Fields

    private readonly TimingWheelOptions _options;
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private readonly int _tickMs;
    private readonly int _wheelSize;
    private readonly int _idleTimeoutMs;

    /// <summary>
    /// Mask is used only when WheelSize is a power of two.
    /// </summary>
    private readonly int _mask;
    private readonly bool _useMask;

    /// <summary>
    /// One bucket per slot in the timing wheel. 
    /// Uses a doubly-linked list with lock to allow O(1) removal.
    /// </summary>
    private readonly TimingWheelBucket[] _wheel;

    /// <summary>
    /// Array of locks to synchronize connection registration without mutating IConnection.
    /// Uses lock striping to minimize contention.
    /// </summary>
    private readonly Lock[] _connectionLocks;

    private int _activeListeners;
    private long _tick;
    private int _disposed;
#pragma warning disable CA2213 // Worker handle is cancelled/disposed via Interlocked.Exchange in Deactivate; analyzer does not track exchange-based cleanup.
    private IWorkerHandle? _worker;
#pragma warning restore CA2213
#pragma warning disable CA2213 // Cancellation source is cancelled/disposed via Interlocked.Exchange in Deactivate; analyzer does not track exchange-based cleanup.
    private CancellationTokenSource? _cts;
#pragma warning restore CA2213

#pragma warning restore CA2213

    private int _registeredCount;
    private int _peakRegistered;
    private long _totalRegistrations;
    private long _totalUnregistrations;
    private long _totalTimeouts;
    private long _totalStaleSkips;
    private long _maxTickDrift;

    #endregion Fields

    #region Nested types

    internal interface ITimeoutTrackedConnection : IConnection
    {
        int IdleTimeoutMs { get; set; }
        int TimeoutVersion { get; set; }
        bool IsRegisteredInWheel { get; set; }
        TimeoutTask? TimeoutTask { get; set; }
    }

    /// <summary>
    /// Represents a single timeout task in the wheel.
    /// One instance is reused per registered connection via the object pool.
    /// </summary>
    internal sealed class TimeoutTask : IPoolable
    {
        public ITimeoutTrackedConnection? Conn;
        public int Rounds;
        public int Version;

        internal TimeoutTask? Next;
        internal TimeoutTask? Prev;
        internal TimingWheelBucket? Bucket;

        public void ResetForPool()
        {
            Conn = null;
            Rounds = 0;
            Version = 0;
            Next = null;
            Prev = null;
            Bucket = null;
        }
    }

    /// <summary>
    /// A lock-based doubly-linked list bucket.
    /// Allows O(1) removal of tasks during Unregister to prevent pool exhaustion.
    /// </summary>
    internal sealed class TimingWheelBucket
    {
        public TimeoutTask? Head;
        public TimeoutTask? Tail;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Enqueue(TimeoutTask task)
        {
            lock (this)
            {
                task.Bucket = this;
                task.Next = null;
                task.Prev = Tail;

                if (Tail != null)
                {
                    Tail.Next = task;
                }
                else
                {
                    Head = task;
                }
                Tail = task;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Remove(TimeoutTask task)
        {
            // Assumes caller holds lock(this)
            if (task.Prev != null)
            {
                task.Prev.Next = task.Next;
            }
            else
            {
                Head = task.Next;
            }

            if (task.Next != null)
            {
                task.Next.Prev = task.Prev;
            }
            else
            {
                Tail = task.Prev;
            }

            task.Next = null;
            task.Prev = null;
            task.Bucket = null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public TimeoutTask? DequeueAll()
        {
            lock (this)
            {
                TimeoutTask? currentHead = Head;
                TimeoutTask? curr = currentHead;
                while (curr != null)
                {
                    curr.Bucket = null; // Detach from bucket to prevent concurrent Unregister
                    curr = curr.Next;
                }
                Head = null;
                Tail = null;
                return currentHead;
            }
        }
    }

    #endregion Nested types

    #region Ctor

    /// <summary>
    /// Initializes a new instance of the <see cref="TimingWheel"/> class
    /// using values from <see cref="TimingWheelOptions"/> via <see cref="ConfigurationManager"/>.
    /// </summary>
    public TimingWheel()
    {
        _options = ConfigurationManager.Instance.Get<TimingWheelOptions>();

        _options.Validate();

        _wheelSize = _options.BucketCount;
        _tickMs = _options.TickDuration;
        _idleTimeoutMs = _options.IdleTimeoutMs;

        _useMask = (_wheelSize & (_wheelSize - 1)) == 0 && _wheelSize > 0;
        _mask = _useMask ? (_wheelSize - 1) : 0;

        _wheel = new TimingWheelBucket[_wheelSize];
        for (int i = 0; i < _wheelSize; i++)
        {
            _wheel[i] = new TimingWheelBucket();
        }

        _connectionLocks = new Lock[256];
        for (int i = 0; i < 256; i++)
        {
            _connectionLocks[i] = new Lock();
        }

        _disposed = 0;
    }

    #endregion Ctor

    #region IActivatable

    /// <summary>
    /// Starts the background timing loop if it is not already running.
    /// </summary>
    /// <param name="cancellationToken"></param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Activate(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, nameof(TimingWheel));

        if (Interlocked.Increment(ref _activeListeners) > 1)
        {
            return;
        }

        CancellationTokenSource linkedCts = cancellationToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            : new CancellationTokenSource();

        _cts = linkedCts;

        _worker = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleWorker(
            name: $"{TaskNaming.Tags.Time}.{TaskNaming.Tags.Wheel}",
            group: TaskNaming.Tags.Time,
            work: async (ctx, ct) => await this.RUN_LOOP(ctx, ct).ConfigureAwait(false),
            options: new WorkerOptions
            {
                Tag = TaskNaming.Tags.Wheel,
                IdType = SnowflakeType.System,
                CancellationToken = linkedCts.Token,
                RetainFor = TimeSpan.Zero
            }
        );

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information, new DiagnosticLog("NW.TimingWheelBucket:Activate", $"activated active-listeners={_activeListeners.ToString(CultureInfo.InvariantCulture)} wheel-size={_wheelSize.ToString(CultureInfo.InvariantCulture)} tick-ms={_tickMs.ToString(CultureInfo.InvariantCulture)} idle-timeout-ms={_idleTimeoutMs.ToString(CultureInfo.InvariantCulture)} use-mask={_useMask.ToString().ToLowerInvariant()}"));
        }
    }

    /// <summary>
    /// Stops the background timing loop and drains all buckets back to the pool.
    /// </summary>
    /// <param name="cancellationToken"></param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Deactivate(CancellationToken cancellationToken = default)
    {
        int count = Interlocked.Decrement(ref _activeListeners);
        if (count > 0)
        {
            return;
        }

        // Clip to zero in case of mismatched Deactivate calls
        if (count < 0)
        {
            _ = Interlocked.Exchange(ref _activeListeners, 0);
            return;
        }

        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        IWorkerHandle? worker = Interlocked.Exchange(ref _worker, null);
        bool loopStopped = true;
        if (worker != null)
        {
            worker.Dispose();

            int elapsed = 0;
            int timeout = _options.WheelDrainTimeoutMs;
            while (worker.IsRunning && elapsed < timeout)
            {
                Thread.Sleep(10);
                elapsed += 10;
            }

            // If the loop is still running after the drain timeout, it may still be
            // dequeuing/re-enqueuing and returning tasks to the pool. Running the bucket
            // drain concurrently with a live loop can return a task the loop already
            // returned and the pool re-rented (Conn set again) — a double-return that
            // corrupts the pool. In that case skip the drain: the still-live loop owns
            // its tasks and will return them itself.
            loopStopped = !worker.IsRunning;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                string exceptionType = ex.GetType().Name;
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", $"cts-cancel-ignored reason-exception-type={exceptionType}", ex));
                }
                ;
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", "cts-cancel-failed", ex));
            }
        }

        try
        {
            cts.Dispose();
        }
        catch (ObjectDisposedException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                string exceptionType = ex.GetType().Name;
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", $"cts-dispose-ignored reason-exception-type={exceptionType}", ex));
                }
                ;
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", "cts-dispose-failed", ex));
            }
        }

        try
        {
            worker?.Dispose();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", "worker-dispose-failed", ex));
            }
        }

        if (loopStopped)
        {
            this.DRAIN_AND_RELEASE_ALL_BUCKETS();
        }
        else if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", "drain-skipped reason=loop-still-running-after-timeout"));
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Information, new DiagnosticLog("NW.TimingWheelBucket:Deactivate", "deactivated"));
        }
    }

    #endregion IActivatable

    #region Public APIs

    /// <summary>
    /// Registers a connection for idle-timeout monitoring.
    /// </summary>
    /// <param name="connection">The connection to monitor.</param>
    /// <remarks>
    /// If the connection is already registered the call is a no-op.
    /// The method subscribes to <see cref="IConnection.ConnectionClosed"/> once so that
    /// the connection is automatically unregistered when it closes.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Register(ITimeoutTrackedConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        Lock syncLock = _connectionLocks[connection.ConnectionId.GetHashCode() & 255];
        lock (syncLock)
        {
            if (connection.IsRegisteredInWheel)
            {
                return;
            }

            connection.IsRegisteredInWheel = true;

            int newCount = Interlocked.Increment(ref _registeredCount);
            _ = Interlocked.Increment(ref _totalRegistrations);
            int peak;
            while (newCount > (peak = Volatile.Read(ref _peakRegistered)))
            {
                if (Interlocked.CompareExchange(ref _peakRegistered, newCount, peak) == peak)
                {
                    break;
                }
            }

            TimeoutTask? task = null;
            bool subscribed = false;
            bool queued = false;

            try
            {
                task = s_pool.Get<TimeoutTask>();
                task.Conn = connection;

                // Set version to match current connection version.
                task.Version = connection.TimeoutVersion;

                long baseTick = Volatile.Read(ref _tick);
                long ticks = Math.Max(1, connection.IdleTimeoutMs / (long)_tickMs);

                int bucket = _useMask
                    ? (int)((baseTick + ticks) & _mask)
                    : (int)((baseTick + ticks) % _wheelSize);

                task.Rounds = (int)((ticks - 1) / _wheelSize);

                connection.ConnectionClosed += this.OnConnectionClosed;
                subscribed = true;

                _wheel[bucket].Enqueue(task);
                queued = true;

                // Link the task to the connection for instant cleanup during Dispose.
                connection.TimeoutTask = task;
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (task is not null && !queued)
                {
                    s_pool.Return(task);
                }

                if (subscribed)
                {
                    connection.ConnectionClosed -= this.OnConnectionClosed;
                }

                connection.IsRegisteredInWheel = false;
                throw;
            }
        }
    }

    /// <summary>
    /// Removes a connection from idle-timeout monitoring.
    /// </summary>
    /// <param name="connection">The connection to stop monitoring.</param>
    /// <remarks>
    /// Since the wheel now uses a doubly-linked list with lock striping, this method
    /// safely removes the task from the wheel bucket in O(1) time and returns it to the
    /// object pool immediately, preventing memory exhaustion under high connection churn.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Unregister(ITimeoutTrackedConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        Lock syncLock = _connectionLocks[connection.ConnectionId.GetHashCode() & 255];
        lock (syncLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (connection.IsRegisteredInWheel)
            {
                connection.IsRegisteredInWheel = false;
                _ = Interlocked.Decrement(ref _registeredCount);
                _ = Interlocked.Increment(ref _totalUnregistrations);
                connection.TimeoutVersion++;
                connection.ConnectionClosed -= this.OnConnectionClosed;

                if (connection.TimeoutTask is TimeoutTask task)
                {
                    TimingWheelBucket? bucket = task.Bucket;
                    if (bucket != null)
                    {
                        bool removed = false;
                        lock (bucket)
                        {
                            if (task.Bucket == bucket)
                            {
                                bucket.Remove(task);
                                removed = true;
                            }
                        }

                        if (removed)
                        {
                            connection.TimeoutTask = null;
                            s_pool.Return(task);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Releases resources and stops the background loop. Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _disposed, 1, 0) != 0)
        {
            return;
        }

        this.Deactivate();
    }

    /// <summary>Gets diagnostic statistics about timing wheel.</summary>
    public TimingWheelMetrics GetStatistics()
    {
        return new TimingWheelMetrics(
            Volatile.Read(ref _registeredCount),
            Volatile.Read(ref _peakRegistered),
            Volatile.Read(ref _totalRegistrations),
            Volatile.Read(ref _totalUnregistrations),
            Volatile.Read(ref _totalTimeouts),
            Volatile.Read(ref _totalStaleSkips),
            Volatile.Read(ref _maxTickDrift)
        );
    }

    #endregion Public APIs

    #region Loop

    [MethodImpl(MethodImplOptions.NoInlining)]
    private async Task RUN_LOOP(IWorkerContext ctx, CancellationToken ct)
    {
        _ = Interlocked.Exchange(ref _tick, 0);
        long startTime = Clock.MonoTicksNow();
        long lastProcessedTick = -1;

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(_tickMs));

            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                ctx.Beat();

                long currentMono = Clock.MonoTicksNow();
                double elapsedMs = Clock.MonoTicksToMilliseconds(currentMono - startTime);
                long expectedTick = (long)(elapsedMs / _tickMs);

                long drift = expectedTick - lastProcessedTick;
                if (drift > Volatile.Read(ref _maxTickDrift))
                {
                    Volatile.Write(ref _maxTickDrift, drift);
                }

                // Catch up if we've missed any ticks due to system load or loop delay.
                while (lastProcessedTick < expectedTick)
                {
                    lastProcessedTick++;
                    long tickToProcess = lastProcessedTick;

                    int bucketIndex = _useMask
                        ? (int)(tickToProcess & _mask)
                        : (int)(tickToProcess % _wheelSize);

                    // Update the public tick count so that new registrations calculate
                    // their destination bucket based on the current logical time.
                    _ = Interlocked.Exchange(ref _tick, tickToProcess);

                    TimeoutTask? task = _wheel[bucketIndex].DequeueAll();

                    while (task is not null)
                    {
                        TimeoutTask? next = task.Next;

                        ITimeoutTrackedConnection? connection = task.Conn;

                        if (connection is null)
                        {
                            s_pool.Return(task);
                            task = next;

                            continue;
                        }

                        Lock syncLock = _connectionLocks[connection.ConnectionId.GetHashCode() & 255];

                        // If version mismatch or connection is marked as not in wheel, it's stale.
                        if (connection.TimeoutVersion != task.Version || !connection.IsRegisteredInWheel)
                        {
                            _ = Interlocked.Increment(ref _totalStaleSkips);
                            lock (syncLock)
                            {
                                if (ReferenceEquals(connection.TimeoutTask, task))
                                {
                                    connection.TimeoutTask = null;
                                }
                            }

                            s_pool.Return(task);
                            task = next;

                            continue;
                        }

                        if (task.Rounds > 0)
                        {
                            task.Rounds--;

                            lock (syncLock)
                            {
                                if (!connection.IsRegisteredInWheel || connection.TimeoutVersion != task.Version)
                                {
                                    _ = Interlocked.Increment(ref _totalStaleSkips);
                                    if (ReferenceEquals(connection.TimeoutTask, task))
                                    {
                                        connection.TimeoutTask = null;
                                    }

                                    s_pool.Return(task);
                                    task = next;
                                    continue;
                                }

                                task.Next = null;
                                task.Prev = null;
                                _wheel[bucketIndex].Enqueue(task);
                            }

                            task = next;

                            continue;
                        }

                        if (connection.ExcludeFromIdleTimeout)
                        {
                            lock (syncLock)
                            {
                                if (connection.IsRegisteredInWheel && connection.TimeoutVersion == task.Version)
                                {
                                    connection.IsRegisteredInWheel = false;
                                    _ = Interlocked.Decrement(ref _registeredCount);
                                    connection.TimeoutVersion++;
                                    connection.ConnectionClosed -= this.OnConnectionClosed;
                                }
                                else
                                {
                                    _ = Interlocked.Increment(ref _totalStaleSkips);
                                }

                                if (ReferenceEquals(connection.TimeoutTask, task))
                                {
                                    connection.TimeoutTask = null;
                                }
                            }

                            s_pool.Return(task);
                            task = next;
                            continue;
                        }

                        long idleMs = Clock.UnixMillisecondsNow() - connection.LastPingTime;
                        int currentTimeoutMs = connection.IdleTimeoutMs;

                        if (idleMs >= currentTimeoutMs)
                        {
                            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
                                {
                                    string idleMsStr = idleMs.ToString(CultureInfo.InvariantCulture);
                                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.TimingWheelBucket:Internal", $"timeout remote={connection.NetworkEndpoint.Address} idle-ms={idleMsStr}"));
                                }
                                ;
                            }

                            try
                            {
                                _ = Interlocked.Increment(ref _totalTimeouts);
                                connection.Disconnect();
                            }
                            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                                {
                                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                                    {
                                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.TimingWheelBucket:Internal", $"close-error remote={connection.NetworkEndpoint.Address}", ex));
                                    }
                                    ;
                                }
                            }

                            lock (syncLock)
                            {
                                if (connection.IsRegisteredInWheel && connection.TimeoutVersion == task.Version)
                                {
                                    connection.IsRegisteredInWheel = false;
                                    _ = Interlocked.Decrement(ref _registeredCount);
                                    connection.TimeoutVersion++;
                                    connection.ConnectionClosed -= this.OnConnectionClosed;
                                }

                                if (ReferenceEquals(connection.TimeoutTask, task))
                                {
                                    connection.TimeoutTask = null;
                                }
                            }

                            s_pool.Return(task);
                            task = next;
                            continue;
                        }

                        long remainingMs = currentTimeoutMs - idleMs;
                        long ticksMore = Math.Max(1, remainingMs / _tickMs);

                        int nextBucket = _useMask
                            ? (int)((tickToProcess + ticksMore) & _mask)
                            : (int)((tickToProcess + ticksMore) % _wheelSize);

                        lock (syncLock)
                        {
                            if (!connection.IsRegisteredInWheel || connection.TimeoutVersion != task.Version)
                            {
                                _ = Interlocked.Increment(ref _totalStaleSkips);
                                if (ReferenceEquals(connection.TimeoutTask, task))
                                {
                                    connection.TimeoutTask = null;
                                }

                                s_pool.Return(task);
                                task = next;
                                continue;
                            }

                            task.Rounds = (int)((ticksMore - 1) / _wheelSize);
                            task.Next = null;
                            task.Prev = null;
                            _wheel[nextBucket].Enqueue(task);
                        }

                        task = next;
                    }

                    ctx.Advance(1);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.TimingWheelBucket:Internal", "loop-error", ex));
            }
        }
    }

    #endregion Loop

    #region Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void OnConnectionClosed(object? sender, IConnectionEventArgs args)
    {
        if (args?.Connection is ITimeoutTrackedConnection conn)
        {
            this.Unregister(conn);
        }
    }

    /// <summary>
    /// Drains every wheel bucket and returns all tasks that have not yet been
    /// pool-reset (<c>Conn != null</c>) back to the pool.
    /// Called on <see cref="Deactivate"/> after the loop has been cancelled.
    /// </summary>
    private void DRAIN_AND_RELEASE_ALL_BUCKETS()
    {
        for (int i = 0; i < _wheel.Length; i++)
        {
            TimeoutTask? task = _wheel[i].DequeueAll();
            while (task is not null)
            {
                TimeoutTask? next = task.Next;

                // Only return tasks that still own a live connection reference.
                // Tasks already returned by the loop or a concurrent path have Conn
                // cleared, so they should be ignored here.
                if (task.Conn is not null)
                {
                    s_pool.Return(task);
                }

                task = next;
            }
        }
    }

    #endregion Helpers
}


