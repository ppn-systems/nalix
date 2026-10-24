// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;

namespace Nalix.Network.Internal.Transport;

internal sealed partial class SocketConnection
{
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task SAEA_RECEIVE_LOOP_VARINT_ASYNC(CancellationToken token)
    {
        // Identifies the connection this loop serves: the instance is pooled, and ResetForPool advances the generation.
        int generation = Volatile.Read(ref _generation);

        try
        {
            while (generation == Volatile.Read(ref _generation) && Volatile.Read(ref _disposed) == 0 && Volatile.Read(ref _cancelSignaled) == 0 && !token.IsCancellationRequested)
            {
                int consumed = 0;
                bool incomplete = false;
                int? pendingFrameSize = null;
                bool parsedAtLeastOne = false;
                bool protocolViolation = false;

                while (_bufferDataLength - consumed > 0)
                {
                    // 1. Attempt to read the VarInt header.
                    ReadOnlySpan<byte> currentBuffer = MemoryExtensions.AsSpan(_buffer!, consumed, _bufferDataLength - consumed);
                    if (!Leb128.TryRead(currentBuffer, out int payloadLen, out int varIntBytesRead))
                    {
                        // Incomplete VarInt header. Wait for more data.
                        incomplete = true;
                        break;
                    }

                    if (payloadLen < 0 || payloadLen > _maxVarIntPayloadSize)
                    {
                        if (_owner is IConnectionTrafficMetrics trafficMetrics)
                        {
                            trafficMetrics.IncrementPacketsDropped();
                        }

                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                        {
                            if (Security.ThrottledEventGate.TryAcquire(ref s_protocolViolationTicks, ref s_protocolViolationSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
                            {
                                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                                {
                                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"invalid varint payload length, closing connection payload-length={payloadLen} endpoint={_owner.NetworkEndpoint.Address} suppressed-count={suppressed}"));
                                }
                            }
                        }

                        protocolViolation = true;
                        break;
                    }

                    int totalFrameSize = varIntBytesRead + payloadLen;

                    // 2. Check if the full frame is in the buffer.
                    if (_bufferDataLength - consumed < totalFrameSize)
                    {
                        pendingFrameSize = totalFrameSize;
                        break;
                    }

                    // 3. We have the full frame. Dispatch it.
                    this.PROCESS_VARINT_FRAME_FROM_BUFFER(consumed + varIntBytesRead, payloadLen);

                    consumed += totalFrameSize;
                    parsedAtLeastOne = true;
                }

                // Invariant: every inner-loop exit either dispatched+advanced consumed,
                // recorded a legitimate wait (pendingFrameSize / incomplete header), or
                // flagged a protocol violation. It must never silently stall.
                Debug.Assert(
                    protocolViolation || pendingFrameSize.HasValue || incomplete ||
                    _bufferDataLength - consumed <= 0 || parsedAtLeastOne,
                    "SAEA varint receive loop inner exit with no progress and no recorded wait/violation state.");

                if (protocolViolation)
                {
                    break;
                }

                // Buffer Compaction
                if (consumed > 0)
                {
                    int remaining = _bufferDataLength - consumed;
                    if (remaining > 0)
                    {
                        Buffer.BlockCopy(_buffer!, consumed, _buffer!, 0, remaining);
                    }

                    _bufferDataLength = remaining;
                }

                // Elastic Resizing
                if (pendingFrameSize.HasValue)
                {
                    int requiredSize = pendingFrameSize.Value;

                    if (requiredSize > s_maxReceiveBufferSize)
                    {
                        throw Throw.GetMessageSize();
                    }

                    if (requiredSize > _buffer!.Length)
                    {
                        byte[] newBuffer = BufferLease.ByteArrayPool.Rent(requiredSize);

                        if (_bufferDataLength > 0)
                        {
                            Buffer.BlockCopy(_buffer, 0, newBuffer, 0, _bufferDataLength);
                        }

                        BufferLease.ByteArrayPool.Return(_buffer);
                        _buffer = newBuffer;
                    }
                }
                else if (_bufferDataLength == 0 && _buffer!.Length > s_fragmentOptions.MinReceiveBufferSize)
                {
                    BufferLease.ByteArrayPool.Return(_buffer);
                    _buffer = BufferLease.ByteArrayPool.Rent(s_fragmentOptions.MinReceiveBufferSize);
                }

                // Opportunistic Read
                if (!parsedAtLeastOne || _bufferDataLength == 0 || incomplete)
                {
                    ReceiveResult receiveResult = await this.RECEIVE_OPPORTUNISTIC_ASYNC(token).ConfigureAwait(false);
                    if (generation != Volatile.Read(ref _generation))
                    {
                        // This instance was reset and handed to another connection while the receive was pending.
                        return;
                    }

                    if (receiveResult != ReceiveResult.Success)
                    {
                        if (receiveResult == ReceiveResult.InvalidPacketSize)
                        {
                            if (_owner is IConnectionTrafficMetrics trafficMetrics)
                            {
                                trafficMetrics.IncrementPacketsDropped();
                            }
                        }
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (IS_BENIGN_DISCONNECT(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.SocketConnection:Internal", $"saea-receive-loop varint ended (peer closed/shutdown) endpoint={_owner?.NetworkEndpoint.Address}"));
            }
        }
        catch (OperationCanceledException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.SocketConnection:Internal", $"saea-receive-loop varint cancelled endpoint={_owner?.NetworkEndpoint.Address}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                Exception e = (ex as AggregateException)?.Flatten() ?? ex;
                if (Security.ThrottledEventGate.TryAcquire(ref s_receiveVarIntFaultedTicks, ref s_receiveVarIntFaultedSuppressed, DateTime.UtcNow.Ticks, TimeSpan.TicksPerSecond * 5, out long suppressed))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                            new DiagnosticLog("NW.SocketConnection:Internal", $"receive varint faulted endpoint={_owner.NetworkEndpoint.Address} suppressed-count={suppressed}", e));
                    }
                    ;
                }
            }
        }
        finally
        {
            // A loop that outlived its connection must not touch the connection that now owns this pooled instance.
            if (generation == Volatile.Read(ref _generation))
            {
                if (Volatile.Read(ref _socketDetached) == 1 && _bufferDataLength > 0)
                {
                    this.StolenData = new byte[_bufferDataLength];
                    Buffer.BlockCopy(_buffer!, 0, this.StolenData, 0, _bufferDataLength);
                }

                this.CANCEL_RECEIVE_ONCE();
                this.INVOKE_CLOSE_ONCE();
            }
        }
    }

    private void PROCESS_VARINT_FRAME_FROM_BUFFER(int offset, int payloadLen)
    {
        ReadOnlySpan<byte> rawPayloadSpan = MemoryExtensions.AsSpan(_buffer, offset, payloadLen);

        BufferLease lease = BufferLease.CopyFrom(rawPayloadSpan);
        lease.IsReliable = true;

        if (payloadLen < PacketConstants.HeaderSize)
        {
#if DEBUG
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning, new DiagnosticLog("NW.SocketConnection:Internal", $"malformed-payload (too small for protocol header) payload-length={payloadLen} endpoint={_endpointString}"));
            }
#endif
            lease.Dispose();
            return;
        }

        this.LastPingTime = Clock.UnixMillisecondsNow();

        bool handled = _connectionOwner?.OnFrameReceived(lease) ?? true;
        if (!handled)
        {
            if (_owner is IConnectionTrafficMetrics trafficMetrics)
            {
                trafficMetrics.IncrementPacketsDropped();
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"sink-rejected-frame-drop payload-length={payloadLen} endpoint={_endpointString}"));
            }

            lease.Dispose();
            return;
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.SocketConnection:Internal", $"accepted payload-length={payloadLen} endpoint={_endpointString}"));
        }
    }
}
