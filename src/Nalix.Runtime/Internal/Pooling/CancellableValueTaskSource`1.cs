// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Runtime.Internal.Pooling;

/// <summary>
/// Provides a pooled <see cref="IValueTaskSource{T}"/> that enables awaiting a <see cref="ValueTask{T}"/>
/// with cancellation support without allocating a <see cref="Task"/> via <c>AsTask()</c>.
/// </summary>
/// <typeparam name="T">The result type of the underlying operation.</typeparam>
/// <remarks>
/// <para>
/// This type is designed for high-performance scenarios where <see cref="ValueTask{T}"/> is used
/// and allocations on the asynchronous path must be minimized.
/// </para>
/// <para>
/// Instances are managed by an <see cref="ObjectPoolManager"/> and must not be created directly.
/// Use <see cref="Await(ValueTask{T}, CancellationToken)"/> to obtain a usable <see cref="ValueTask{T}"/>.
/// </para>
/// <para>
/// Lifecycle:
/// <list type="number">
/// <item>
/// Rent from pool and initialize via <see cref="Await(ValueTask{T}, CancellationToken)"/>.
/// </item>
/// <item>
/// Await the returned <see cref="ValueTask{T}"/>.
/// </item>
/// <item>
/// The instance is automatically returned to the pool after <see cref="IValueTaskSource{T}.GetResult(short)"/> completes.
/// </item>
/// </list>
/// </para>
/// <para>
/// This type is not thread-safe for concurrent consumption of the same instance.
/// </para>
/// </remarks>
internal sealed class CancellableValueTaskSource<T> : IValueTaskSource<T>, IPoolable, IPoolRentable
{
    #region Fields

    public static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private short _token;
    private int _resultSet;
    private int _owners;
    private ManualResetValueTaskSourceCore<T> _core;
    private CancellationTokenRegistration _cancellationReg;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets a value indicating whether the instance is currently in use.
    /// </summary>
    public bool IsActive { get; private set; }

    #endregion Properties

    #region IPoolable

    /// <summary>
    /// Initializes the instance after it is rented from the pool.
    /// </summary>
    /// <remarks>
    /// This method is called by the pool infrastructure and should not be invoked manually.
    /// </remarks>
    public void OnRent()
    {
        this.IsActive = true;
        _resultSet = 0;
        _owners = 2;
        _core.Reset();
        _token = _core.Version;
    }

    /// <inheritdoc/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetForPool()
    {
        this.IsActive = false;
        _cancellationReg = default;
    }

    #endregion IPoolable

    #region Public API

    /// <summary>
    /// Creates a <see cref="ValueTask{T}"/> that awaits the specified <paramref name="task"/>
    /// with support for cancellation.
    /// </summary>
    /// <param name="task">The operation to await.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    /// <returns>
    /// A <see cref="ValueTask{T}"/> that completes when either the underlying operation
    /// completes or the cancellation token is triggered.
    /// </returns>
    /// <remarks>
    /// <para>
    /// Fast paths:
    /// <list type="bullet">
    /// <item>
    /// If <paramref name="task"/> has already completed successfully, it is returned directly.
    /// </item>
    /// <item>
    /// If <paramref name="cancellationToken"/> cannot be canceled, the original task is returned.
    /// </item>
    /// <item>
    /// If the token is already canceled, a canceled <see cref="ValueTask{T}"/> is returned.
    /// </item>
    /// </list>
    /// </para>
    /// <para>
    /// Otherwise, a pooled <see cref="IValueTaskSource{T}"/> is used to coordinate completion
    /// between the task and the cancellation token.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask<T> Await(ValueTask<T> task, CancellationToken cancellationToken)
    {
        if (task.IsCompletedSuccessfully)
        {
            return task;
        }

        if (!cancellationToken.CanBeCanceled)
        {
            return task;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<T>(cancellationToken);
        }

        CancellableValueTaskSource<T> source = s_pool.Get<CancellableValueTaskSource<T>>();
        source.Start(task, cancellationToken);

        return new ValueTask<T>(source, source._token);
    }

    // ── IValueTaskSource<T> ──────────────────────────────────────────────────

    /// <inheritdoc />
    T IValueTaskSource<T>.GetResult(short token)
    {
        // Dispose cancellation registration trước để tránh callback fire sau khi trả pool
        _cancellationReg.Dispose();
        _cancellationReg = default;

        try
        {
            // GetResult throw nếu task bị cancel hoặc faulted
            return _core.GetResult(token);
        }
        finally
        {
            // Consumer side is done. The instance is shared with AwaitTaskAsync (which may still be
            // pending after a cancel/timeout), so it only goes back to the pool once both sides released it.
            this.Release();
        }
    }

    /// <inheritdoc />
    ValueTaskSourceStatus IValueTaskSource<T>.GetStatus(short token) => _core.GetStatus(token);

    /// <inheritdoc />
    void IValueTaskSource<T>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    #endregion Public API

    #region Private Methods

    private void Start(ValueTask<T> task, CancellationToken cancellationToken)
    {
        // Đăng ký callback cancel trước khi kick off task,
        // để không có window race nếu token bị cancel ngay lập tức.
        // UnsafeRegister: không flow ExecutionContext → tiết kiệm thêm 1 allocation.
        _cancellationReg = cancellationToken.UnsafeRegister(
            static (state, token) =>
            {
                // static lambda: không capture biến ngoài → không closure allocation
                CancellableValueTaskSource<T> self = (CancellableValueTaskSource<T>)state!;
                if (Interlocked.CompareExchange(ref self._resultSet, 1, 0) == 0)
                {
                    self._core.SetException(new OperationCanceledException(token));
                }
            },
            this);

        // Kick off awaiting task gốc trên background
        // _ = để không await ở đây; completion sẽ SetResult/SetException trên _core
        _ = this.AwaitTaskAsync(task);
    }

    /// <summary>
    /// Await task gốc và forward kết quả vào <see cref="_core"/>.
    /// </summary>
    private async Task AwaitTaskAsync(ValueTask<T> task)
    {
        try
        {
            T result = await task.ConfigureAwait(false);

            // Chỉ set nếu chưa bị cancel trước đó
            if (Interlocked.CompareExchange(ref _resultSet, 1, 0) == 0)
            {
                _core.SetResult(result);
            }
        }
        catch (Exception ex)
        {
            // Always complete the awaiter (even for fatal exceptions) so it can never hang.
            if (Interlocked.CompareExchange(ref _resultSet, 1, 0) == 0)
            {
                _core.SetException(ex);
            }

            if (!ExceptionClassifier.IsNonFatal(ex))
            {
                throw;
            }
        }
        finally
        {
            // Producer side is done; never touch _core after this point.
            this.Release();
        }
    }

    /// <summary>
    /// Releases one of the two owners (consumer / producer). The last one returns the instance to the pool,
    /// so a late completion of an abandoned operation can never reach a re-rented instance.
    /// </summary>
    private void Release()
    {
        if (Interlocked.Decrement(ref _owners) == 0)
        {
            s_pool.Return(this);
        }
    }

    #endregion Private Methods
}
