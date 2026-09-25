// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using Nalix.Environment.Extensions;
using Nalix.Environment.Hashing;
using Nalix.Environment.Memory;
using Nalix.Framework.Identifiers;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Listeners.Udp;

public abstract partial class UdpListenerBase
{
    #region Datagram Layout

    /// <summary>
    /// Session token size in bytes — equals <see cref="Snowflake.Size"/> (8 bytes).
    /// The token is the connection's <see cref="ISnowflake"/> identifier issued
    /// by the server after TCP login.
    /// </summary>
    /// <remarks>
    /// Datagram layout: <c>[SessionToken (8 bytes) | Payload ...]</c>.
    /// Security is provided by the TCP handshake that issued the token; UDP carries
    /// only non-sensitive game-state data (movement, actions, etc.).
    /// </remarks>
    private const int SessionTokenSize = Snowflake.Size;

    #endregion Datagram Layout

    /// <summary>
    /// Background receive loop worker managed by <see cref="Nalix.Framework.Tasks.TaskManager"/>.
    /// </summary>
    [DebuggerStepThrough]
    private async Task RunReceiveWorkerAsync(IWorkerContext ctx, CancellationToken cancellationToken)
    {
        PooledUdpReceiveEventArgs args = new();
        args.Completed += this.OnReceiveCompleted;

        try
        {
            this.StartReceive(args, ctx, cancellationToken);

            TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(state => ((TaskCompletionSource)state!).TrySetResult(), tcs))
            {
                await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            args.Completed -= this.OnReceiveCompleted;
            args.Dispose();
        }
    }

    /// <summary>
    /// Repeatedly receives datagrams using a <see cref="PooledUdpReceiveEventArgs"/> 
    /// synchronously if possible, or sets up the async callback.
    /// </summary>
    [StackTraceHidden]
    [DebuggerStepThrough]
    private void StartReceive(PooledUdpReceiveEventArgs args, IWorkerContext ctx, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _isDisposed) != 0 || _socket is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ctx.Beat();
                args.ResetForPool();
                args.RemoteEndPoint = _anyEndPoint;
                args.UserToken = (ctx, cancellationToken);

                bool pending = _socket.ReceiveFromAsync(args);
                if (pending)
                {
                    // Will continue in OnReceiveCompleted
                    break;
                }

