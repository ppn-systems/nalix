// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Environment.Memory;
using Nalix.Framework.Injection;
using Nalix.Framework.Options;
using Nalix.Framework.Tasks;
using Nalix.Network.Internal.Protocol;
using Nalix.Network.Internal.Tcp;
using Nalix.Network.Internal.Transport;
using Nalix.Network.RateLimiting;

namespace Nalix.Network.Listeners.Tcp;

public abstract partial class TcpListenerBase
{
    #region Fields

    /// <summary>
    /// Synchronizes access to the intrusive list that tracks in-flight Proxy Protocol handshakes.
    /// </summary>
    private readonly Lock _proxyLock = new();

    /// <summary>
    /// First pending Proxy Protocol handshake in insertion order.
    /// </summary>
    private ProxyHeaderContext? _proxyHead;

    /// <summary>
    /// Last pending Proxy Protocol handshake in insertion order.
    /// </summary>
    private ProxyHeaderContext? _proxyTail;

    /// <summary>
    /// Recurring timeout sweeper handle for pending Proxy Protocol handshakes.
    /// </summary>
    private IRecurringHandle? _proxySweepHandle;

    /// <summary>
    /// Reuses receive event arguments for Proxy Protocol header reads to avoid allocating
    /// a new <see cref="SocketAsyncEventArgs"/> per accepted socket.
    /// </summary>
    private readonly ConcurrentStack<SocketAsyncEventArgs> _receiveArgsPool = new();

    #endregion Fields

    #region Lifecycle

    /// <summary>
    /// Starts the recurring worker that closes Proxy Protocol handshakes which exceed
    /// <see cref="Options.ProxyProtocolOptions.HeaderTimeoutMs"/>.
    /// </summary>
    /// <param name="ct">
    /// Cancellation token reserved for listener shutdown coordination.
    /// </param>
    /// <remarks>
    /// The scheduled task is non-reentrant so only one sweep can inspect the pending
    /// handshake list at a time.
    /// </remarks>
    private void START_PROXY_SWEEP(CancellationToken ct)
    {
        _proxySweepHandle = InstanceManager.Instance.GetOrCreateInstance<TaskManager>().ScheduleRecurring(
            name: $"{TaskNaming.Tags.Tcp}.{TaskNaming.Tags.Proxy}.Sweep.{_port}",
            interval: TimeSpan.FromMilliseconds(500),
            work: _ => { this.SWEEP_PROXY_TIMEOUTS(ct); return ValueTask.CompletedTask; },
            options: new RecurringOptions
            {
                Tag = TaskNaming.Tags.Net,
                NonReentrant = true,
                ExecutionTimeout = TimeSpan.FromMilliseconds(_proxyConfig.HeaderTimeoutMs)
            }
        );
    }

    /// <summary>
    /// Stops the Proxy Protocol timeout sweeper and closes all sockets still waiting for
    /// a complete proxy header.
    /// </summary>
    /// <remarks>
    /// Socket cleanup is performed while holding the handshake-list lock so shutdown sees
    /// a stable list and prevents timeout traversal from observing detached nodes.
    /// </remarks>
    private void STOP_PROXY_SWEEP()
    {
        _proxySweepHandle?.Dispose();
        _proxySweepHandle = null;

        lock (_proxyLock)
        {
            ProxyHeaderContext? node = _proxyHead;
            while (node is not null)
            {
                ProxyHeaderContext? next = node.Next;
                SafeCloseSocket(node.Socket!);
                node = next;
            }
            _proxyHead = null;
            _proxyTail = null;
        }
    }

    /// <summary>
    /// Scans pending Proxy Protocol handshakes and closes sockets whose header read has
    /// exceeded the configured timeout.
    /// </summary>
    /// <remarks>
    /// The list is maintained in insertion order, so the sweep stops at the first node
    /// that has not timed out. This keeps each sweep proportional to the number of expired
    /// handshakes rather than the total number of pending sockets.
    /// </remarks>
    private void SWEEP_PROXY_TIMEOUTS(CancellationToken cancellation)
    {
        long now = Stopwatch.GetTimestamp();
        long timeoutTicks = _proxyConfig.HeaderTimeoutMs * (Stopwatch.Frequency / 1000L);

        lock (_proxyLock)
        {
            ProxyHeaderContext? node = _proxyHead;

            while (node is not null)
            {
                if (cancellation.IsCancellationRequested)
                {
                    break;
                }

                ProxyHeaderContext? next = node.Next;

                if (now - node.HandshakeStartTimeTicks < timeoutTicks)
                {
                    // List is ordered by insertion time. If this one hasn't timed out, the rest haven't either.
                    break;
                }

                // Timeout reached
                this.DetachProxyContext(node);
                SafeCloseSocket(node.Socket!);
                // SafeCloseSocket will trigger OnProxyReadCompleted with error/0 bytes if a receive is pending
                // So we don't clean up the state here, let OnProxyReadCompleted handle it.

                node = next;
            }
        }
    }

