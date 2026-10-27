// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Network.Internal.Pooling;

/// <summary>
/// Represents a pooled context for receiving TCP data asynchronously via SAEA.
/// Mirrors the pattern of <see cref="PooledAcceptContext"/>:
/// wraps a reusable <see cref="SocketAsyncEventArgs"/> with
/// built-in pooling and TCS-bridge logic.
/// </summary>
/// <remarks>
/// Lifecycle per connection:
/// <code>
/// // Acquire
/// PooledReceiveContext ctx = ObjectPoolManager.Get&lt;PooledReceiveContext&gt;();
/// ctx.EnsureArgsBound();
///
/// // Use (many times — SAEA is reused for every packet on this connection)
/// int n = await ctx.ReceiveAsync(socket, buffer, offset, count);
///
/// // Release on Dispose
/// ctx.ResetForPool();
/// ObjectPoolManager.Return&lt;PooledReceiveContext&gt;(ctx);
/// </code>
///
/// <para>
/// <b>Bug fixes vs previous revision:</b>
/// <list type="bullet">
///   <item>
///     Static <c>AsyncReceiveCompleted</c> now resolves the result directly on a reusable
///     <see cref="ManualResetValueTaskSourceCore{TResult}"/> stored on the owning
///     <see cref="PooledSocketReceiveContext"/>, removing the per-receive
///     <see cref="TaskCompletionSource{TResult}"/> allocation.
///   </item>
///   <item>
///     <see cref="ReceiveAsync"/> is no longer <c>async</c>: it returns
///     <see cref="ValueTask{T}"/> directly, preserving the
///     synchronous fast-path and <c>AggressiveInlining</c>.
///   </item>
/// </list>
/// </para>
/// </remarks>
[SkipLocalsInit]
[DebuggerStepThrough]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("Args={Args}, ActiveOps={_activeOps}, AwaiterPending={_consumerAwaitPending}")]
internal sealed class PooledSocketReceiveContext : IPoolable, IDisposable, IValueTaskSource<int>
{
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    /// <summary>
    /// Static completion handler shared across all instances.
    /// It resolves the receive task and decrements the active-operation counter
    /// for the owning context without allocating a per-call closure.
    /// </summary>
    [SuppressMessage("Style", "IDE1006:Naming Styles", Justification = "<Pending>")]
    private static readonly EventHandler<SocketAsyncEventArgs> AsyncReceiveCompleted = static (_, e) =>
    {
        if (e.UserToken is not PooledSocketReceiveContext owner)
        {
            return;
        }

#if DEBUG
        Debug.WriteLine($"[PooledSocketReceiveContext] async-complete err={e.SocketError} bytes={e.BytesTransferred} ctx={RuntimeHelpers.GetHashCode(owner)}");
#endif

        try
        {
            if (e.SocketError == SocketError.Success)
            {
                owner._receiveSource.SetResult(e.BytesTransferred);
            }
            else
            {
                owner._receiveSource.SetException(Throw.GetSocketError(e.SocketError));
            }
        }
        finally
        {
            // Always decrement, even if TrySet* fails, so duplicate completions or
            // late completions cannot leave the context stuck in "busy" state.
            owner.EndOperation();
        }
    };

    // -------------------------------------------------------------------------
    // Fields
    // -------------------------------------------------------------------------

    /// <summary>
    /// Always access through BindArgs(...) so the completion handler wiring stays
    /// correct and the pooled SAEA is not swapped out behind the context's back.
    /// </summary>
    private SocketAsyncEventArgs? _args;

    /// <summary>
    /// Active operations counter.
    /// 0 = idle, 1 = one receive in-flight (SAEA is single-op per instance).
    /// Incremented before ReceiveAsync, decremented when OS completes (sync or async).
    /// Prevents ResetForPool() from returning the SAEA while the kernel still holds it.
    /// This is the guard that makes pooling safe under both synchronous and async
    /// completion paths.
    /// </summary>
    private int _activeOps;

    /// <summary>
    /// Reusable awaitable backing store for the async receive path.
    /// Reset once per pending operation, then completed from the shared SAEA callback.
    /// </summary>
    private ManualResetValueTaskSourceCore<int> _receiveSource;

    private int _consumerAwaitPending;

    // -------------------------------------------------------------------------
    // Public surface
    // -------------------------------------------------------------------------

    public PooledSocketReceiveContext() => _receiveSource.RunContinuationsAsynchronously = true;