                // Completed synchronously
                this.HandleReceive(args, ctx);
            }
        }
        catch (ObjectDisposedException ex) when (Volatile.Read(ref _isDisposed) != 0 || cancellationToken.IsCancellationRequested)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.UdpListenerBase:StartReceive", $"disposed-or-cancelled port={_port} exception-type={ex.GetType().Name}", ex));
            }
        }
        catch (ObjectDisposedException ex)
        {
            this.Metrics.RECORD_RECV_ERROR();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:StartReceive", $"recv-object-disposed port={_port}", ex));
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            this.Metrics.RECORD_RECV_ERROR();

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:StartReceive", $"recv-error port={_port}", ex));
            }

            // Brief delay to prevent tight error loops on synchronous failure.
            this.ScheduleRetryStartReceive(args, ctx, cancellationToken);
        }
    }

    [DebuggerStepThrough]
    private void ScheduleRetryStartReceive(PooledUdpReceiveEventArgs args, IWorkerContext ctx, CancellationToken cancellationToken)
    {
        Task retryTask = this.RetryStartReceiveAsync(args, ctx, cancellationToken);
        if (retryTask.IsCompletedSuccessfully)
        {
            return;
        }

        _ = retryTask.ContinueWith((task, state) =>
        {
            if (state is not UdpListenerBase self)
            {
                return;
            }

            Exception? error = task.Exception?.GetBaseException();
            if (error is not null && Volatile.Read(ref self._isDisposed) == 0 && !cancellationToken.IsCancellationRequested)
            {
                self.Metrics.RECORD_RECV_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:ScheduleRetryStartReceive", $"retry-failed port={self._port}", error));
                }
            }
        }, this, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [DebuggerStepThrough]
    private async Task RetryStartReceiveAsync(PooledUdpReceiveEventArgs args, IWorkerContext ctx, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }

        if (Volatile.Read(ref _isDisposed) != 0 || _socket is null || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        this.StartReceive(args, ctx, cancellationToken);
    }

    [DebuggerStepThrough]
    private void OnReceiveCompleted(object? sender, SocketAsyncEventArgs e)
    {
        PooledUdpReceiveEventArgs args = (PooledUdpReceiveEventArgs)e;
        if (args.UserToken is not ValueTuple<IWorkerContext, CancellationToken> state)
        {
            return;
        }
        IWorkerContext ctx = state.Item1;
        CancellationToken cancellationToken = state.Item2;

        try
        {
            this.HandleReceive(args, ctx);
        }
        catch (SocketException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:OnReceiveCompleted", $"handle-error port={_port}", ex));
            }
        }
        catch (ObjectDisposedException ex)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:OnReceiveCompleted", $"handle-error port={_port}", ex));
            }
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Error, new DiagnosticLog("NW.UdpListenerBase:OnReceiveCompleted", $"handle-error port={_port}", ex));
            }
        }
        finally
        {
            if (Volatile.Read(ref _isDisposed) == 0 && !cancellationToken.IsCancellationRequested)
            {
                this.StartReceive(args, ctx, cancellationToken);
            }
        }
    }

    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void HandleReceive(PooledUdpReceiveEventArgs args, IWorkerContext ctx)
    {
        if (args.SocketError != SocketError.Success ||
            args.BytesTransferred == 0 ||
            args.RemoteEndPoint is null ||
            args.Buffer is null)
        {
            return;
        }

        this.HANDLE_RECEIVE_SAFE(args, ctx);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void HANDLE_RECEIVE_SAFE(PooledUdpReceiveEventArgs args, IWorkerContext ctx)
    {
        try
        {
            if (args.RemoteEndPoint is IPEndPoint ip && !_rateLimiter.TryAccept(ip))
            {
                this.LOG_RATE_LIMIT_DROP(ip);
                return;
            }

            if (args.BytesTransferred > _options.MaxUdpDatagramSize)
            {
                this.LOG_OVERSIZE_DROP(args.RemoteEndPoint, args.BytesTransferred);
                return;
            }

            // Copy + lease
            byte[] buffer = BufferLease.ByteArrayPool.Rent(args.BytesTransferred);
            args.Buffer.AsSpan(args.Offset, args.BytesTransferred).CopyTo(buffer.AsSpan());

            BufferLease lease = BufferLease.TakeOwnership(buffer, 0, args.BytesTransferred);
            lease.IsReliable = false;

            this.ProcessDatagram(lease, args.RemoteEndPoint);
            ctx.Advance(1);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.LOG_HANDLE_RECEIVE_ERROR(ex);
        }
    }

    /// <summary>
    /// Processes a single received datagram: extracts the session token, resolves
    /// the associated connection, runs the application-level authentication hook,
    /// and injects the payload into the connection's inbound pipeline.
    /// </summary>
    /// <param name="lease">
    /// The pooled buffer containing the raw datagram bytes. Ownership is transferred
    /// to the connection on success, or the lease is disposed on rejection.
    /// </param>
    /// <param name="remoteEndPoint">The remote endpoint that sent the datagram.</param>
    /// <remarks>
    /// <para>Datagram layout: <c>[SessionToken (8 bytes / ISnowflake) | Payload ...]</c></para>
    /// <para>
    /// The session token is the connection's <see cref="ISnowflake"/> ID (8 bytes)
    /// issued during TCP login. It maps 1:1 to a <see cref="Connection"/> in the
    /// <see cref="IConnectionHub"/>. Lightweight by design — sensitive operations
    /// go through the TCP channel.
    /// </para>
    /// </remarks>
    protected virtual void ProcessDatagram(BufferLease lease, EndPoint? remoteEndPoint)
    {
        // --- 1. Minimum-size and null gate ---
        if (lease == null || remoteEndPoint == null || lease.Length < SessionTokenSize)
        {
            this.Metrics.RECORD_DROP_SHORT();
            this.LOG_SHORT_PACKET_DROP_SESSION_TOKEN(remoteEndPoint, lease?.Length);
            lease?.Dispose();
            return;
        }

        ReadOnlySpan<byte> buffer = lease.Span;
        ReadOnlySpan<byte> payload = buffer[SessionTokenSize..];

        // --- 2. Protocol validation gate ---
        // SEC-72: Strict length and type guard. 
        // A valid UDP datagram must have at least the full packet header size.
        // And the transport byte must be UDP.
        if (payload.Length < PacketHeader.Size)
        {
            this.Metrics.RECORD_DROP_SHORT();
            this.LOG_SHORT_PACKET_DROP_HEADER(remoteEndPoint, payload.Length);
            lease.Dispose();
            return;
        }

        ref readonly PacketHeader header = ref payload.AsHeaderRef();

        // ================================================================
        // FAST PATH — Lookup Connection via SessionToken (Snowflake).
        // ================================================================
        ReadOnlySpan<byte> sessionToken = buffer[..SessionTokenSize];
#pragma warning disable CA2000 // Borrowed from IConnectionHub; UDP receive path must not dispose hub-owned connections.
        if (!this.TryResolveConnection(_hub, sessionToken, out Connection? connection) || connection is null || connection.IsDisposed)
#pragma warning restore CA2000
        {
            this.Metrics.RECORD_DROP_UNKNOWN();
            this.LOG_UNKNOWN_TOKEN_DROP(remoteEndPoint);
            lease.Dispose();
            return;
        }

        // --- 2.5. XxHash32 Integrity Check ---
        // Verify the datagram signature: XxHash32(SessionToken + Payload + Connection.Secret)
        if (lease.Length < SessionTokenSize + PacketHeader.Size + 4)
        {
            this.Metrics.RECORD_DROP_SHORT();
            this.LOG_SHORT_PACKET_DROP_HEADER(remoteEndPoint, lease.Length);
            lease.Dispose();
            return;
        }

        uint receivedHash = BinaryPrimitives.ReadUInt32LittleEndian(lease.Span[^4..]);
        int dataLen = lease.Length - 4;

        if (lease.Capacity >= dataLen + Bytes32.Size)
        {
            connection.Secret.AsSpan().CopyTo(lease.SpanFull[dataLen..]);
            uint computedHash = XxHash32.Compute(lease.SpanFull[..(dataLen + Bytes32.Size)]);
            lease.SpanFull.Slice(dataLen, Bytes32.Size).Clear(); // [SECURITY] scrub the copied secret

            if (computedHash != receivedHash)
            {
                this.Metrics.RECORD_DROP_UNAUTH();
                lease.Dispose();
                return;
            }
        }
        else
        {
            byte[] temp = BufferLease.ByteArrayPool.Rent(dataLen + Bytes32.Size);
            lease.Span[..dataLen].CopyTo(temp);
            connection.Secret.AsSpan().CopyTo(temp.AsSpan(dataLen));
            uint computedHash = XxHash32.Compute(temp.AsSpan(0, dataLen + Bytes32.Size));
            BufferLease.ByteArrayPool.Return(temp, clearArray: true); // [SECURITY] holds the secret

            if (computedHash != receivedHash)
            {
                this.Metrics.RECORD_DROP_UNAUTH();
                lease.Dispose();
                return;
            }
        }

        // Strip the 4-byte MAC from the payload
        lease.CommitLength(dataLen);

        // Update the payload span reference since the length changed
        payload = lease.Span[SessionTokenSize..];

        // --- 3. Endpoint pinning gate (SEC-30) ---
        if (connection.NetworkEndpoint is null ||
            remoteEndPoint is not IPEndPoint remoteIpEndPoint ||
            !this.IsPinnedEndpointMatch(connection.NetworkEndpoint, remoteIpEndPoint))
        {
            this.Metrics.RECORD_DROP_UNAUTH();
            this.LOG_ENDPOINT_MISMATCH_DROP(connection.NetworkEndpoint, remoteEndPoint, connection.ConnectionId);
            lease.Dispose();
            return;
        }

        // --- 4. Replay protection (SEC-27, SEC-71) ---
        if (!connection.UdpReplayWindow.TryCheck(header.SequenceId))
        {
            this.Metrics.RECORD_DROP_UNAUTH();
            this.LOG_REPLAY_WINDOW_DROP(header.SequenceId, connection.ConnectionId);
            lease.Dispose();
            return;
        }

        // --- 5. Application authentication hook ---
        if (!this.IsAuthenticated(connection, remoteEndPoint, payload))
        {
            this.Metrics.RECORD_DROP_UNAUTH();
            this.LOG_UNAUTH_DROP(remoteEndPoint, connection.ConnectionId);
            lease.Dispose();
            return;
        }

        // Ensure the connection has a UDP transport bound to our socket.
        SocketUdpTransport.EnsureUDP(connection, (IPEndPoint)remoteEndPoint, _socket!);

        this.Metrics.RECORD_RX_PACKET();
        this.Metrics.RECORD_RX_BYTES(lease.Length);

        connection.UdpTransport?.RecordBytesReceived(lease.Length);

        if (!lease.ReleaseOwnership(out byte[]? rawBuffer, out int start, out int length) || rawBuffer is null)
        {
            lease.Dispose();
            return;
        }

        try
        {
            BufferLease incomingLease = BufferLease.TakeOwnership(rawBuffer, start + Snowflake.Size, length - Snowflake.Size);
            incomingLease.IsReliable = false;

            ConnectionEventArgs args = connection.AcquireEventArgs();
            args.Initialize(incomingLease, connection);

            if (!Internal.Transport.AsyncCallback.Invoke(s_onProcessFrameBridge, this, args, CallbackLane.Process))
            {
                args.Dispose();
            }

            this.LOG_ACCEPTED(connection.ConnectionId, remoteEndPoint, incomingLease.Length);
        }
        finally
        {
            lease.Dispose();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsPinnedEndpointMatch(INetworkEndpoint pinnedEndPoint, IPEndPoint remoteEndPoint)
    {
        SocketEndpoint pinned = SocketEndpoint.FromNetworkEndpoint(pinnedEndPoint);
        SocketEndpoint remote = SocketEndpoint.FromEndPoint(remoteEndPoint);

        if (!pinned.Equals(remote))
        {
            return false;
        }

        if (pinned.HasPort && pinned.Port != remote.Port)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves a <see cref="Connection"/> from a session token (8-byte <see cref="ISnowflake"/>).
    /// Override in a derived class to change the token → connection mapping strategy.
    /// </summary>
    /// <param name="hub">The active connection hub.</param>
    /// <param name="sessionToken">The 8-byte session token extracted from the datagram header.</param>
    /// <param name="connection">When this method returns <c>true</c>, the resolved connection.</param>
    /// <returns><c>true</c> if a matching connection was found; otherwise <c>false</c>.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public virtual bool TryResolveConnection(IConnectionHub hub, ReadOnlySpan<byte> sessionToken, out Connection? connection)
    {
        connection = hub?.GetConnection(sessionToken[..Snowflake.Size]) as Connection;
        return connection is not null;
    }

    #region Event Bridge

    private static readonly EventHandler<IConnectionEventArgs> s_onProcessFrameBridge = OnProcessFrameBridge;

    /// <summary>
    /// Align with TCP's MessageProcessingBridge: ensures disposal after the pipeline.
    /// </summary>
    private static void OnProcessFrameBridge(object? sender, IConnectionEventArgs e)
    {
        if (sender is not UdpListenerBase self)
        {
            e?.Dispose();
            return;
        }

        try
        {
            self.Protocol.FrameProcessor.ProcessFrame(sender, e);
        }
        finally
        {
            e.Dispose();
        }
    }

    #endregion Event Bridge

    #region Logging Helpers

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_RATE_LIMIT_DROP(IPEndPoint ip)
    {
        this.Metrics.RECORD_DROP_RATE_LIMITED();
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"rate-limit-drop ip={ip}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_OVERSIZE_DROP(EndPoint? remoteEndPoint, int size)
    {
        this.Metrics.RECORD_DROP_OVERSIZE();
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"oversize-drop remote-end-point={remoteEndPoint} size={size}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_HANDLE_RECEIVE_ERROR(Exception ex)
    {
        this.Metrics.RECORD_RECV_ERROR();
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Debug, new DiagnosticLog("NW.UdpListenerBase:Internal", "handle-receive non-fatal", ex));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_SHORT_PACKET_DROP_SESSION_TOKEN(EndPoint? remoteEndPoint, int? leaseLength)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"short-packet-drop session-token remote-end-point={remoteEndPoint} lease-length={leaseLength}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_SHORT_PACKET_DROP_HEADER(EndPoint remoteEndPoint, int payloadLength)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"short-packet-drop header remote-end-point={remoteEndPoint} payload-length={payloadLength}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_UNKNOWN_TOKEN_DROP(EndPoint remoteEndPoint)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"unknown-token-drop remote-end-point={remoteEndPoint}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_ENDPOINT_MISMATCH_DROP(INetworkEndpoint? expected, EndPoint remoteEndPoint, ulong connectionId)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"endpoint-mismatch-drop expected={expected} remote-end-point={remoteEndPoint} connection-id={connectionId:X16}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_REPLAY_WINDOW_DROP(ushort sequenceId, ulong connectionId)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"replay-window-drop sequence-id={sequenceId} connection-id={connectionId:X16}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_UNAUTH_DROP(EndPoint remoteEndPoint, ulong connectionId)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"unauth-drop remote-end-point={remoteEndPoint} connection-id={connectionId:X16}"));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void LOG_ACCEPTED(ulong connectionId, EndPoint remoteEndPoint, int payloadSize)
    {
        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.UdpListenerBase:Internal", $"accepted connection-id={connectionId:X16} remote-end-point={remoteEndPoint} incoming-lease-length={payloadSize}"));
        }
    }

    #endregion Logging Helpers
}
