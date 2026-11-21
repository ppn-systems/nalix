// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Environment.Datagrams;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;

namespace Nalix.Network.Internal.Time;

/// <summary>
/// Millisecond timing wheel that drives the protocol timers (ACK delay, loss detection, pacing, probes, idle) of the
/// datagram sessions of one listener, instead of one <see cref="Timer"/> per session.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TimingWheel"/> tracks idle timeouts with a one-second tick and disconnects on expiry. Datagram
/// sessions need millisecond deadlines that change on every flush and a callback into the engine, so they get
/// their own wheel.
/// </para>
/// <para>
/// A target has at most one live deadline, <see cref="ITarget.ScheduledTick"/>. Rescheduling earlier enqueues a new
/// entry and leaves the old one in its bucket; an entry whose tick no longer matches is stale and dropped when its
/// bucket is processed. Deadlines more than one revolution away stay in their bucket until their tick comes.
/// Due targets run on the thread pool, so a slow session never delays the wheel.
/// </para>
/// </remarks>
[DebuggerDisplay("Tick={CurrentTick}, Running={IsRunning}")]
internal sealed class DatagramTimingWheel : IDisposable
{
    #region Constants

    /// <summary>Number of buckets: about one second per revolution at one millisecond per tick.</summary>
    private const int WheelSize = 1024;
    private const int Mask = WheelSize - 1;
    private const long MicrosPerTick = 1000;

    #endregion Constants

    #region Fields