    /// <summary>
    /// The SAEA currently bound to this context.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <see cref="EnsureArgsBound"/> has not been called yet.
    /// </exception>
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
    /// Ensures this context has a bound SAEA, acquiring one from
    /// <see cref="ObjectPoolManager"/> if necessary.
    /// The binding step is deferred so contexts can be pooled independently from
    /// the underlying SocketAsyncEventArgs instances.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    public void EnsureArgsBound()
    {
        if (_args != null)
        {
            return;
        }

        PooledSocketAsyncEventArgs pooledArgs = s_pool.Get<PooledSocketAsyncEventArgs>();

#if DEBUG
        Debug.WriteLine(
            $"[PooledSocketReceiveContext] EnsureArgsBound acquired saea ctx={RuntimeHelpers.GetHashCode(this)}");
#endif

        this.BindArgs(pooledArgs);
    }

    /// <summary>
    /// Rebinds this context to <paramref name="newArgs"/>:
    /// detaches the completion handler from the old SAEA (if any) and
    /// attaches it to the new one.
    /// This keeps the static completion callback paired with the correct SAEA.
    /// </summary>
    /// <param name="newArgs"></param>
    /// <exception cref="ArgumentNullException"></exception>
    [MemberNotNull(nameof(_args))]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void BindArgs(SocketAsyncEventArgs newArgs)
    {
        ArgumentNullException.ThrowIfNull(newArgs);
        _args = newArgs;
        _args.UserToken = this;

        // One-time registration of the persistent completion handler to avoid
        // delegate churn. Once bound, the handler stays with the pooled SAEA.
        if (newArgs is PooledSocketAsyncEventArgs pooled && !pooled.IsHandlerBound)
        {
            pooled.Completed += AsyncReceiveCompleted;
            pooled.IsHandlerBound = true;
        }

#if DEBUG
        Debug.WriteLine($"[PooledSocketReceiveContext] BindArgs ctx={RuntimeHelpers.GetHashCode(this)}");
#endif
    }

