// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Extensions;
using Nalix.Runtime.Internal.Compilation;
using Nalix.Runtime.Internal.Diagnostics;
using Nalix.Runtime.Middleware;
using Nalix.Runtime.Routing;

namespace Nalix.Runtime.Dispatching;

/// <summary>
/// A lightweight packet dispatcher that executes handlers inline on the thread pool
/// without the overhead of a dedicated dispatch queue or background worker loops.
/// </summary>
[DebuggerNonUserCode]
[SkipLocalsInit]
public sealed class InlinePacketDispatcher
    : PacketDispatcherBase<IPacket>, IPacketDispatch, IActivatable
{
    private static readonly Internal.Diagnostics.ThrottleKey s_keyExecute = new("dispatch.execute");
    private static readonly Internal.Diagnostics.ThrottleKey s_keyExecutePrep = new("dispatch.execute_prep");

    private int _running;
    private long _deserializationErrors;

    /// <summary>
    /// Initializes a new instance of the <see cref="InlinePacketDispatcher"/> class.
    /// </summary>
    /// <param name="options">Option builder.</param>
    public InlinePacketDispatcher(Action<PacketDispatchOptions<IPacket>>? options = null) : base(options)
    {
    }

    /// <inheritdoc />
    [StackTraceHidden]
    public void Activate(CancellationToken cancellationToken = default) => Volatile.Write(ref _running, 1);

    /// <inheritdoc />
    [StackTraceHidden]
    public void Deactivate(CancellationToken cancellationToken = default) => Volatile.Write(ref _running, 0);

    /// <inheritdoc />
    public void Dispose() => this.Deactivate();

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    public void HandlePacket(IBufferLease lease, IConnection connection)
    {
        if (Volatile.Read(ref _running) == 0)
        {
            lease?.Dispose();
            return;
        }

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

        lease.Retain();

        // Queue to thread pool to avoid blocking the IO thread.
        // Uses UnsafeQueueUserWorkItem to bypass ExecutionContext flow for maximum performance.
        _ = ThreadPool.UnsafeQueueUserWorkItem(state =>
        {
            (InlinePacketDispatcher? dispatcher, IBufferLease? l, IConnection? c) = state;
            _ = dispatcher.ExecutePacketAsync(c, l, CancellationToken.None).AsTask();
        }, (this, lease, connection), preferLocal: true);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask ExecutePacketAsync(IConnection connection, IBufferLease lease, CancellationToken ct)
    {
        try
        {
            return this.ExecutePacketCoreAsync(connection, lease, ct);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Preparing the packet (opcode extraction, handler lookup, deserialization) runs on a thread-pool work
            // item with a retained lease. A malformed frame must not leak the lease or escape as an unhandled exception.
            connection.IncrementErrorCount();
            connection.ThrottledDiagnosticError(
                DiagnosticsEvents.Internal.Error,
                s_keyExecutePrep,
                "RT.InlinePacketDispatcher:ExecutePacketAsync",
                $"prepare-error ep={connection.NetworkEndpoint}",
                ex);
            lease.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private ValueTask ExecutePacketCoreAsync(IConnection connection, IBufferLease lease, CancellationToken ct)
    {
        // 1. Read the packet header directly from the raw span to determine routing
        bool isReliable = lease.IsReliable;
        bool encryptedOnWire = lease.EncryptedOnWire;
        ushort opcode = connection.PacketClassifier.Extract(lease.Span);

        // 2. Resolve the handler using the parsed opcode
        if (!this.Options.TryResolveHandler(opcode, out PacketHandler<IPacket> handler))
        {
            connection.ThrottledDiagnosticWarning(
                DiagnosticsEvents.Internal.Warning,
                s_keyExecute,
                "RT.InlinePacketDispatcher:ExecutePacketAsync",
                $"no-handler opcode={opcode}");

            lease.Dispose();
            connection.IncrementErrorCount();
            return ValueTask.CompletedTask;
        }

        IPacket packet;

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

        try
        {
            /*
             * [Packet Handler Execution]
             * 1. Attempt to execute the resolved handler.
             * 2. If it completes synchronously, we can dispose resources immediately.
             * 3. If it's asynchronous, we hand off to AwaitPacketHandlerCompletionAsync.
             */
            ValueTask pending = this.Options.ExecuteResolvedHandlerAsync(in handler, packet, connection, isReliable, encryptedOnWire, ct);

            // Fast-path: handler completed synchronously. The packet context already released the packet.
            if (pending.IsCompletedSuccessfully)
            {
                lease.Dispose();
                return ValueTask.CompletedTask;
            }

            // Slow-path: async completion (AwaitDispatchAsync handles Return/Dispose)
            return AwaitPacketHandlerCompletionAsync(connection, lease, pending, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // External cancellation during sync execution
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            connection.IncrementErrorCount();

            connection.ThrottledDiagnosticError(
                DiagnosticsEvents.Internal.Error,
                s_keyExecute,
                "RT.InlinePacketDispatcher:ExecutePacketAsync",
                $"handler-error ep={connection.NetworkEndpoint}");
        }

        // 5. Cleanup for synchronous errors/cancellation (the packet was already released by its context)
        lease.Dispose();
        return ValueTask.CompletedTask;
    }

    private static async ValueTask AwaitPacketHandlerCompletionAsync(
        IConnection connection,
        IBufferLease lease, ValueTask pending, CancellationToken ct)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Async cancellation
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            connection.IncrementErrorCount();
            connection.ThrottledDiagnosticError(
                DiagnosticsEvents.Internal.Error,
                s_keyExecute,
                "RT.InlinePacketDispatcher:ExecutePacketAsync",
                $"handler-error ep={connection.NetworkEndpoint}");
        }
        finally
        {
            // Guaranteed release for async path (the packet is released by its context)
            lease.Dispose();
        }
    }

    /// <inheritdoc/>
    [StackTraceHidden]
    public string GenerateReport()
    {
        StringBuilder sb = new(1024);
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}] InlinePacketDispatcher:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Running              : {(Volatile.Read(ref _running) == 1 ? "Yes" : "No")}");
        _ = sb.AppendLine();

        PipelineMetrics metrics = this.Options.Metrics;
        _ = sb.AppendLine("---------------------------------------------------------------------");
        _ = sb.AppendLine("Pipeline Statistics:");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Active Executions    : {metrics.ActiveExecutions}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Executions     : {metrics.TotalExecutions}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Total Errors         : {metrics.TotalErrors}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Deserialization Err  : {Volatile.Read(ref _deserializationErrors)}");
        _ = sb.AppendLine(CultureInfo.InvariantCulture, $"Average Time (ms)    : {metrics.AverageExecutionTime.TotalMilliseconds:F4}");

        ReadOnlySpan<PerMiddlewareMetrics> mwMetrics = this.Options.MiddlewareMetrics;
        if (mwMetrics.Length > 0)
        {
            _ = sb.AppendLine();
            _ = sb.AppendLine("Middleware Performance:");
            _ = sb.AppendLine("Middleware                           | Executions | Errors | Avg Time (ms)");
            _ = sb.AppendLine("-------------------------------------|------------|--------|--------------");
            foreach (ref readonly PerMiddlewareMetrics m in mwMetrics)
            {
                double avgMs = m.TotalExecutions == 0 ? 0 : TimeSpan.FromTicks(m.TotalExecutionTicks / m.TotalExecutions).TotalMilliseconds;
                _ = sb.AppendLine(CultureInfo.InvariantCulture, $"{m.MiddlewareType.Name,-36} | {m.TotalExecutions,-10} | {m.TotalErrors,-6} | {avgMs:F4}");
            }
        }

        return sb.ToString();
    }

    /// <inheritdoc/>
    public void WriteReportData(System.Text.Json.Utf8JsonWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStartObject();
        writer.WriteString("UtcNow", DateTime.UtcNow);
        writer.WriteBoolean("Running", Volatile.Read(ref _running) == 1);

        writer.WriteStartObject("PipelineMetrics");
        writer.WriteNumber("ActiveExecutions", this.Options.Metrics.ActiveExecutions);
        writer.WriteNumber("TotalExecutions", this.Options.Metrics.TotalExecutions);
        writer.WriteNumber("TotalErrors", this.Options.Metrics.TotalErrors);
        writer.WriteNumber("DeserializationErrors", Volatile.Read(ref _deserializationErrors));
        writer.WriteNumber("AverageTimeMs", this.Options.Metrics.AverageExecutionTime.TotalMilliseconds);
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
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }
}
