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


/// <inheritdoc />
internal sealed class CancellableValueTaskSource : IValueTaskSource, IPoolable, IPoolRentable
{
    public static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;

    private short _token;
    private int _resultSet;
    private int _owners;
    private ManualResetValueTaskSourceCore<bool> _core;
    private CancellationTokenRegistration _cancellationReg;

    /// <inheritdoc />
    public bool IsActive { get; private set; }

    /// <inheritdoc />
    public void OnRent()
    {
        this.IsActive = true;
        _resultSet = 0;
        _owners = 2;
        _core.Reset();
        _token = _core.Version;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ResetForPool()
    {
        this.IsActive = false;
        _cancellationReg = default;
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ValueTask Await(ValueTask task, CancellationToken cancellationToken)
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
            return ValueTask.FromCanceled(cancellationToken);
        }

        CancellableValueTaskSource source = s_pool.Get<CancellableValueTaskSource>();

        source.Start(task, cancellationToken);

        return new ValueTask(source, source._token);
    }

    // ── IValueTaskSource ─────────────────────────────────────────────────────

    /// <inheritdoc />
    void IValueTaskSource.GetResult(short token)
    {
        // Dispose cancellation registration trước để tránh callback fire sau khi trả pool
        _cancellationReg.Dispose();
        _cancellationReg = default;

        try
        {
            // GetResult throw nếu task bị cancel hoặc faulted
            _ = _core.GetResult(token);
        }
        finally
        {
            // Consumer side is done. The instance is shared with AwaitTaskAsync (which may still be
            // pending after a cancel/timeout), so it only goes back to the pool once both sides released it.
            this.Release();
        }
    }

    /// <inheritdoc />
    ValueTaskSourceStatus IValueTaskSource.GetStatus(short token)
        => _core.GetStatus(token);

    /// <inheritdoc />
    void IValueTaskSource.OnCompleted(
        Action<object?> continuation,
        object? state,
        short token,
        ValueTaskSourceOnCompletedFlags flags)
        => _core.OnCompleted(continuation, state, token, flags);

    // ── Private helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Bắt đầu race giữa <paramref name="task"/> và <paramref name="cancellationToken"/>.
    /// </summary>
    private void Start(ValueTask task, CancellationToken cancellationToken)
    {
        // Đăng ký callback cancel trước khi kick off task,
        // để không có window race nếu token bị cancel ngay lập tức.
        // UnsafeRegister: không flow ExecutionContext → tiết kiệm thêm 1 allocation.
        _cancellationReg = cancellationToken.UnsafeRegister(
            static (state, token) =>
            {
                // static lambda: không capture biến ngoài → không closure allocation
                CancellableValueTaskSource self = (CancellableValueTaskSource)state!;
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
    private async Task AwaitTaskAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);

            // Chỉ set nếu chưa bị cancel trước đó
            if (Interlocked.CompareExchange(ref _resultSet, 1, 0) == 0)
            {
                _core.SetResult(true); // sentinel value, không mang ý nghĩa nghiệp vụ
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
}