    /// <summary>
    /// Issues a single receive of up to <paramref name="count"/> bytes into
    /// <paramref name="buffer"/> at <paramref name="offset"/>.
    /// <para>
    /// <b>Sync fast-path:</b> when <see cref="Socket.ReceiveAsync(SocketAsyncEventArgs)"/>
    /// returns <see langword="false"/>, the result is returned via
    /// <see cref="ValueTask{T}"/> — no Task allocation,
    /// no TCS await. Abstractions on LAN/loopback, where the socket often completes
    /// inline before the OS has a chance to post an async completion.
    /// </para>
    /// </summary>
    /// <param name="socket">The connected socket to read from.</param>
    /// <param name="buffer">The backing byte array (pooled).</param>
    /// <param name="offset">Start offset inside <paramref name="buffer"/>.</param>
    /// <param name="count">Maximum bytes to receive.</param>
    /// <returns>
    /// A <see cref="ValueTask{T}"/> resolving to the number of bytes
    /// received. Returns 0 when the peer has closed the connection.
    /// </returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask<int> ReceiveAsync(Socket socket, byte[] buffer, int offset, int count)
    {
        SocketAsyncEventArgs args = this.Args; // throws if not bound

        // Point the SAEA window at the requested slice so the kernel writes
        // directly into the caller's buffer segment.
        args.SetBuffer(buffer, offset, count);

        // Re-arm the reusable value-task source for the next pending receive.
        _receiveSource.Reset();

        // Mark that a kernel operation is now in-flight before calling into the socket.
        this.BeginOperation();

        bool pending = this.RECEIVE_ASYNC_SAFE(socket, args);

        if (!pending)
        {
            // Sync fast-path: the socket completed inline, so we must consume the
            // result here because the static completion handler will not run.
            SocketError err = args.SocketError;
            int bytes = args.BytesTransferred;

            this.EndOperation(); // Decrement here — static handler won't fire.

#if DEBUG
            Debug.WriteLine(
                $"[PooledSocketReceiveContext] recv-syncerr={err} bytes={bytes} offset={offset} count={count} ctx={RuntimeHelpers.GetHashCode(this)}");
#endif

            return err != SocketError.Success
                ? ValueTask.FromException<int>(Throw.GetSocketError(err))
                : ValueTask.FromResult(bytes);
        }

#if DEBUG
        Debug.WriteLine($"[PooledSocketReceiveContext] recv-async-pending offset={offset} count={count} ctx={RuntimeHelpers.GetHashCode(this)}");
#endif

        this.BeginConsumerAwait();

        // Async path: the completion callback will fire later and call
        // EndOperation() via the shared owner reference stored in UserToken.
        return new ValueTask<int>(this, _receiveSource.Version);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool RECEIVE_ASYNC_SAFE(Socket socket, SocketAsyncEventArgs args)
    {
        try
        {
            return socket.ReceiveAsync(args);
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            // socket.ReceiveAsync threw synchronously (e.g. disposed socket).
            this.EndOperation();
            throw;
        }
    }

    /// <summary>
    /// Resets the internal state of this context before returning to the pool.
    /// Waits (up to 5 s) for any in-flight SAEA operation to finish so the
    /// kernel is guaranteed to have released the buffer before we clear the SAEA.
    /// The wait is intentionally bounded so teardown cannot hang forever if a
    /// socket misbehaves or the caller forgets to cancel first.
    /// </summary>
    public void ResetForPool()
    {
#if DEBUG
        Debug.WriteLine(
            $"[PooledSocketReceiveContext] ResetForPool begin activeOps={_activeOps} ctx={RuntimeHelpers.GetHashCode(this)}");
#endif

        // Wait for the in-flight operation to finish. Five seconds is generous;
        // a real teardown should cancel the socket first so the OS aborts the op.
        // Only the kernel operation matters here. A consumer whose continuation has not run yet (_consumerAwaitPending)
        // does not touch the SAEA, and the Reset() below bumps the source version so its stale token is rejected.
        if (Volatile.Read(ref _activeOps) != 0)
        {
            long start = Stopwatch.GetTimestamp();
            SpinWait sw = new();

            while (Volatile.Read(ref _activeOps) != 0)
            {
                if (Stopwatch.GetElapsedTime(start).TotalSeconds > 5)
                {
#if DEBUG
                    Debug.WriteLine(
                        $"[PooledSocketReceiveContext] ResetForPool TIMEOUT waiting for idle ops={_activeOps} pending={_consumerAwaitPending} ctx={RuntimeHelpers.GetHashCode(this)}");
#endif
                    // If we are still pending after 5 seconds, we MUST wake up the 
                    // consumer now, otherwise the state machine will be leaked 
                    // (suspended forever) because it's rooted by this context.
                    if (Interlocked.Exchange(ref _consumerAwaitPending, 0) != 0)
                    {
                        try
                        {
                            _receiveSource.SetException(Throw.GetPooledContextDisposed());
                        }
                        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                        {
                            _ = ex.HResult;
#if DEBUG
                            Debug.WriteLine(
                                $"[PooledSocketReceiveContext] SetException race while resetting context={RuntimeHelpers.GetHashCode(this)} ex={ex}");
#endif
                        }
                    }
                    break;
                }
                sw.SpinOnce();
            }
        }

        // IMPORTANT: Reset the ValueTaskSource core. This clears the continuation 
        // delegate (releasing the state machine) and increments the version 
        // to invalidate any in-flight ValueTasks.
        _receiveSource.Reset();

        if (_args != null)
        {
            // If we are still busy after the wait loop, we cannot safely return 
            // the SAEA to the pool because the kernel might still write to its 
            // buffer window. We unsubscribe and let it go to be GC'd later.
            bool isBusy = Volatile.Read(ref _activeOps) != 0;

            _args.Completed -= AsyncReceiveCompleted;
            _args.UserToken = null;

            if (!isBusy)
            {
                _args.SetBuffer(null, 0, 0);

                if (_args is PooledSocketAsyncEventArgs pooled)
                {
                    s_pool.Return(pooled);
                }
            }
#if DEBUG
            else if (_args is PooledSocketAsyncEventArgs)
            {
                Debug.WriteLine(
                    $"[PooledSocketReceiveContext] LEAKING busy SAEA to prevent corruption ctx={RuntimeHelpers.GetHashCode(this)}");
            }
#endif

            _args = null;
        }

        // Re-arm for next use: reset counter to idle state.
        Volatile.Write(ref _activeOps, 0);
        Volatile.Write(ref _consumerAwaitPending, 0);

#if DEBUG
        Debug.WriteLine($"[PooledSocketReceiveContext] ResetForPool done ctx={RuntimeHelpers.GetHashCode(this)}");
#endif
    }

    // -------------------------------------------------------------------------
    // Private: active-op counter helpers
    // -------------------------------------------------------------------------

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginOperation() => _ = Interlocked.Increment(ref _activeOps);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndOperation() => _ = Interlocked.Decrement(ref _activeOps);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void BeginConsumerAwait() => Volatile.Write(ref _consumerAwaitPending, 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EndConsumerAwait() => _ = Interlocked.Exchange(ref _consumerAwaitPending, 0);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token)
        => _receiveSource.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _receiveSource.OnCompleted(continuation, state, token, flags);

    int IValueTaskSource<int>.GetResult(short token)
    {
        try
        {
            return _receiveSource.GetResult(token);
        }
        finally
        {
            this.EndConsumerAwait();
        }
    }

    public void Dispose() => this.ResetForPool();
}
