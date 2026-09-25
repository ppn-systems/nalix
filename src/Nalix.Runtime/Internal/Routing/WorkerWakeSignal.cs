// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Nalix.Runtime.Internal.Routing;

/// <summary>
/// Allocation-free, reusable auto-reset signal owned by a single waiter (one dispatch worker).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the shared <see cref="SemaphoreSlim"/> + timeout wait of the dispatch workers.
/// <see cref="SemaphoreSlim.WaitAsync(int)"/> allocates a task node, an async box, a cancellation
/// promise and a <c>TimerQueueTimer</c> on every idle to wake transition. This type is backed by a
/// single <see cref="ManualResetValueTaskSourceCore{TResult}"/> that is reset and reused for
/// every wait, so a wake costs no allocation and no timer.
/// </para>
/// <para>
/// The signal is sticky: <see cref="Set"/> before <see cref="WaitAsync"/> makes the next wait
/// complete synchronously, so a wake can never be lost between "register idle" and "await".
/// Only one outstanding <see cref="WaitAsync"/> is allowed at a time (single consumer); any
/// number of threads may call <see cref="Set"/>.
/// </para>
/// </remarks>
[DebuggerDisplay("State={_state}")]
internal sealed class WorkerWakeSignal : IValueTaskSource
{
    private const int StateIdle = 0;
    private const int StateWaiting = 1;
    private const int StateSignaled = 2;

    private ManualResetValueTaskSourceCore<bool> _core;
    private int _state;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerWakeSignal"/> class.
    /// </summary>
    /// <remarks>
    /// Continuations always run on the thread pool: the setter is usually a socket receive
    /// completion and must never run a dispatch worker's drain loop inline.
    /// </remarks>
    public WorkerWakeSignal() => _core.RunContinuationsAsynchronously = true;

    /// <summary>
    /// Gets a value indicating whether a signal is pending (set but not yet consumed).
    /// </summary>
    public bool IsSet => Volatile.Read(ref _state) == StateSignaled;

    /// <summary>
    /// Signals the owner. Wakes it if it is waiting; otherwise makes the next wait complete immediately.
    /// </summary>
    /// <returns><see langword="true"/> when a waiting owner was released.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Set()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            switch (state)
            {
                case StateSignaled:
                    return false;

                case StateIdle:
                    if (Interlocked.CompareExchange(ref _state, StateSignaled, StateIdle) == StateIdle)
                    {
                        return false;
                    }

                    break;

                default: // StateWaiting
                    if (Interlocked.CompareExchange(ref _state, StateIdle, StateWaiting) == StateWaiting)
                    {
                        _core.SetResult(true);
                        return true;
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Waits for the next <see cref="Set"/>. Completes synchronously if a signal is already pending.
    /// </summary>
    /// <returns>A value task that completes when the signal is set.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ValueTask WaitAsync()
    {
        // Fast path: consume a pending signal without suspending.
        if (Interlocked.CompareExchange(ref _state, StateIdle, StateSignaled) == StateSignaled)
        {
            return ValueTask.CompletedTask;
        }

        // Safe to reset: the previous wait (if any) has already been consumed via GetResult,
        // and setters only touch _core after winning Waiting -> Idle.
        _core.Reset();

        if (Interlocked.CompareExchange(ref _state, StateWaiting, StateIdle) != StateIdle)
        {
            // A Set() raced in between; consume it and complete synchronously.
            Volatile.Write(ref _state, StateIdle);
            return ValueTask.CompletedTask;
        }

        return new ValueTask(this, _core.Version);
    }

    /// <summary>
    /// Discards a pending signal, if any. Must only be called by the owner while it is not waiting.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Clear() => _ = Interlocked.CompareExchange(ref _state, StateIdle, StateSignaled);

    /// <inheritdoc />
    void IValueTaskSource.GetResult(short token) => _ = _core.GetResult(token);

    /// <inheritdoc />
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token) => _core.GetStatus(token);

    /// <inheritdoc />
    void IValueTaskSource.OnCompleted(
        Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);
}
