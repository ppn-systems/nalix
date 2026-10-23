// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Framework.Memory.Objects;
using Nalix.Runtime.Internal.Diagnostics;

namespace Nalix.Runtime.Internal.Routing;

/// <summary>
/// A zero-allocation ThreadPool work item used to offload the continuation
/// of a heavy packet handler without blocking the main dispatch loops.
/// </summary>
internal sealed class DispatchContinuation : IThreadPoolWorkItem, IPoolable, IDisposable
{
    private static readonly ObjectPoolManager s_pool = ObjectPoolManager.Shared;
    private static readonly ThrottleKey s_keyExecute = new("dispatch.execute");

    private readonly Action _onCompleted;
    private IConnection? _connection;
    private IBufferLease? _lease;
    private IPacket? _packet;
    private Task? _pendingTask;
    private CancellationToken _ct;

    static DispatchContinuation()
    {
        _ = s_pool.SetMaxCapacity<DispatchContinuation>(2048);
        _ = s_pool.Prealloc<DispatchContinuation>(128);
    }

    public DispatchContinuation() => _onCompleted = this.Execute;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Initialize(
        IConnection connection,
        [Borrowed] IBufferLease lease,
        IPacket packet, Task pendingTask, CancellationToken ct)
    {
        _connection = connection;
        _lease = lease;
#pragma warning disable NALIX076 // Packet context must not escape handler scope
        _packet = packet;
#pragma warning restore NALIX076 // Packet context must not escape handler scope
        _pendingTask = pendingTask;
        _ct = ct;

        pendingTask.ConfigureAwait(false).GetAwaiter().UnsafeOnCompleted(_onCompleted);
    }


    // Execute runs when _pendingTask has completed via UnsafeOnCompleted.
    public void Execute()
    {
        IConnection? connection = _connection;
        IBufferLease? lease = _lease;
        IPacket? packet = _packet;
        Task? pending = _pendingTask;
        CancellationToken ct = _ct;

        if (connection is null || lease is null || pending is null)
        {
            this.Return();
            return;
        }

        try
        {
#pragma warning disable CA1849 // _pendingTask is already completed when UnsafeOnCompleted invokes Execute
            pending.GetAwaiter().GetResult();
#pragma warning restore CA1849
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Async cancellation
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            try
            {
                if (!connection.IsDisposed)
                {
                    connection.IncrementErrorCount();
                    connection.ThrottledDiagnosticError(
                        DiagnosticsEvents.Internal.Error,
                        s_keyExecute,
                        "RT.AsyncPacketContinuation:ExecuteAsync",
                        $"handler-error ep={connection.NetworkEndpoint}");
                }
            }
            catch (ObjectDisposedException)
            {
                // Ignore ObjectDisposedException since the connection is going away
            }
        }
        finally
        {
            // Guaranteed release for async path
            if (packet is IDisposable disposable)
            {
                disposable.Dispose();
            }

            lease.Dispose();

            // Return this object to the pool
            this.Return();
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Return() => s_pool.Return(this);

    public void ResetForPool()
    {
        _ct = default;
        _lease = null;
        _packet = null;
        _connection = null;
        _pendingTask = null;
    }

    public void Dispose() => this.Return();
}
