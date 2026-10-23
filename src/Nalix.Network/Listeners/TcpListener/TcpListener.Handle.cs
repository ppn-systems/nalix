// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Concurrency;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Tcp;
using Nalix.Network.Internal.Time;

namespace Nalix.Network.Listeners.Tcp;

public abstract partial class TcpListenerBase
{
    /// <summary>
    /// Finalizes the acceptance of an incoming connection by invoking the protocol handler
    /// and recording the accepted metric.
    /// </summary>
    /// <param name="connection">
    /// The fully initialized <see cref="IConnection"/> instance representing the accepted client.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// If the protocol's <c>OnAccept</c> call throws, the exception is caught, logged, and the
    /// connection is closed immediately — the listener loop continues uninterrupted.
    /// </remarks>
    [DebuggerStepThrough]
    protected void ProcessConnection(IConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        try
        {
            _protocol.OnAccept(connection, _cancellationToken);

            if (!connection.IsDisposed)
            {
                _hub.RegisterConnection(connection);
                this.Metrics.RECORD_ACCEPTED();
            }
            else
            {
                this.Metrics.RECORD_REJECTED();
            }

            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace, new DiagnosticLog("NW.TcpListenerBase:ProcessConnection", $"new-connection connection-network-endpoint={connection.NetworkEndpoint}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted, new DiagnosticLog("NW.TcpListenerBase:ProcessConnection", $"process-error connection-network-endpoint={connection.NetworkEndpoint}", ex));
            }

            // Disconnect the connection immediately if an error occurs -> prevent resource leaks.
            // WHY Disconnect here: If OnAccept throws an error, the connection has not been registered to any
            // managed list -> you must close it manually here; no one else can do it.
            connection.Dispose();
        }
    }

