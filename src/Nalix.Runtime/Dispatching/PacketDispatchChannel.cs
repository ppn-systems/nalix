// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Extensions;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Internal.Diagnostics;
using Nalix.Runtime.Internal.Routing;
using Nalix.Runtime.Middleware;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Dispatching;

/// <summary>
/// High-performance packet dispatch channel with coalesced wake-up signaling.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
[DebuggerDisplay("Running={_running}, Pending={_dispatch.TotalPackets}")]
public sealed class PacketDispatchChannel
    : PacketDispatcherBase<IPacket>, IPacketDispatch, IDisposable, IActivatable
{
    #region Fields

    private static readonly Internal.Diagnostics.ThrottleKey s_keyExecute = new("dispatch.execute");
    private static readonly Internal.Diagnostics.ThrottleKey s_keyExecutePrep = new("dispatch.execute_prep");

    private readonly DispatchChannel<IPacket> _dispatch;

    private readonly int _maxDrainPerWake;

    // One reusable, allocation-free wake signal per worker, plus a per-worker "parked" flag
    // (1 = registered idle and eligible to be woken). Sized in Activate().
    private WorkerWakeSignal[] _wakeSignal = [];
    private int[] _parked = [];
    private int _wakeCursor;

    private int _running;
    private int _activeLoops;
    private int _dispatchLoops;
    private long _idleWorkers;
    private long _deserializationErrors;
    private long _totalDropped;

    private long _wakeSignals;
    private long _wakeReadSignals;

    private IWorkerHandle[] _workerHandle = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _linkedCts;

    #endregion Fields

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="PacketDispatchChannel"/> class.
    /// </summary>
    /// <param name="options">Option builder.</param>
    public PacketDispatchChannel(Action<PacketDispatchOptions<IPacket>> options) : base(options)
    {
        _dispatch = new DispatchChannel<IPacket>();
        _maxDrainPerWake = Math.Clamp(System.Environment.ProcessorCount * this.Options.Drain.MaxDrainPerWakeMultiplier, this.Options.Drain.MinDrainPerWake, this.Options.Drain.MaxDrainPerWake);
    }

    #endregion Constructors

    #region Public Methods

    /// <summary>
    /// Starts background dispatch workers.
    /// </summary>
    /// <param name="cancellationToken">External cancellation token.</param>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Activate(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        _linkedCts?.Dispose();
        _linkedCts = null;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();

        CancellationToken linkedToken;
        if (cancellationToken.CanBeCanceled)
        {
            _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);
            linkedToken = _linkedCts.Token;
        }
        else
        {
            linkedToken = _cts.Token;
        }

        _ = Interlocked.Exchange(ref _idleWorkers, 0);
        _ = Interlocked.Exchange(ref _wakeSignals, 0);
        _ = Interlocked.Exchange(ref _wakeReadSignals, 0);

        Volatile.Write(ref _activeLoops, 0);

        int[]? affinityCores = ParseProcessorAffinities(this.Options.Drain.DispatchProcessorAffinities);

        if (affinityCores is not null && affinityCores.Length > 0)
        {
            _dispatchLoops = affinityCores.Length;
        }
        else
        {
            _dispatchLoops = this.Options.Drain.Count > 0
                ? this.Options.Drain.Count
                : Math.Clamp(System.Environment.ProcessorCount, this.Options.Drain.MinDispatchLoops, this.Options.Drain.MaxDispatchLoops);
        }

        WorkerWakeSignal[] signals = new WorkerWakeSignal[_dispatchLoops];
        for (int i = 0; i < signals.Length; i++)
        {
            signals[i] = new WorkerWakeSignal();
        }

        _parked = new int[_dispatchLoops];
        _wakeSignal = signals;
        _wakeCursor = 0;

        _workerHandle = new IWorkerHandle[_dispatchLoops];
        CancellationToken linkedTokenRef = linkedToken;

        for (int i = 0; i < _dispatchLoops; i++)
        {
            _ = Interlocked.Increment(ref _activeLoops);

            int loopIndex = i;
            _workerHandle[i] = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleWorker(
                name: $"{TaskNaming.Tags.Dispatch}.{TaskNaming.Tags.Process}.{i}",
                group: $"{TaskNaming.Tags.Net}/{TaskNaming.Tags.Dispatch}",
                work: async (ctx, ct) =>
                {
                    // This is now a truly asynchronous worker managed by InstanceManager.Instance.GetOrCreateInstance<TaskManager>().
                    // By removing the manual OS thread, we reduce context switching and 
                    // allow .NET's thread pool to optimize the execution of the async state machine.
                    // NOTE: If DispatchProcessorAffinities is configured, this worker will be 
                    // upgraded to a dedicated OS thread for core pinning.
                    await this.DispatchWorkerLoopAsync(ctx, ct, loopIndex).ConfigureAwait(false);
                },
                options: new WorkerOptions
                {
                    RetainFor = TimeSpan.Zero,
                    Tag = TaskNaming.Tags.Net,
                    IdType = SnowflakeType.System,
                    CancellationToken = linkedTokenRef,
                    Priority = WorkerPriority.HIGH,
                    OSPriority = affinityCores is not null ? ThreadPriority.Normal : null,
                    ProcessorAffinity = affinityCores?[i % affinityCores.Length]
                });
        }

        this.RequestWake();
    }

    /// <summary>
    /// Stops dispatch workers.
    /// </summary>
    /// <param name="cancellationToken">Unused optional token for compatibility.</param>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public void Deactivate(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _running, 0) == 0)
        {
            return;
        }

        CancellationTokenSource? localCts = Interlocked.Exchange(ref _cts, null);
        CancellationTokenSource? linkedCts = Interlocked.Exchange(ref _linkedCts, null);

        try
        {
            for (int i = 0; i < _dispatchLoops; i++)
            {
                if (i < _workerHandle.Length && _workerHandle[i] is not null)
                {
                    _workerHandle[i].Dispose();
                }
            }

            if (localCts is { IsCancellationRequested: false })
            {
                localCts.Cancel();
            }

            // Wake every worker so it observes _running == 0 / cancellation and exits.
            WorkerWakeSignal[] signals = _wakeSignal;
            for (int i = 0; i < signals.Length; i++)
            {
                _ = signals[i].Set();
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("RT.PacketDispatchChannel:Deactivate", "deactivate-error", ex));
            }
        }
        finally
        {
            try
            {
                linkedCts?.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("RT.PacketDispatchChannel:Deactivate", "linked-cts-dispose-failed", ex));
                }
            }

            try
            {
                localCts?.Dispose();
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(
                        DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("RT.PacketDispatchChannel:Deactivate", "local-cts-dispose-failed", ex));
                }
            }
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void HandlePacket(IBufferLease lease, IConnection connection)
    {
        if (lease is null || connection is null)
        {
            lease?.Dispose();
            return;
        }

        if (connection.TCP.Framing == TransportFraming.UInt16LengthPrefixed && (uint)lease.Length < PacketConstants.HeaderSize)
        {
            lease.Dispose();
            connection.IncrementErrorCount();
            return;
        }

        /*
         * [Asynchronous Handoff & Reference Management]
         * The transport layer owns the buffer until this call returns. 
         * To safely process the packet asynchronously in a worker thread, 
         * we MUST take an additional reference (Retain).
         */
        lease.Retain();

        if (!_dispatch.PushCore(connection, lease, out bool readyEmitted, noBlock: true))
        {
            // If the channel is full or the connection is inactive, we must
            // release the reference we just took to avoid a memory leak.
            lease.Dispose();
            _ = Interlocked.Increment(ref _totalDropped);
            return;
        }

        if (readyEmitted)
        {
            // Signal a worker to wake up and process the newly queued packet.
            this.RequestWake();
        }
    }

    #endregion Public Methods

    #region IReportable

    /// <summary>
    /// Generates a human-readable diagnostic report.
    /// </summary>
    /// <returns>Formatted report.</returns>
    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public string GenerateReport()
    {
        StringBuilder sb = new(2048);
        _ = this.Options.Metrics;
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] PacketDispatchChannel:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Running              : {(Volatile.Read(ref _running) == 1 ? "Yes" : "No")}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"DispatchLoops        : {_dispatchLoops}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"WakeSignals          : {Interlocked.Read(ref _wakeSignals)}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"WakeReads            : {Interlocked.Read(ref _wakeReadSignals)}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"IdleWorkers          : {Volatile.Read(ref _idleWorkers)}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("---------------------------------------------------------------------");
        _ = sb.AppendLine("Channel Statistics:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Packets        : {_dispatch.TotalPackets}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Peak Packets         : {_dispatch.PeakPackets}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Connections    : {_dispatch.TotalConnections}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Ready Connections    : {_dispatch.ReadyConnections}");
        _ = sb.AppendLine();

        _ = sb.AppendLine("Pending by Priority:");
        _ = sb.AppendLine("Priority          | Pending Connections");
        _ = sb.AppendLine("------------------|---------------------");
        Span<int> priorities = stackalloc int[(int)PacketPriority.URGENT + 1];
        _dispatch.CopyPendingPerPriority(priorities);
        for (int p = priorities.Length - 1; p >= 0; p--)
        {
            _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{GetPriorityName(p),-18}| {priorities[p],-19}");
        }

        _ = sb.AppendLine();
        _ = sb.AppendLine("---------------------------------------------------------------------");
        _ = sb.AppendLine("Pipeline Statistics:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Active Executions    : {this.Options.Metrics.ActiveExecutions}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Executions     : {this.Options.Metrics.TotalExecutions}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Dropped        : {Volatile.Read(ref _totalDropped) + _dispatch.TotalEvicted}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Errors         : {this.Options.Metrics.TotalErrors}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Deserialization Err : {Volatile.Read(ref _deserializationErrors)}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Average Time (ms)    : {this.Options.Metrics.AverageExecutionTime.TotalMilliseconds:F4}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Max Time (ms)        : {TimeSpan.FromTicks(this.Options.Metrics.MaxExecutionTicks).TotalMilliseconds:F4}");
        _ = this.Options.MiddlewareMetrics;
        if (this.Options.MiddlewareMetrics.Length > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("Middleware Performance:");
            _ = sb.AppendLine("Middleware                           | Executions | Errors | Avg Time (ms) | Max Time (ms)");
            _ = sb.AppendLine("-------------------------------------|------------|--------|---------------|--------------");
            foreach (ref readonly PerMiddlewareMetrics m in this.Options.MiddlewareMetrics)
            {
                double avgMs = m.TotalExecutions == 0 ? 0 : TimeSpan.FromTicks(m.TotalExecutionTicks / m.TotalExecutions).TotalMilliseconds;
                double maxMs = TimeSpan.FromTicks(m.MaxExecutionTicks).TotalMilliseconds;
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{m.MiddlewareType.Name,-36} | {m.TotalExecutions,-10} | {m.TotalErrors,-6} | {avgMs,-13:F4} | {maxMs:F4}");
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        PipelineMetrics metrics = this.Options.Metrics;

        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteBoolean("Running", Volatile.Read(ref _running) == 1);
        writer.WriteNumber("DispatchLoops", _dispatchLoops);
        writer.WriteNumber("TotalPackets", _dispatch.TotalPackets);
        writer.WriteNumber("PeakPackets", _dispatch.PeakPackets);
        writer.WriteNumber("TotalConnections", _dispatch.TotalConnections);
        writer.WriteNumber("ReadyConnections", _dispatch.ReadyConnections);
        writer.WriteNumber("WakeSignals", Interlocked.Read(ref _wakeSignals));
        writer.WriteNumber("WakeReads", Interlocked.Read(ref _wakeReadSignals));
        writer.WriteNumber("IdleWorkers", Volatile.Read(ref _idleWorkers));
        writer.WriteString("PacketRegistryType", nameof(PacketRegistry));

        Span<int> priorities = stackalloc int[(int)PacketPriority.URGENT + 1];
        _dispatch.CopyPendingPerPriority(priorities);
        writer.WriteStartObject("PendingPerPriority");
        for (int p = priorities.Length - 1; p >= 0; p--)
        {
            writer.WriteNumber(GetPriorityName(p), priorities[p]);
        }
        writer.WriteEndObject();

        writer.WriteStartObject("PipelineMetrics");
        writer.WriteNumber("ActiveExecutions", this.Options.Metrics.ActiveExecutions);
        writer.WriteNumber("TotalExecutions", this.Options.Metrics.TotalExecutions);
        writer.WriteNumber("TotalDropped", Volatile.Read(ref _totalDropped) + _dispatch.TotalEvicted);
        writer.WriteNumber("TotalErrors", this.Options.Metrics.TotalErrors);
        writer.WriteNumber("DeserializationErrors", Volatile.Read(ref _deserializationErrors));
        writer.WriteNumber("AverageTimeMs", this.Options.Metrics.AverageExecutionTime.TotalMilliseconds);
        writer.WriteNumber("MaxTimeMs", TimeSpan.FromTicks(this.Options.Metrics.MaxExecutionTicks).TotalMilliseconds);
        writer.WriteEndObject();

        if (this.Options.MiddlewareMetrics.Length > 0)
        {
            writer.WriteStartArray("MiddlewareMetrics");
            foreach (ref readonly PerMiddlewareMetrics m in this.Options.MiddlewareMetrics)
            {
                writer.WriteStartObject();
                writer.WriteString("Type", m.MiddlewareType.Name);
                writer.WriteNumber("TotalExecutions", m.TotalExecutions);
                writer.WriteNumber("TotalErrors", m.TotalErrors);
                double avgMs = m.TotalExecutions == 0 ? 0 : TimeSpan.FromTicks(m.TotalExecutionTicks / m.TotalExecutions).TotalMilliseconds;
                writer.WriteNumber("AverageTimeMs", avgMs);
                writer.WriteNumber("MaxTimeMs", TimeSpan.FromTicks(m.MaxExecutionTicks).TotalMilliseconds);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string GetPriorityName(int index)
    {
        string? value = Enum.GetName(typeof(PacketPriority), index);
        return value ?? index.ToString(CultureInfo.InvariantCulture);
    }

    #endregion IReportable

    #region Private Methods

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static int[]? ParseProcessorAffinities(string? config)
    {
        if (string.IsNullOrWhiteSpace(config))
        {
            return null;
        }

        string[] parts = config.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<int> parsedCores = new(parts.Length);
        foreach (string p in parts)
        {
            if (int.TryParse(p, out int core) && core >= 0)
            {
                parsedCores.Add(core);
            }
        }

        return parsedCores.Count > 0 ? [.. parsedCores] : null;
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private async ValueTask DispatchWorkerLoopAsync(IWorkerContext ctx, CancellationToken ct, int index)
    {
        // WORK-STEALING / ACTOR-MAILBOX LOOP.
        // Each worker claims an entire connection's mailbox (session),
        // processes a batch of packets, then releases the claim.
        // This guarantees strict per-connection ordering while distributing
        // load evenly across all workers.

        // Snapshot this generation's signal/flag arrays: a later Activate() replaces the fields.
        WorkerWakeSignal signal = _wakeSignal[index];
        int[] parked = _parked;

        // Cancellation must wake a parked worker (the old 50 ms timed wait used to poll it).
        // One registration per worker lifetime, not per wait.
        using CancellationTokenRegistration ctReg = ct.UnsafeRegister(
            static s => _ = ((WorkerWakeSignal)s!).Set(), signal);

        try
        {
            // Loop while work is available, with occasional yields to InstanceManager.Instance.GetOrCreateInstance<TaskManager>().
            while (Volatile.Read(ref _running) == 1 && !ct.IsCancellationRequested)
            {
                int processed = 0;

                if (_dispatch.TryClaim(out IDispatchSession? session))
                {
                    // More connections are waiting to be claimed: hand one to a parked worker
                    // so a burst fans out instead of being drained serially by this worker.
                    if (_dispatch.HasClaimableConnection)
                    {
                        this.RequestWake();
                    }

                    try
                    {
#pragma warning disable CA2000
                        /*
                         * [The Hot Loop: Draining a Claimed Connection]
                         * We drain up to _maxDrainPerWake packets from a single
                         * connection's mailbox. The session holds exclusive rights,
                         * so no other worker can touch this connection.
                         */
                        while (processed < _maxDrainPerWake &&
                               session.TryDequeue(out IBufferLease? lease))
                        {
                            try
                            {
                                // Dispatch directly using await.
                                // Zero-allocation for sync completion; Pooled for async.
                                await this.ExecutePacketAsync(session.Connection, lease, ct).ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                                {
                                    DiagnosticsEvents.Write(
                                        DiagnosticsEvents.Internal.Error,
                                        new DiagnosticLog("RT.PacketDispatchChannel:DispatchWorkerLoopAsync", "dispatch-execution-error", ex));
                                }
                            }
                            processed++;
                        }
#pragma warning restore CA2000
                    }
                    finally
                    {
                        // Release the session: re-enqueues the connection if packets remain.
                        session.Dispose();
                    }
                }

                if (processed > 0)
                {
                    ctx.Advance(processed);

                    // If we hit the batch cap, immediately try to claim another session
                    // to keep draining without sleeping.
                    if (processed >= _maxDrainPerWake)
                    {
                        continue;
                    }
                }

                // Signal that this worker is about to go idle.
                _ = Interlocked.Increment(ref _idleWorkers);

                try
                {
                    /*
                     * [Wait Strategy: Parked Worker + Per-Worker Wake Signal]
                     * Publish "parked" (full fence via Interlocked) BEFORE re-checking the ready
                     * queues. A producer publishes the ready entry (Interlocked) BEFORE reading the
                     * parked flags in RequestWake(). With both sides fenced, either this worker sees
                     * the new work, or the producer sees this worker parked and sets its signal, so
                     * a wake cannot be lost and no timer poll is needed.
                     */
                    _ = Interlocked.Exchange(ref parked[index], 1);

                    int spins = 0;
                    while (!_dispatch.HasClaimableConnection && spins < 16)
                    {
                        Thread.SpinWait(8);
                        spins++;
                    }

                    if (_dispatch.HasClaimableConnection ||
                        Volatile.Read(ref _running) == 0 || ct.IsCancellationRequested)
                    {
                        // Work (or shutdown) showed up: un-park without sleeping. If a producer
                        // already claimed our flag it also set our signal; drop that stale wake.
                        if (Interlocked.Exchange(ref parked[index], 0) == 0)
                        {
                            signal.Clear();
                        }
                    }
                    else
                    {
                        // Allocation-free wait; completes synchronously if already signaled.
                        await signal.WaitAsync().ConfigureAwait(false);
                        _ = Interlocked.Increment(ref _wakeReadSignals);
                        _ = Interlocked.Exchange(ref parked[index], 0);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }
                }
                finally
                {
                    // Whether we were woken or found work, we are no longer idle.
                    DecrementNonNegative(ref _idleWorkers);
                }

                ctx.Beat();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("RT.PacketDispatchChannel:DispatchWorkerLoopAsync", $"fatal-loop-error index={index}", ex));
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _activeLoops) == 0)
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }


    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask ExecutePacketAsync(IConnection connection, IBufferLease lease, CancellationToken ct)
    {
        ushort opcode;
        IPacket packet;
        bool isReliable;
        bool encryptedOnWire;
        PacketHandler<IPacket> handler;

        try
        {
            // 1. Read the packet header directly from the raw span to determine routing
            isReliable = lease.IsReliable;
            encryptedOnWire = lease.EncryptedOnWire;
            opcode = connection.PacketClassifier.Extract(lease.Span);

            // 2. Resolve the handler using the parsed opcode
            if (!this.Options.TryResolveHandler(opcode, out handler))
            {
                connection.ThrottledDiagnosticWarning(
                    DiagnosticsEvents.Internal.Warning,
                    s_keyExecute,
                    "RT.PacketDispatchChannel:ExecutePacketAsync",
                    $"no-handler opcode={opcode}");

                lease.Dispose();
                connection.IncrementErrorCount();
                return ValueTask.CompletedTask;
            }

            // 3. Bypass deserialization if the handler expects raw memory
            if (handler.ExpectedPacketType == typeof(MemoryPacket))
            {
                // HACK: [Technical Debt] Initialize a "dummy" PacketHeader containing only OpCode
                // to satisfy the MemoryPacket constructor for routing.
                // WARNING: Other attributes such as SequenceId and Flags in this Header will have default values ​​(0).
                // Middleware (RateLimit, Timeout, Concurrency, etc.) must be cautious when reading data from Packet.Header.
                // TODO: In the future, if the system requires stricter QoS control, IOpCodeExtractor should be upgraded to IHeaderParser.

                if (lease.Length >= PacketConstants.HeaderSize)
                {
                    PacketHeader header = HeaderExtensions.AsHeaderRef(lease.Span);

                    header.OpCode = opcode;
                    packet = new MemoryPacket(lease.Memory, header);
                }
                else
                {
                    packet = new MemoryPacket(lease.Memory, new PacketHeader { OpCode = opcode });
                }
            }
            else
            {
                // 4. Normal deserialization fallback for structured packets
                if (!PacketRegistry.TryDeserialize(lease.Span, out IPacket? deserialized) || deserialized is null)
                {
                    _ = Interlocked.Increment(ref _deserializationErrors);
                    lease.Dispose();
                    connection.IncrementErrorCount();
                    return ValueTask.CompletedTask;
                }

                packet = deserialized;
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            connection.IncrementErrorCount();
            connection.ThrottledDiagnosticError(
                DiagnosticsEvents.Internal.Error,
                s_keyExecutePrep,
                "RT.PacketDispatchChannel:ExecutePacketAsync",
                $"prepare-error ep={connection.NetworkEndpoint}",
                ex);
            lease.Dispose();
            return ValueTask.CompletedTask;
        }

        try
        {
            /*
             * [Packet Handler Execution]
             * 1. Attempt to execute the resolved handler.
             * 2. If it completes synchronously, we can dispose resources immediately.
             * 3. If it's asynchronous, we hand off to AwaitPacketHandlerCompletionAsync.
             */
            ValueTask pending = this.Options.ExecuteResolvedHandlerAsync(in handler, packet, connection, isReliable, encryptedOnWire, ct);

            // Fast-path: handler completed synchronously
            if (pending.IsCompletedSuccessfully)
            {
                if (packet is IDisposable disposable)
                {
                    disposable.Dispose();
                }

                lease.Dispose();
                return ValueTask.CompletedTask;
            }

            // Slow-path: async completion
            // The handler returned an incomplete task (e.g. awaited I/O).
            // To prevent blocking the DispatchWorkerLoop, we offload the continuation to the ThreadPool.
            // Using a pooled IThreadPoolWorkItem ensures 0-byte allocation.
            DispatchContinuation workItem = ObjectPoolManager.Shared.Get<DispatchContinuation>();
            workItem.Initialize(connection, lease, packet, pending.AsTask(), ct);

            _ = ThreadPool.UnsafeQueueUserWorkItem(workItem, preferLocal: false);

            return ValueTask.CompletedTask;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation during sync execution
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            try
            {
                if (!connection.IsDisposed)
                {
                    connection.IncrementErrorCount();

                    connection.ThrottledDiagnosticError(
                        DiagnosticsEvents.Internal.Error,
                        s_keyExecute,
                        "RT.PacketDispatchChannel:ExecutePacketAsync",
                        $"handler-error ep={connection.NetworkEndpoint}");
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore ObjectDisposedException since the connection is going away
            }
        }

        // 5. Cleanup for synchronous errors/cancellation
        if (packet is IDisposable disposableSync)
        {
            disposableSync.Dispose();
        }

        lease.Dispose();
        return ValueTask.CompletedTask;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequestWake()
    {
        // Fast exit when nobody is parked (the common case under sustained load).
        // _idleWorkers is incremented (full fence) before a worker re-checks the ready queues.
        if (Volatile.Read(ref _idleWorkers) <= 0)
        {
            return;
        }

        int[] parked = _parked;
        WorkerWakeSignal[] signals = _wakeSignal;
        int n = Math.Min(parked.Length, signals.Length);
        if (n == 0)
        {
            return;
        }

        // Wake exactly one parked worker per ready emission. Start the scan at a rotating
        // cursor so wake-ups are spread across workers instead of always hitting worker 0.
        int start = (int)((uint)Interlocked.Increment(ref _wakeCursor) % (uint)n);
        for (int k = 0; k < n; k++)
        {
            int i = start + k;
            if (i >= n)
            {
                i -= n;
            }

            if (Volatile.Read(ref parked[i]) == 1 &&
                Interlocked.CompareExchange(ref parked[i], 0, 1) == 1)
            {
                _ = signals[i].Set();
                _ = Interlocked.Increment(ref _wakeSignals);
                return;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecrementNonNegative(ref long value)
    {
        long after = Interlocked.Decrement(ref value);
        if (after < 0)
        {
            _ = Interlocked.Exchange(ref value, 0);
        }
    }

    #endregion Private Methods

    #region IDisposable

    /// <summary>
    /// Releases dispatcher resources.
    /// </summary>
    [DebuggerNonUserCode]
    public void Dispose()
    {
        this.Deactivate();
        _dispatch.Dispose();
        _linkedCts?.Dispose();
        _cts?.Dispose();
    }

    #endregion IDisposable
}
