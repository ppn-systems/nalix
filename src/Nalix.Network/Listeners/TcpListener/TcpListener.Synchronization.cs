// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal;
using Nalix.Network.Internal.Pooling;
using Nalix.Network.Internal.Tcp;

namespace Nalix.Network.Listeners.Tcp;

/* ###########################################################################
 * Dead Code
 *
 * This implementation contains the original synchronous SocketAsyncEventArgs
 * accept pipeline.
 *
 * It has been superseded by the fully asynchronous accept pipeline and is no
 * longer referenced by the runtime.
 *
 * The file is intentionally kept for:
 *   - historical reference,
 *   - performance comparison,
 *   - debugging old behavior,
 *   - emergency rollback if required.
 *
 * IMPORTANT:
 *   - Do NOT modify this implementation for new features.
 *   - Bug fixes should be applied only if they are required for preserving
 *     historical correctness.
 *
 * ###########################################################################
 */

public abstract partial class TcpListenerBase
{
    /// <summary>
    /// Processes the result of a single accept operation represented by
    /// <paramref name="args"/>, initializing the connection on success or recovering
    /// the pooled resources on failure.
    /// </summary>
    /// <param name="args">
    /// The <see cref="SocketAsyncEventArgs"/> that completed the accept.
    /// Must be a <see cref="PooledSocketAsyncEventArgs"/> instance.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// On success the method validates the accepted socket, checks the connection limiter, calls
    /// <see cref="DISPATCH_CONNECTION"/>, and rebinds a fresh <see cref="PooledAcceptContext"/>
    /// on <paramref name="args"/> so it can be reused for the next accept.
    /// </para>
    /// <para>
    /// On failure the method always ensures that any borrowed pool objects are returned and
    /// that <see cref="SocketAsyncEventArgs.AcceptSocket"/> is reset to
    /// <see langword="null"/> (in the <c>finally</c> block) so the args is safe to reuse.
    /// </para>
    /// <para>
    /// Three distinct exception paths are handled:
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="ObjectDisposedException"/> — the listener was closed mid-accept;
    ///     logged as a warning, socket and context are cleaned up.
    ///   </item>
    ///   <item>
    ///     <see cref="Exception"/> (general) — metrics are incremented, error is logged,
    ///     socket and context are cleaned up, and a fresh context is bound for the next accept.
    ///   </item>
    ///   <item>
    ///     <see cref="SocketError"/> != <c>Success</c> — accept did not
    ///     produce a socket; context is returned and rebound.
    ///   </item>
    /// </list>
    /// </para>
    /// </remarks>
    /// <exception cref="NetworkException"></exception>
    [DebuggerStepThrough]
    protected void HandleAccept(SocketAsyncEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        try
        {
            if (args.SocketError != SocketError.Success)
            {
                // SocketError check first — cheapest path, no pattern match required.
                // This is an early exit for all OS-level errors (Interrupted, OperationAborted, etc.)
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:HandleAccept", $"accept-failed socket-error={args.SocketError}"));
                }

                this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);
                return;
            }

            if (args.AcceptSocket is not Socket socket)
            {
                // SocketError == Success but AcceptSocket null — a rare case,
                // usually due to a race between Close() and Completed callbacks.
                // No socket to log endpoint, logging warning is sufficient.
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:HandleAccept", $"accept-socket-null port={_port}"));
                }