    /// <summary>
    /// Handles the <see cref="IConnection.ConnectionClosed"/> event raised when a client connection
    /// is closed, either by the remote peer or by the server.
    /// </summary>
    /// <param name="sender">
    /// The object that raised the event. May be <see langword="null"/>.
    /// </param>
    /// <param name="args">
    /// The event arguments containing the <see cref="IConnection"/> that was closed.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// Unsubscribes all event handlers from the closing connection before calling
    /// <see cref="Connection.Dispose"/> to prevent memory leaks and duplicate callbacks.
    /// </para>
    /// <para>
    /// If <paramref name="args"/> or its <see cref="IConnectionEventArgs.Connection"/> is
    /// <see langword="null"/>, this method returns immediately without performing any action.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    protected void HandleConnectionClose(object? sender, IConnectionEventArgs args)
    {
        if (args?.Connection == null)
        {
            return;
        }

        // De-subscribe to prevent memory leaks
        args.Connection.ConnectionClosed -= this.HandleConnectionClose;
        args.Connection.ConnectionClosed -= _limiter.OnConnectionClosed;

        // Keep unwiring post-process as before (if you subscribed it).
        args.Connection.MessageProcessed -= _protocol.PostProcessMessage;
        args.Connection.MessageProcessing -= _protocol.FrameProcessor.ProcessFrame;

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                new DiagnosticLog("NW.TcpListenerBase:HandleConnectionClose", $"close-connection args-connection-network-endpoint={args.Connection.NetworkEndpoint}"));
        }

        args.Connection.Dispose();
    }

    /// <summary>
    /// Configures socket options, constructs a new <see cref="IConnection"/>, and subscribes
    /// all required event handlers on it.
    /// </summary>
    /// <param name="socket">
    /// The raw <see cref="Socket"/> accepted from the listener.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="context">
    /// The pooled accept context that was used for the accept operation.
    /// Returned to the pool immediately after this method claims the socket — callers must
    /// not touch <paramref name="context"/> after this call returns.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A fully configured <see cref="IConnection"/> ready to send and receive data.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The <paramref name="context"/> is returned to the pool <em>before</em> the connection
    /// object is constructed, because the context is only needed during the accept phase and
    /// the pool slot can be reused immediately by the next pending accept.
    /// </para>
    /// <para>
    /// If <c>EnableTimeout</c> is set in the server configuration, the connection is registered
    /// with the <see cref="TimingWheel"/> for idle-timeout tracking.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    internal virtual IConnection? InitializeConnection(Socket socket, PooledAcceptContext context)
    {
        Connection? connection = null;
        bool eventsHooked = false;

        try
        {
            // Order of importance:
            // 1. Configure socket OPTIONS first (it may throw if the socket is invalid).
            // 2. Return the context to the pool (only after step 1 is successful).
            // 3. Create a connection wrapper and subscribe events.
            //
            // WHY doesn't try/finally be used to return the context?
            // If InitializeOptions throw -> caller (ProcessAcceptedSocket/CreateConnectionAsync)
            // It still holds the reference socket and will close it itself in its catch block.
            // If the return context is here when throw -> caller doesn't know the context has been returned ->
            // double-return bug: context returned twice -> pool corrupted.
            if (!this.InitializeOptions(socket))
            {
                _pool.Return(context);
                try
                {
                    if (socket.RemoteEndPoint is IPEndPoint ip)
                    {
                        _limiter.Release(ip);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                SafeCloseSocket(socket);
                this.Metrics.RECORD_ERROR();
                return null;
            }

            // Context only needs to return immediately during the accept — phase after the socket has been claimed.
            // This pool slot will be reused for the next accept without waiting.
            _pool.Return(context);

            connection = new(socket, _protocol.OpCodeExtractor, _timing);

            // Subscribe lifecycle events.
            // WHY subscribe to _limiter.OnConnectionClosed:
            //      When connection close -> limit, the counter must be reduced -> allow a new connection.
            connection.ConnectionClosed += this.HandleConnectionClose;
            connection.ConnectionClosed += _limiter.OnConnectionClosed;
            eventsHooked = true;

            // Keep post-process as you already have (optional).
            // If your PostProcessMessage should run after app protocol, leaving it subscribed is OK
            // as long as it depends on the same args lifecycle rules.
            connection.MessageProcessed += _protocol.PostProcessMessage;

            // Wire the internal listener method to handle the shared pipeline before routing.
            connection.MessageProcessing += _protocol.FrameProcessor.ProcessFrame;

            if (_config.EnableTimeout)
            {
                // Register connection with TimingWheel to track idle timeout.
                // WHY TimingWheel instead of Timer per-connection:
                // - Timer per-connection: O(n) memory + GC pressure when there are thousands of connections.
                // - TimingWheel: O(1) tick, shared wheel for all connections -> much more efficient.
                _timing.Register(connection);
            }

            return connection;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (connection != null)
            {
                connection.Dispose();
            }
            else if (!eventsHooked)
            {
                try
                {
                    if (socket.RemoteEndPoint is IPEndPoint ip)
                    {
                        _limiter.Release(ip);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Processes a socket that has completed Proxy Protocol parsing.
    /// Can be overridden to delay connection creation (e.g. for WebSocket handshakes).
    /// </summary>
    [DebuggerStepThrough]
    internal virtual AcceptResult ProcessProxyAcceptedSocket(Socket socket, EndPoint? realEndPoint, int headerBytesConsumed, byte[]? receiveBuffer, int bytesReceived)
    {
        IConnection? connection = this.InitializeConnection(socket, realEndPoint, headerBytesConsumed, receiveBuffer, bytesReceived);
        if (connection == null)
        {
            return new AcceptResult(AcceptConnectionResult.Failed, null);
        }

        return new AcceptResult(AcceptConnectionResult.Accepted, connection);
    }

    /// <inheritdoc/>
    [DebuggerStepThrough]
    internal virtual IConnection? InitializeConnection(Socket socket, EndPoint? realEndPoint, int headerBytesConsumed, byte[]? receiveBuffer, int bytesReceived)
    {
        bool eventsHooked = false;
        Connection? connection = null;
        EndPoint endpoint = realEndPoint ?? socket.RemoteEndPoint!;

        try
        {
            if (!this.InitializeOptions(socket))
            {
                try
                {
                    if (endpoint is IPEndPoint ip)
                    {
                        _limiter.Release(ip);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
                SafeCloseSocket(socket);
                this.Metrics.RECORD_ERROR();
                return null;
            }
            connection = new(socket, _protocol.OpCodeExtractor, endpoint, _timing);

            connection.ConnectionClosed += this.HandleConnectionClose;
            connection.ConnectionClosed += _limiter.OnConnectionClosed;
            eventsHooked = true;

            connection.MessageProcessed += _protocol.PostProcessMessage;
            connection.MessageProcessing += _protocol.FrameProcessor.ProcessFrame;

            if (_config.EnableTimeout)
            {
                _timing.Register(connection);
            }

            if (bytesReceived > headerBytesConsumed && receiveBuffer != null)
            {
                ReadOnlySpan<byte> extraBytes = receiveBuffer.AsSpan(headerBytesConsumed, bytesReceived - headerBytesConsumed);
                connection.InjectPreReadBytes(extraBytes);
            }

            return connection;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (connection != null)
            {
                connection.Dispose();
            }
            else if (!eventsHooked)
            {
                try
                {
                    if (endpoint is IPEndPoint ip)
                    {
                        _limiter.Release(ip);
                    }
                }
                catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
            throw;
        }
    }

    /// <summary>
    /// Closes a socket, swallowing any exception that occurs during the close operation.
    /// </summary>
    /// <param name="socket">
    /// The socket to close. If <see langword="null"/>, this method is a no-op.
    /// </param>
    /// <remarks>
    /// Intended for use in error-recovery paths where the socket may already be in an
    /// indeterminate state. Exceptions are logged at <c>Debug</c> level and not rethrown,
    /// so the caller's error-handling flow is never interrupted by a secondary failure.
    /// </remarks>
    [StackTraceHidden]
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveInlining)]
    protected internal static void SafeCloseSocket(Socket socket)
    {
        try
        {
            // socket? -> null-conditional: does not throw NullReferenceException if null.
            socket?.Close();
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // Log in Trace (not Error) because this is expected failure in error-recovery path.
            // WHY not rethrow: Currently in cleanup path -> the second exception will obscure the original exception.
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.TcpListenerBase:SafeCloseSocket", "accept-error", ex));
            }
        }
    }

    /// <summary>
    /// Asynchronous accept loop that runs as a background worker for the lifetime of the listener.
    /// Continuously accepts incoming TCP connections and dispatches each one for processing.
    /// </summary>
    /// <param name="ctx">
    /// The worker context used to signal liveness (heartbeat) and to track processed-connection
    /// counts. Must not be <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to signal a graceful shutdown. When cancelled, the loop exits after the
    /// current accept completes or is interrupted.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A <see cref="Task"/> that completes when the loop has exited.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The following exception types are handled without terminating the loop:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="OperationCanceledException"/> (when <paramref name="cancellationToken"/>
    ///     is cancelled) — exits the loop cleanly.
    ///   </item>
    ///   <item>
    ///     <see cref="NetworkException"/> — a rate-limited or limiter-rejected connection;
    ///     the loop pauses for 10 ms and continues.
    ///   </item>
    ///   <item>
    ///     <see cref="SocketException"/> with an ignorable error code
    ///     (see <c>IsIgnorableAcceptError</c>) — transient OS-level accept failure; the loop
    ///     pauses for 50 ms and continues.
    ///   </item>
    ///   <item>
    ///     Any other <see cref="Exception"/> (when not cancelled) — unexpected failure;
    ///     metrics are incremented, the error is logged, and the loop pauses for 50 ms.
    ///   </item>
    /// </list>
    /// </para>
    /// <para>
    /// On each successfully accepted connection forwarded to <see cref="DISPATCH_CONNECTION"/>.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    protected async Task AcceptConnectionsAsync(IWorkerContext ctx, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ = TimeSpan.FromSeconds(2);

        while (!cancellationToken.IsCancellationRequested)
        {
            // ctx.Beat() = heartbeat signal tells TaskManager that the worker is still alive.
            // WHY requires heartbeat: If the worker is hanged (deadlock/infinite loop), TaskManager
            // It can detect and restart/alert based on heartbeat timeout.
            ctx.Beat();

            AcceptResult acceptResult;
            try
            {
                acceptResult = await this.CreateConnectionAsync(cancellationToken)
                                         .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                        new DiagnosticLog("NW.TcpListenerBase:AcceptConnectionsAsync", "shutdown-requested port=" + _port));
                }
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                this.Metrics.RECORD_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                        new DiagnosticLog("NW.TcpListenerBase:AcceptConnectionsAsync", "accept-error port=" + _port, ex));
                }

                // Unexpected error -> record + log + delay 50ms to avoid CPU spin.
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
                continue;
            }

            switch (acceptResult.Result)
            {
                case AcceptConnectionResult.Accepted:
                    if (acceptResult.Connection != null)
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                                new DiagnosticLog("NW.TcpListenerBase:AcceptConnectionsAsync", "accepted connection-network-endpoint=" + acceptResult.Connection.NetworkEndpoint + " port=" + _port));
                        }

                        // Send the connection to process channel -> consumer thread for processing.
                        this.DISPATCH_CONNECTION(acceptResult.Connection);
                    }
                    ctx.Advance(1, note: "accepted");
                    break;

                case AcceptConnectionResult.RejectedByLimiter:
                case AcceptConnectionResult.ProcessChannelFull:
                    if (cancellationToken.IsCancellationRequested || this.State != ListenerState.RUNNING)
                    {
                        break;
                    }
                    // Rate-limited or limiter rejects -> short delay and then try again.
                    // WHY 10ms: Enough for the limiter to "cool down" but not too long -> responsive.
                    // WHY CancellationToken.None: This delay must be completed completely (without interruptions).
                    await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
                    break;

                case AcceptConnectionResult.InvalidSocket:
                case AcceptConnectionResult.Failed:
                    if (cancellationToken.IsCancellationRequested || this.State != ListenerState.RUNNING)
                    {
                        break;
                    }
                    // Transient accept failures. We shouldn't sleep 50ms on expected accept pressure.
                    // No delay is needed for transient / expected pressure failures.
                    break;
                case AcceptConnectionResult.Pending:
                    // Ownership of the context was already transferred (async completion in flight);
                    // nothing to do here, wait for the next loop iteration.
                    break;

                case AcceptConnectionResult.ListenerClosed:
                case AcceptConnectionResult.SocketAborted:
                    // Handled below (after the switch) since the retry-vs-break decision also
                    // depends on cancellationToken, not just the result value.
                    break;

                default:
                    throw new System.ComponentModel.InvalidEnumArgumentException(
                        nameof(acceptResult.Result), (int)acceptResult.Result, typeof(AcceptConnectionResult));
            }

            // ListenerClosed/SocketAborted are only fatal when we actually asked to stop
            // (cancellation requested). Both results can also occur transiently -- e.g. a
            // momentarily disposed-looking socket handle, or an aborted in-flight accept
            // caused by an abrupt client/proxy disconnect (observed in prod behind Cloudflare
            // Tunnel) -- without the listener's CancellationToken ever being signaled. Treating
            // them as unconditionally fatal breaks out of this loop for good; since MaxParallel
            // defaults to 1, that silently kills the only accept-worker and the listener stops
            // accepting new connections forever while the process keeps running (bug: listener
            // hang under prod traffic, fixed by container restart -- root-caused 2026-07-28).
            if (acceptResult.Result is AcceptConnectionResult.ListenerClosed or AcceptConnectionResult.SocketAborted)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                this.Metrics.RECORD_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Critical))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Critical,
                        new DiagnosticLog("NW.TcpListenerBase:AcceptConnectionsAsync",
                            $"transient-{acceptResult.Result} port={_port} -- retrying accept, no cancellation requested"));
                }

                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
        }

        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
        {
            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Trace,
                new DiagnosticLog("NW.TcpListenerBase:AcceptConnectionsAsync", "loop-exited port=" + _port));
        }
    }

    /// <summary>
    /// Asynchronously accepts a single TCP connection from the listener socket,
    /// validates it against the connection limiter, and returns a fully initialized
    /// <see cref="IConnection"/>.
    /// </summary>
    /// <param name="cancellationToken">
    /// Token used to abort the accept operation. When cancelled,
    /// <see cref="OperationCanceledException"/> is propagated to the caller.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <returns>
    /// A <see cref="ValueTask{TResult}"/> whose result is the
    /// accepted and initialized <see cref="IConnection"/>.
    /// </returns>
    /// <exception cref="InternalErrorException">
    /// Thrown when the listener socket has not been initialized (i.e. <c>_listener</c> is
    /// <see langword="null"/>).
    /// </exception>
    /// <exception cref="NetworkException">
    /// Thrown in the following cases:
    /// <list type="bullet">
    ///   <item>The remote endpoint was rejected by the connection limiter.</item>
    ///   <item>A <see cref="SocketException"/> occurred during accept.</item>
    ///   <item>Any other unexpected exception occurred during accept.</item>
    /// </list>
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Propagated when <paramref name="cancellationToken"/> is cancelled during the
    /// async accept wait.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A <see cref="PooledAcceptContext"/> is borrowed from the pool before the async accept
    /// and is returned to the pool in all exit paths — either by
    /// <see cref="InitializeConnection(Socket, PooledAcceptContext)"/> on the success path, or explicitly in every
    /// catch/early-return branch via a <c>contextReturned</c> guard flag to avoid
    /// double-return bugs.
    /// </para>
    /// <para>
    /// The <c>cancellationToken</c> parameter is intentionally unused beyond the initial
    /// <see cref="CancellationToken.ThrowIfCancellationRequested"/> check;
    /// the actual token is forwarded to <c>BeginAcceptAsync</c> internally.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining)]
    [SuppressMessage("Style", "IDE0060:Remove unused parameter", Justification = "<Pending>")]
    [SuppressMessage("CodeQuality", "IDE0079:Remove unnecessary suppression", Justification = "<Pending>")]
    internal async ValueTask<AcceptResult> CreateConnectionAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return new AcceptResult(AcceptConnectionResult.ListenerClosed, null);
        }

        // contextOwned = true: context is the responsibility of this method (requires return to pool).
        // contextOwned = false: ownership has been transferred to InitializeConnection.
        // This pattern prevents double-return (returns twice to the pool) and memory leak(no return).
        bool contextOwned = true;
        PooledAcceptContext context = _pool.Get<PooledAcceptContext>();

        try
        {
            Socket socket;

            if (_listener == null)
            {
                return new AcceptResult(AcceptConnectionResult.ListenerClosed, null);
            }

            // Wait async accept:
            try
            {
                socket = await context.BeginAcceptAsync(_listener, cancellationToken)
                                      .ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return new AcceptResult(AcceptConnectionResult.ListenerClosed, null);
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                   SocketError.Interrupted or
                   SocketError.OperationAborted or
                   SocketError.ConnectionAborted or
                   SocketError.Shutdown)
            {
                return new AcceptResult(AcceptConnectionResult.SocketAborted, null);
            }
            catch (SocketException)
            {
                return new AcceptResult(AcceptConnectionResult.Failed, null);
            }

            if (this.IsProcessChannelFull())
            {
                this.Metrics.RECORD_QUEUE_FULL_REJECTION();
                SafeCloseSocket(socket);
                return new AcceptResult(AcceptConnectionResult.ProcessChannelFull, null);
            }

            // Validate and limit checks occur BEFORE ownership transfer.
            // If a throw occurs here (invalid socket, limiter reject), contextOwned remains true.
            // -> finally, Return(context) will be true.
            if (!socket.Connected || socket.Handle.ToInt64() == -1)
            {
                SafeCloseSocket(socket);
                return new AcceptResult(AcceptConnectionResult.InvalidSocket, null);
            }

            if (_proxyConfig.Enabled)
            {
                if (_proxyConfig.RequireTrustedProxy && socket.RemoteEndPoint is IPEndPoint remoteEp && !_limiter.IsTrustedProxy(remoteEp))
                {
                    this.Metrics.RECORD_LIMITER_REJECTION();
                    SafeCloseSocket(socket);
                    return new AcceptResult(AcceptConnectionResult.RejectedByLimiter, null);
                }

                if (Interlocked.Increment(ref _pendingProxyConnections) > _proxyConfig.MaxPendingProxyConnections)
                {
                    _ = Interlocked.Decrement(ref _pendingProxyConnections);
                    this.Metrics.RECORD_LIMITER_REJECTION();
                    SafeCloseSocket(socket);
                    return new AcceptResult(AcceptConnectionResult.RejectedByLimiter, null);
                }

                // We return the context right away because the proxy header read is async.
                _pool.Return(context);
                contextOwned = false; // So finally block doesn't double-return

                this.BeginProxyHeaderRead(socket);
                return new AcceptResult(AcceptConnectionResult.Accepted, null);
            }

            AcceptResult result = this.ProcessAcceptedSocket(socket, context);
            if (result.Result == AcceptConnectionResult.Accepted)
            {
                contextOwned = false;
            }
            else if (result.Result == AcceptConnectionResult.Pending)
            {
                contextOwned = false;
            }

            if (result.Result == AcceptConnectionResult.Failed)
            {
                return new AcceptResult(AcceptConnectionResult.Failed, null);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            this.Metrics.RECORD_ERROR();
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
            {
                DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                    new DiagnosticLog("NW.TcpListenerBase:CreateConnectionAsync", "unexpected-accept-error", ex));
            }
            return new AcceptResult(AcceptConnectionResult.Failed, null);
        }
        finally
        {
            // WHY: `finally` only returns when `contextOwned` = true.
            // If `contextOwned` = false: InitializeConnection has already returned.
            // If `contextOwned` = true: an exception occurred before the ownership transfer.
            // This pattern completely eliminates the possibility of double-returns and simultaneous leaks.
            // If an async accept is pending, do not return to the pool immediately.
            // The async completion handler (AsyncAcceptCompleted) will return it to the pool
            // once the OS has aborted/completed the operation.
            if (contextOwned && !context.IsAsyncPending)
            {
                _pool.Return(context);
            }
        }
    }

    /// <inheritdoc/>
    /// <param name="connection">
    /// The fully initialized <see cref="IConnection"/> instance representing the accepted client.
    /// Must not be <see langword="null"/>.
    /// </param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    protected void DoAccept(IConnection connection)
    {
        _protocol.OnAccept(connection, _cancellationToken);

        if (connection != null && !connection.IsDisposed)
        {
            _hub.RegisterConnection(connection);
            this.Metrics.RECORD_ACCEPTED();
        }
        else
        {
            this.Metrics.RECORD_REJECTED();
        }
    }

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RebindAcceptContext(PooledSocketAsyncEventArgs pooled)
    {
        if (pooled.Context is PooledAcceptContext ctx)
        {
            _pool.Return(ctx);
        }

        PooledAcceptContext next = _pool.Get<PooledAcceptContext>();
        pooled.Context = next;
        next.BindArgsForSync(pooled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal virtual AcceptResult ProcessAcceptedSocket(Socket socket, PooledAcceptContext context)
    {
        // Validate and limit checks occur BEFORE ownership transfer.
        // If a throw occurs here (invalid socket, limiter reject), contextOwned remains true.
        // -> finally, Return(context) will be true.
        if (!socket.Connected || socket.Handle.ToInt64() == -1)
        {
            SafeCloseSocket(socket);
            return new AcceptResult(AcceptConnectionResult.InvalidSocket, null);
        }

        // Check the connection limiter before proceeding.
        // If the limiter rejects the connection, close the socket and throw to trigger the appropriate metrics and logging in the caller.
        if (socket.RemoteEndPoint is not IPEndPoint ip || !_limiter.TryAccept(ip))
        {
            this.Metrics.RECORD_LIMITER_REJECTION();
            SafeCloseSocket(socket);
            return new AcceptResult(AcceptConnectionResult.RejectedByLimiter, null);
        }

        try
        {
#pragma warning disable CA2000
            IConnection? connection = this.InitializeConnection(socket, context);
#pragma warning restore CA2000

            if (connection is null)
            {
                return new AcceptResult(AcceptConnectionResult.Failed, null);
            }

            return new AcceptResult(AcceptConnectionResult.Accepted, connection);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            return new AcceptResult(AcceptConnectionResult.Failed, null);
        }
    }

    #endregion Private Methods
}
