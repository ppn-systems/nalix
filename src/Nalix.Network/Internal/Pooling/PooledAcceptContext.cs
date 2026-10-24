// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Network.Internal.Pooling;

/// <summary>
/// Represents a pooled context for accepting TCP socket connections asynchronously.
/// Wraps a reusable <see cref="SocketAsyncEventArgs"/> with built-in pooling logic.
/// </summary>
[DebuggerStepThrough]
[DebuggerNonUserCode]
[SkipLocalsInit]
[DebuggerDisplay("Args={Args}")]
[EditorBrowsable(EditorBrowsableState.Never)]
internal sealed class PooledAcceptContext : IPoolable
{
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
    private static readonly EventHandler<SocketAsyncEventArgs> AsyncAcceptCompleted = static (s, e) =>
    {
        if (e.UserToken is not TaskCompletionSource<Socket> tcs)
        {
            return;
        }

        PooledAcceptContext? context = (e as PooledSocketAsyncEventArgs)?.Context;
        _ = (context?._isAsyncPending = false);

        bool wasResolved = e.SocketError == SocketError.Success
            ? e.AcceptSocket is Socket acceptedSocket
                ? tcs.TrySetResult(acceptedSocket)
                : tcs.TrySetException(new InternalErrorException("TryAccept completed successfully without a socket."))
            : tcs.TrySetException(new SocketException((int)e.SocketError));

        if (!wasResolved && context != null)
        {
            if (e.AcceptSocket is Socket socketToClose)
            {
                try
                {
                    socketToClose.Close();
                }
                catch
                {
                    // Ignore
                }
            }

            // The task was already cancelled, and BeginAcceptAsync's caller returns this context on cancellation.
            // Returning it here as well put it in the pool twice, so two listeners shared one SAEA.
        }
    };

    private SocketAsyncEventArgs? _args;
    private volatile bool _isAsyncPending;

    /// <summary>
    /// Gets a value indicating whether an asynchronous accept operation is currently pending.
    /// </summary>
    public bool IsAsyncPending => _isAsyncPending;

    /// <summary>The SAEA currently bound to this context.</summary>
    /// <exception cref="InvalidOperationException"></exception>
    public SocketAsyncEventArgs Args
    {
        get
        {
            if (_args is null)
            {
                Throw.ArgsNotBound();
            }

            return _args;
        }
    }

    /// <summary>
    /// Ensures that this context has a bound SAEA, acquiring one from the pool if necessary.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void EnsureArgsBound()
    {
        if (_args == null)
        {
            PooledSocketAsyncEventArgs pooledArgs = s_pool.Get<PooledSocketAsyncEventArgs>();

            if (pooledArgs == null)
            {
                Throw.FailedToAcquirePooledArgs();
            }

            this.BindArgs(pooledArgs);
        }
    }