    #endregion Lifecycle

    #region Proxy Protocol Handshake

    /// <summary>
    /// Handles completion of a Proxy Protocol header read and either continues reading,
    /// rejects the socket, or promotes it to a fully initialized connection.
    /// </summary>
    /// <param name="sender">
    /// The source that completed the receive operation. May be <see langword="null"/>.
    /// </param>
    /// <param name="args">
    /// Receive event arguments containing a <see cref="ProxyHeaderContext"/> in
    /// <see cref="SocketAsyncEventArgs.UserToken"/>. Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// When a valid proxy header is parsed, the extracted endpoint is checked by the
    /// connection limiter before the socket is wrapped in a <see cref="Connections.Connection"/>.
    /// Any bytes received after the proxy header are injected back into the connection
    /// transport so application data already read during the handshake is preserved.
    /// </remarks>
    private void OnProxyReadCompleted(object? sender, SocketAsyncEventArgs args)
    {
        ProxyHeaderContext state = (ProxyHeaderContext)args.UserToken!;

        if (args.SocketError != SocketError.Success || args.BytesTransferred == 0)
        {
            lock (_proxyLock)
            {
                this.DetachProxyContext(state);
            }
            this.Metrics.RECORD_PROXY_ERROR();
            this.ReleaseProxyContext(state, args, success: false);
            return;
        }

        state.BytesReceived += args.BytesTransferred;
        ReadOnlySpan<byte> buffer = state.Buffer.AsSpan(0, state.BytesReceived);

        // Use zero-alloc overload: returns SocketEndpoint (stack struct) instead of IPEndPoint.
        // On the reject path, no IPAddress/IPEndPoint heap allocation occurs.
        if (!ProxyProtocolParser.TryParse(buffer, out SocketEndpoint parsedEndpoint, out int consumed))
        {
            if (state.BytesReceived >= 232)
            {
                lock (_proxyLock)
                {
                    this.DetachProxyContext(state);
                }

                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                        new DiagnosticLog("NW.TcpListenerBase:OnProxyReadCompleted", $"invalid-proxy-header-drop state-socket-remote-end-point={state.Socket?.RemoteEndPoint}"));
                }

                this.Metrics.RECORD_PROXY_ERROR();
                this.ReleaseProxyContext(state, args, success: false);
                return;
            }

            args.SetBuffer(state.Buffer, state.BytesReceived, 232 - state.BytesReceived);