    private readonly ConcurrentQueue<Entry>[] _buckets;
    private readonly string _name;
    private long _currentTick;
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed by Deactivate, which Dispose calls.")]
    private CancellationTokenSource? _cts;

    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "Disposed by Deactivate, which Dispose calls.")]
    private IWorkerHandle? _worker;
    private TaskCompletionSource _wakeup = NewSignal();
    private long _entries;
    private long _fired;
    private long _staleSkips;
    private long _iterations;

    #endregion Fields

    /// <summary>
    /// A session driven by the wheel. It is queued to the thread pool when its deadline passes.
    /// </summary>
    internal interface ITarget : IThreadPoolWorkItem
    {
        /// <summary>
        /// Gets the tick of the target's live deadline, or <see cref="long.MaxValue"/> when it has none (or is
        /// disposed). Entries for any other tick are stale.
        /// </summary>
        long ScheduledTick { get; }
    }

    /// <summary>
    /// Initializes a stopped wheel.
    /// </summary>
    /// <param name="name">Name of the worker, for diagnostics.</param>
    public DatagramTimingWheel(string name)
    {
        _name = name;
        _buckets = new ConcurrentQueue<Entry>[WheelSize];
        for (int i = 0; i < _buckets.Length; i++)
        {
            _buckets[i] = new ConcurrentQueue<Entry>();
        }

        _currentTick = NowTick();
    }

    #region Properties

    /// <summary>Gets the last processed tick.</summary>
    public long CurrentTick => Volatile.Read(ref _currentTick);

    /// <summary>Gets a value indicating whether the wheel worker is running.</summary>
    public bool IsRunning => _worker?.IsRunning ?? false;

    /// <summary>Gets the number of deadlines that fired.</summary>
    public long FiredCount => Volatile.Read(ref _fired);

    /// <summary>Gets the number of loop iterations; the loop sleeps while no entry is scheduled.</summary>
    internal long Iterations => Volatile.Read(ref _iterations);

    /// <summary>Gets the number of entries in the buckets, live or stale.</summary>
    internal long EntryCount => Volatile.Read(ref _entries);

    /// <summary>Gets the number of stale entries dropped.</summary>
    public long StaleSkipCount => Volatile.Read(ref _staleSkips);

    #endregion Properties

    #region Methods

    /// <summary>
    /// Starts the wheel worker.
    /// </summary>
    /// <param name="cancellationToken">Stops the wheel when cancelled.</param>
    public void Activate(CancellationToken cancellationToken)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _currentTick, NowTick());

        // A previous Deactivate may have cancelled the signal.
        Volatile.Write(ref _wakeup, NewSignal());

        _worker = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleWorker(
            name: _name,
            group: $"{TaskNaming.Tags.Net}/{TaskNaming.Tags.Datagram}",
            work: this.RunAsync,
            options: new WorkerOptions
            {
                Tag = TaskNaming.Tags.Net,
                IdType = SnowflakeType.System,
                CancellationToken = _cts.Token,
                RetainFor = TimeSpan.Zero,
            });
    }

    /// <summary>
    /// Converts a deadline of <see cref="DatagramEngine.NowMicros"/> into a tick of this wheel, never earlier
    /// than the next tick. Pass the result unchanged to <see cref="Schedule"/> and keep it as
    /// <see cref="ITarget.ScheduledTick"/>.
    /// </summary>
    public long ToTick(long deadlineMicros)
    {
        long tick = (deadlineMicros + MicrosPerTick - 1) / MicrosPerTick;
        long earliest = Volatile.Read(ref _currentTick) + 1;
        return tick < earliest ? earliest : tick;
    }

    /// <summary>
    /// Queues <paramref name="target"/> for <paramref name="tick"/>. The caller sets its
    /// <see cref="ITarget.ScheduledTick"/> to <paramref name="tick"/> before calling.
    /// </summary>
    public void Schedule(ITarget target, long tick)
    {
        _buckets[(int)(tick & Mask)].Enqueue(new Entry(target, tick));

        // The first entry wakes an idle wheel (see RunAsync).
        if (Interlocked.Increment(ref _entries) == 1)
        {
            _ = Volatile.Read(ref _wakeup).TrySetResult();
        }
    }

    /// <summary>
    /// Stops the wheel worker and drops every deadline. The wheel can be activated again.
    /// </summary>
    public void Deactivate()
    {
        CancellationTokenSource? cts = Interlocked.Exchange(ref _cts, null);
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        Interlocked.Exchange(ref _worker, null)?.Dispose();
        cts.Dispose();

        foreach (ConcurrentQueue<Entry> bucket in _buckets)
        {
            bucket.Clear();
        }

        _ = Interlocked.Exchange(ref _entries, 0);
    }

    /// <summary>Stops the wheel.</summary>
    public void Dispose() => this.Deactivate();

    #endregion Methods

    #region Loop

    private async ValueTask RunAsync(IWorkerContext ctx, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1));

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                // Nothing scheduled: sleep until Schedule adds an entry instead of ticking 1000 times a second.
                // The ticks missed meanwhile are processed by Advance below (every bucket at most once).
                if (Volatile.Read(ref _entries) == 0)
                {
                    await this.SleepUntilScheduledAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                ctx.Beat();
                _ = Interlocked.Increment(ref _iterations);
                this.Advance(NowTick());
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                    new DiagnosticLog("NW.DatagramTimingWheel:Run", $"loop-faulted name={_name}", ex));
            }

            throw;
        }
    }

    /// <summary>
    /// Waits until <see cref="Schedule"/> adds the first entry, or the wheel stops.
    /// </summary>
    /// <remarks>
    /// A TaskCompletionSource rather than SemaphoreSlim.WaitAsync: on net11.0 (runtime async) a cancelled
    /// SemaphoreSlim.WaitAsync was seen never completing, which left the wheel worker, and with it the host's
    /// shutdown, waiting forever.
    /// </remarks>
    private async Task SleepUntilScheduledAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource signal = Volatile.Read(ref _wakeup);

        // Re-check after taking the signal: an entry added before it would not set it.
        if (Volatile.Read(ref _entries) == 0)
        {
            using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
                static state => ((TaskCompletionSource)state!).TrySetCanceled(), signal);
            await signal.Task.ConfigureAwait(false);
        }

        // Arm a fresh signal for the next sleep.
        _ = Interlocked.CompareExchange(ref _wakeup, NewSignal(), signal);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// Processes every tick up to <paramref name="now"/>. Called by the wheel worker; tests call it directly.
    /// </summary>
    internal void Advance(long now)
    {
        long tick = Volatile.Read(ref _currentTick);

        // After a long stall every bucket is visited once; entries keep their own tick, so none is lost.
        if (now - tick > WheelSize)
        {
            tick = now - WheelSize;
        }

        while (tick < now)
        {
            tick++;

            // Publish the tick before processing its bucket: ToTick then returns tick + 1 at the earliest, so an
            // entry scheduled during processing never lands behind the bucket's snapshot (which would delay it
            // by a full revolution).
            Volatile.Write(ref _currentTick, tick);
            this.ProcessBucket(tick, now);
        }
    }

    private void ProcessBucket(long tick, long now)
    {
        ConcurrentQueue<Entry> bucket = _buckets[(int)(tick & Mask)];

        // Only the entries present now: re-queued entries (later revolutions) wait for their own tick.
        for (int count = bucket.Count; count > 0 && bucket.TryDequeue(out Entry entry); count--)
        {
            if (entry.Target.ScheduledTick != entry.Tick)
            {
                _ = Interlocked.Increment(ref _staleSkips);
                _ = Interlocked.Decrement(ref _entries);
            }
            else if (entry.Tick <= now)
            {
                _ = Interlocked.Increment(ref _fired);
                _ = Interlocked.Decrement(ref _entries);
                _ = ThreadPool.UnsafeQueueUserWorkItem(entry.Target, preferLocal: false);
            }
            else
            {
                bucket.Enqueue(entry);
            }
        }
    }

    private static long NowTick() => DatagramEngine.NowMicros() / MicrosPerTick;

    #endregion Loop

    private readonly record struct Entry(ITarget Target, long Tick);
}