    /// <summary>
    /// Rebinds this context to a new <see cref="SocketAsyncEventArgs"/>.
    /// This detaches the shared completion handler from the old args, attaches it
    /// to the new args, and keeps the pooled accept context reusable across accepts.
    /// </summary>
    /// <param name="newArgs"></param>
    /// <exception cref="ArgumentNullException"></exception>
    [MemberNotNull(nameof(_args))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindArgs(SocketAsyncEventArgs newArgs)
    {
        ArgumentNullException.ThrowIfNull(newArgs);
        _args?.Completed -= AsyncAcceptCompleted;
        _args = newArgs;
        _args.Completed += AsyncAcceptCompleted;

        if (newArgs is PooledSocketAsyncEventArgs pooled)
        {
            pooled.Context = this;
        }
    }

    /// <summary>
    /// Binds this context to a new SAEA without wiring the async completion handler.
    /// This is used only when the caller wants to reuse the same SAEA binding
    /// without subscribing the shared async completion callback.
    /// </summary>
    /// <param name="newArgs"></param>
    /// <exception cref="ArgumentNullException"></exception>
    [MemberNotNull(nameof(_args))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindArgsForSync(SocketAsyncEventArgs newArgs)
    {
        ArgumentNullException.ThrowIfNull(newArgs);
        _args?.Completed -= AsyncAcceptCompleted;
        _args = newArgs;

        if (newArgs is PooledSocketAsyncEventArgs pooled)
        {
            pooled.Context = this;
        }
    }

    /// <summary>
    /// Starts an async accept. Supports cancellation — when the token is cancelled the
    /// returned task transitions to <see cref="OperationCanceledException"/> and
    /// the caller is responsible for returning this context to the pool.
    /// </summary>
    /// <param name="listener"></param>
    /// <param name="cancellationToken"></param>
    [Pure]
    public ValueTask<Socket> BeginAcceptAsync(Socket listener, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        this.EnsureArgsBound();

        TaskCompletionSource<Socket> tcs = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Register cancellation separately from the SAEA completion path so the
        // accept task can be canceled even if the socket never completes or the
        // accept operation gets stuck in-flight for longer than expected.
        CancellationTokenRegistration reg = default;
        if (cancellationToken.CanBeCanceled)
        {
            reg = cancellationToken.Register(static state =>
            {
                if (state is not ValueTuple<TaskCompletionSource<Socket>, CancellationToken> tuple)
                {
                    return;
                }

                (TaskCompletionSource<Socket> t, CancellationToken ct) = tuple;
                _ = t.TrySetCanceled(ct);
            }, (tcs, cancellationToken));
        }

        SocketAsyncEventArgs args = this.Args;
        args.UserToken = tcs;
        args.AcceptSocket = null;

        if (!listener.AcceptAsync(args))
        {
            // Synchronous completion means the OS already produced a socket, so we
            // can resolve the TCS inline and dispose the cancellation registration now.
#pragma warning disable CA1849 // BeginAcceptAsync is a ValueTask factory with synchronous SAEA completion; no async context exists to await DisposeAsync here.
            reg.Dispose();
#pragma warning restore CA1849

            if (args.SocketError != SocketError.Success)
            {
                args.AcceptSocket = null;
                _ = tcs.TrySetException(new SocketException((int)args.SocketError));
            }
            else
            {
                Socket? s = args.AcceptSocket;
                args.AcceptSocket = null;
                if (s is null)
                {
                    _ = tcs.TrySetException(new InternalErrorException("TryAccept completed successfully without a socket."));
                }
                else
                {
                    _ = tcs.TrySetResult(s);
                }
            }
        }
        else
        {
            // Asynchronous completion keeps the cancellation registration alive
            // until the accept task settles, then disposes it on the continuation
            // so the cancellation callback cannot outlive the accept task.
            _isAsyncPending = true;
            _ = tcs.Task.ContinueWith(
                static (_, state) =>
                {
                    if (state is CancellationTokenRegistration registration)
                    {
                        registration.Dispose();
                    }
                },
                reg,
                TaskContinuationOptions.ExecuteSynchronously);
        }

        return new ValueTask<Socket>(tcs.Task);
    }

    /// <summary>
    /// Resets internal state before returning to the pool and returns the inner
    /// <see cref="PooledSocketAsyncEventArgs"/> to its own pool.
    /// This prevents stale completion handlers, sockets, or user tokens from
    /// leaking into the next accept operation.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetForPool()
    {
        bool acceptInFlight = _isAsyncPending;
        _isAsyncPending = false;
        if (_args != null)
        {
            _args.Completed -= AsyncAcceptCompleted;
            _args.UserToken = null;

            // A cancelled caller can return this context while the OS still owns the SAEA (the accept only
            // completes once the listener socket closes). Pooling it now would hand another listener an args
            // instance that throws "an asynchronous socket operation is already in progress", so abandon it.
            if (!acceptInFlight)
            {
                _args.AcceptSocket = null;

                if (_args is PooledSocketAsyncEventArgs pooled)
                {
                    s_pool.Return(pooled);
                }
            }

            _args = null;
        }
    }
}