            try
            {
                if (!state.Socket!.ReceiveAsync(args))
                {
                    this.OnProxyReadCompleted(sender, args);
                }
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                lock (_proxyLock)
                {
                    this.DetachProxyContext(state);
                }

                this.Metrics.RECORD_PROXY_ERROR();
                this.ReleaseProxyContext(state, args, success: false);
            }
            return;
        }

        lock (_proxyLock)
        {
            this.DetachProxyContext(state);
        }

        // Zero-alloc reject path if using the default ConnectionGuard.
        // TryAccept(SocketEndpoint) avoids IPEndPoint construction.
        // If the parser returned Empty (LOCAL command), fall back to socket remote endpoint.
        bool accepted;
        if (parsedEndpoint != SocketEndpoint.Empty)
        {
            if (_limiter is ConnectionGuard cg)
            {
                accepted = cg.TryAccept(parsedEndpoint);
            }
            else
            {
                accepted = _limiter.TryAccept(new IPEndPoint(parsedEndpoint.ToIPAddress(), parsedEndpoint.Port));
            }
        }
        else
        {
            IPEndPoint fallbackIp = (IPEndPoint)state.Socket!.RemoteEndPoint!;
            accepted = _limiter.TryAccept(fallbackIp);
        }

        if (!accepted)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.TcpListenerBase:OnProxyReadCompleted", "proxy-rate-limit-drop"));
            }

            this.ReleaseProxyContext(state, args, success: false);
            return;
        }

        // Accept path: construct IPEndPoint only now (needed for Connection constructor).
        IPEndPoint? realIp = parsedEndpoint != SocketEndpoint.Empty
            ? new IPEndPoint(parsedEndpoint.ToIPAddress(), parsedEndpoint.Port)
            : null;

        AcceptResult result = new(AcceptConnectionResult.Failed, null);

        try
        {
            result = this.ProcessProxyAcceptedSocket(state.Socket!, realIp, consumed, state.Buffer, state.BytesReceived);
        }
        catch (ObjectDisposedException)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.TcpListenerBase:OnProxyReadCompleted", "socket-disposed-during-init"));
            }
        }
        catch (SocketException ex) when (
            ex.SocketErrorCode is
            SocketError.ConnectionReset or
            SocketError.ConnectionAborted or
            SocketError.OperationAborted)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.TcpListenerBase:OnProxyReadCompleted", $"socket-error-during-init socket-error={ex.SocketErrorCode}", ex));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                    new DiagnosticLog("NW.TcpListenerBase:OnProxyReadCompleted", "init-error", ex));
            }
        }

        this.ReleaseProxyContext(state, args, success: result.Result == AcceptConnectionResult.Accepted || result.Result == AcceptConnectionResult.Pending);

        if (result.Result == AcceptConnectionResult.Accepted && result.Connection != null)
        {
            this.DISPATCH_CONNECTION(result.Connection);
        }
    }

    /// <summary>
    /// Begins reading a Proxy Protocol V1 or V2 header from an accepted TCP socket.
    /// </summary>
    /// <param name="socket">
    /// The accepted socket whose remote endpoint may be replaced by the proxy header.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// This method rents a <see cref="ProxyHeaderContext"/>, rents a small buffer from
    /// <see cref="BufferLease.ByteArrayPool"/>, registers the state for timeout sweeping, and starts
    /// the first asynchronous receive. If the receive completes synchronously, processing
    /// continues inline through <see cref="OnProxyReadCompleted"/>.
    /// </remarks>
    private void BeginProxyHeaderRead(Socket socket)
    {
        ProxyHeaderContext state = _pool.Get<ProxyHeaderContext>();
        state.Socket = socket;
        state.HandshakeStartTimeTicks = Stopwatch.GetTimestamp();
        state.Buffer = BufferLease.ByteArrayPool.Rent(256);
        state.BytesReceived = 0;

#pragma warning disable CA2000
        if (!_receiveArgsPool.TryPop(out SocketAsyncEventArgs? args))
        {
            args = new SocketAsyncEventArgs();
            args.Completed += this.OnProxyReadCompleted;
        }
#pragma warning restore CA2000

        args.UserToken = state;
        args.SetBuffer(state.Buffer, 0, 232);

        lock (_proxyLock)
        {
            this.EnqueueProxyContext(state);
        }

        bool pending = false;
        try
        {
            pending = socket.ReceiveAsync(args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            lock (_proxyLock)
            {
                this.DetachProxyContext(state);
            }

            this.Metrics.RECORD_PROXY_ERROR();
            this.ReleaseProxyContext(state, args, success: false);
            return;
        }

        if (!pending)
        {
            this.OnProxyReadCompleted(socket, args);
        }
    }

    /// <summary>
    /// Removes a Proxy Protocol handshake state from the timeout-tracking list.
    /// </summary>
    /// <param name="state">
    /// The handshake state to detach. Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// The operation is idempotent through <see cref="ProxyHeaderContext.RemovedFromList"/>.
    /// Callers must hold <see cref="_proxyLock"/> before invoking this method.
    /// </remarks>
    private void DetachProxyContext(ProxyHeaderContext state)
    {
        if (state.RemovedFromList)
        {
            return;
        }

        state.RemovedFromList = true;
        _ = Interlocked.Decrement(ref _pendingProxyConnections);

        if (state.Prev != null)
        {
            state.Prev.Next = state.Next;
        }
        else if (_proxyHead == state)
        {
            _proxyHead = state.Next;
        }

        if (state.Next != null)
        {
            state.Next.Prev = state.Prev;
        }
        else if (_proxyTail == state)
        {
            _proxyTail = state.Prev;
        }

        state.Next = null;
        state.Prev = null;
    }

    /// <summary>
    /// Appends a Proxy Protocol handshake state to the timeout-tracking list.
    /// </summary>
    /// <param name="state">
    /// The handshake state to append. Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// Callers must hold <see cref="_proxyLock"/> before invoking this method.
    /// </remarks>
    private void EnqueueProxyContext(ProxyHeaderContext state)
    {
        state.Next = null;
        state.Prev = _proxyTail;

        if (_proxyTail != null)
        {
            _proxyTail.Next = state;
        }
        else
        {
            _proxyHead = state;
        }

        _proxyTail = state;
    }

    /// <summary>
    /// Releases all resources associated with a Proxy Protocol handshake.
    /// </summary>
    /// <param name="state">
    /// The handshake state to return to the pool. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="args">
    /// The receive event arguments to recycle for future handshakes.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="success">
    /// <see langword="true"/> when the socket was successfully promoted to a connection;
    /// <see langword="false"/> when the socket must be closed as part of cleanup.
    /// </param>
    /// <remarks>
    /// The rented header buffer is returned to <see cref="BufferLease.ByteArrayPool"/>, the receive
    /// arguments are pushed back into the local pool, and failed handshakes close the
    /// socket before the state object is returned to the shared object pool.
    /// </remarks>
    private void ReleaseProxyContext(ProxyHeaderContext state, SocketAsyncEventArgs args, bool success)
    {
        if (state.Buffer is { } buf)
        {
            BufferLease.ByteArrayPool.Return(buf);
            state.Buffer = null;
        }

        args.UserToken = null;
        _receiveArgsPool.Push(args);

        if (!success && state.Socket != null)
        {
            SafeCloseSocket(state.Socket);
        }

        _pool.Return(state);
    }

    #endregion Proxy Protocol Handshake
}