                this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);
                return;
            }

            IConnection? connection = null;
            try
            {
                // Create and process connection similar to async version
                if (((PooledSocketAsyncEventArgs)args).Context is not { } context)
                {
                    Throw.TryAcceptContextNotBound();
                    return;
                }

                if (this.IsProcessChannelFull())
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                    {
                        DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                            new DiagnosticLog("NW.TcpListenerBase:if", $"channel-full - dropped socket directly port={_port}"));
                    }

                    SafeCloseSocket(socket);

                    this.Metrics.RECORD_QUEUE_FULL_REJECTION();
                    this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);

                    return;
                }

                if (_proxyConfig.Enabled)
                {
                    if (_proxyConfig.RequireTrustedProxy && (socket.RemoteEndPoint is not IPEndPoint remoteEp || !_limiter.IsTrustedProxy(remoteEp)))
                    {
                        if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                        {
                            DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                                new DiagnosticLog("NW.TcpListenerBase:if", $"untrusted-proxy-rejected remote-endpoint={socket.RemoteEndPoint}"));
                        }

                        SafeCloseSocket(socket);

                        this.Metrics.RECORD_LIMITER_REJECTION();
                        this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);

                        return;
                    }

                    // Return context to pool since proxy header read doesn't need it.
                    _pool.Return(context);
                    this.BeginProxyHeaderRead(socket);
                }
                else
                {
                    AcceptResult result = this.ProcessAcceptedSocket(socket, context);

                    if (result.Result == AcceptConnectionResult.Accepted)
                    {
                        connection = result.Connection;
                        this.DISPATCH_CONNECTION(connection!);
                    }
                    else if (result.Result != AcceptConnectionResult.Pending)
                    {
                        _pool.Return(context);
                    }
                }

                // Prepare args for the NEXT accept immediately.
                // WHY prepare now: AcceptNext will call AcceptAsync with this args.
                // Otherwise, rebind context -> the old context (returned to the pool) is reused -> bug.
                PooledAcceptContext nextCtx = _pool.Get<PooledAcceptContext>();

                ((PooledSocketAsyncEventArgs)args).Context = nextCtx;
                nextCtx.BindArgsForSync((PooledSocketAsyncEventArgs)args);
            }
            catch (ObjectDisposedException)
            {
                // Listener is disposed of while accept is running -> this is expected shutdown case.
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Warning))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.Warning,
                        new DiagnosticLog("NW.TcpListenerBase:if", $"disposed-during-accept remote-endpoint={socket.RemoteEndPoint?.ToString() ?? "<null>"}"));
                }

                if (connection != null)
                {
                    connection.Dispose();
                }
                else
                {
                    SafeCloseSocket(socket);
                }

                this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);
            }
            catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
            {
                this.Metrics.RECORD_ERROR();
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                        new DiagnosticLog("NW.TcpListenerBase:if", $"accept-error port={_port}", ex));
                }

                if (connection != null)
                {
                    connection.Dispose();
                }
                else
                {
                    SafeCloseSocket(socket);
                }

                this.RebindAcceptContext((PooledSocketAsyncEventArgs)args);
            }
        }
        finally
        {
            // ALWAYS clear AcceptSocket in finally.
            // WHY: SocketAsyncEventArgs is pooled and reused.
            // If AcceptSocket is not cleared -> the next time AcceptAsync is used, reject args(throw).
            // Finally ensures clear even if HandleAccept throw -> args always safe to reuse.
            args.AcceptSocket = null;
        }
    }

    /// <summary>
    /// Callback invoked by the socket runtime when a synchronous-path accept operation
    /// completes asynchronously (i.e. <see cref="Socket.AcceptAsync(SocketAsyncEventArgs)"/>
    /// returned <see langword="true"/> and later fired the <c>Completed</c> event).
    /// </summary>
    /// <param name="sender">
    /// The source of the event. May be <see langword="null"/>.
    /// </param>
    /// <param name="args">
    /// The <see cref="SocketAsyncEventArgs"/> whose accept operation
    /// completed. Must be a <see cref="PooledSocketAsyncEventArgs"/> instance.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// After processing the completed accept via <see cref="HandleAccept"/>, this method
    /// unsubscribes itself from <paramref name="args"/> and returns the args to the pool.
    /// It then allocates a fresh pair of <see cref="PooledAcceptContext"/> and
    /// <see cref="PooledSocketAsyncEventArgs"/>, wires up the callback, and calls
    /// <see cref="AcceptNext"/> to keep the accept pipeline flowing.
    /// </para>
    /// <para>
    /// The unsubscription happens in the <c>finally</c> block to guarantee it occurs even if
    /// <see cref="HandleAccept"/> throws, preventing the args from firing a stale callback
    /// after it has been returned to the pool.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    protected void OnSyncAcceptCompleted(object? sender, SocketAsyncEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        // Prepare NEW args BEFORE processing the current args.
        // WHY before: After HandleAccept + Return(args), the old args belong to the pool.
        // AcceptNext requires new args ready to call immediately -> cannot allocate after Return.
        PooledAcceptContext context = _pool.Get<PooledAcceptContext>();
        PooledSocketAsyncEventArgs newArgs = _pool.Get<PooledSocketAsyncEventArgs>();

        newArgs.Context = context;
        context.BindArgsForSync(newArgs);
        newArgs.Completed += this.OnSyncAcceptCompleted;

        try
        {
            this.HandleAccept(args);
        }
        finally
        {
            // Unsubscribe BEFORE returning args to the pool.
            // WHY: Otherwise, unsubscribe -> pool can return this args for another accept ->
            // When that accept is complete, the old callback will be called -> duplicate processing bug.
            args.Completed -= this.OnSyncAcceptCompleted;

            // Ensure the args is clean before returning to pool
            args.AcceptSocket = null;
            _pool.Return((PooledSocketAsyncEventArgs)args);

            // Continue the accept pipeline with the new args.
            // WHY in finally: Ensure the pipeline does not stop even if HandleAccept throws.
            // newArgs has been prepared beforehand try -> cannot fail because OOM is here.
            this.AcceptNext(newArgs, _cancellationToken);
        }
    }

    /// <summary>
    /// Drives the synchronous accept loop: calls
    /// <see cref="Socket.AcceptAsync(SocketAsyncEventArgs)"/> in a tight loop, handling
    /// both the immediate (synchronous) completion path and scheduling the
    /// asynchronous completion path via the <c>Completed</c> event.
    /// </summary>
    /// <param name="args">
    /// The <see cref="SocketAsyncEventArgs"/> to use for each accept call.
    /// Must be a <see cref="PooledSocketAsyncEventArgs"/> with a bound
    /// <see cref="PooledAcceptContext"/>.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Token used to signal that the listener is shutting down.
    /// The loop exits cleanly when cancellation is requested.
    /// Must not be <see langword="null"/>.
    /// </param>
    /// <remarks>
    /// <para>
    /// When <see cref="Socket.AcceptAsync(SocketAsyncEventArgs)"/> returns
    /// <see langword="true"/> the operation is pending — the loop breaks and control returns
    /// to the caller; the <c>Completed</c> event on <paramref name="args"/> will resume
    /// processing via <see cref="OnSyncAcceptCompleted"/>.
    /// </para>
    /// <para>
    /// When <see cref="Socket.AcceptAsync(SocketAsyncEventArgs)"/> returns
    /// <see langword="false"/> the accept completed synchronously — <see cref="HandleAccept"/>
    /// is called inline and the loop continues.
    /// </para>
    /// <para>
    /// Expected shutdown exceptions (<see cref="ObjectDisposedException"/>,
    /// <see cref="SocketError.Interrupted"/>,
    /// <see cref="SocketError.OperationAborted"/>,
    /// <see cref="SocketError.ConnectionAborted"/>) cause a clean break.
    /// Other exceptions are logged and the loop pauses for 50 ms before retrying to avoid
    /// CPU-spinning on persistent errors.
    /// </para>
    /// </remarks>
    [DebuggerStepThrough]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    protected void AcceptNext(SocketAsyncEventArgs args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Take a stable local copy to reduce races
            Socket? s = _listener;
            if (s is null || !s.IsBound)
            {
                break;
            }

            // Re-arm args before each use
            args.AcceptSocket = null;

            try
            {
                // AcceptAsync(args) returns:
                // true -> operation pending (async) -> Completed event will fire later -> break loop.
                // false -> operation complete immediately (sync) -> call HandleAccept inline -> continue loop.
                if (s.AcceptAsync(args))
                {
                    // Async path: OnSyncAcceptCompleted will call AcceptNext next.
                    break;
                }

                // Sync completion: process directly within this thread -> no ThreadPool hop needed.
                this.HandleAccept(args);
            }
            catch (ObjectDisposedException)
            {
                // Listener closed during/just before AcceptAsync
                break;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is
                   SocketError.Interrupted or
                   SocketError.OperationAborted or
                   SocketError.ConnectionAborted)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.LoopFaulted))
                {
                    DiagnosticsEvents.Write(DiagnosticsEvents.Internal.LoopFaulted,
                        new DiagnosticLog("NW.TcpListenerBase:AcceptNext", $"accept-error port={_port}", ex));
                }

                // Delay 50ms to avoid CPU spinning during persistent errors (eg, file descriptor explosion).
                // Use Thread.Sleep because this is a synchronous wait on a background worker thread.
                // Avoids allocating a Task object just to block.
                Thread.Sleep(50);
            }
            finally
            {
                // Ensure args reusable
                args.AcceptSocket = null;
            }
        }
    }
}
