// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Diagnostics;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Networking;
using Nalix.Environment.Time;
using Nalix.Network.Internal.Connections;

namespace Nalix.Network.Connections;

public sealed partial class ConnectionHub : IConnectionBroadcaster
{
    #region Public API

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public async Task BroadcastAsync<TState, TSender>(TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>
    {
        using RentedConnectionSnapshot snapshot = _registry.CaptureConnectionSnapshotRented();
        int connectionCount = snapshot.Count;

        if (connectionCount == 0)
        {
            return;
        }

        Task[]? tasks = null;
        IConnection[]? owners = null;
        int taskCount = 0;

        try
        {
            ReadOnlySpan<IConnection> span = snapshot.Span;
            for (int i = 0; i < span.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                QueueSendGeneric(span[i], connectionCount, ref state, ref sender, cancellationToken, ref tasks, ref owners, ref taskCount);
            }

            if (taskCount == 0 || tasks is null || owners is null)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, nameof(BroadcastAsync)).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public async Task MulticastExceptAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, IConnection excludedConnection, TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>
    {
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        ArgumentNullException.ThrowIfNull(excludedConnection);

        IReadOnlyCollection<IConnection> members = groupProvider.GetGroupMembers(groupName);
        int connectionCount = members.Count;
        if (connectionCount == 0)
        {
            return;
        }

        Task[]? tasks = null;
        IConnection[]? owners = null;
        int taskCount = 0;

        ulong excludedId = excludedConnection.ConnectionId;

        try
        {
            if (members is IReadOnlyList<IConnection> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    IConnection conn = list[i];
                    if (conn.ConnectionId == excludedId)
                    {
                        continue;
                    }

                    QueueSendGeneric(conn, connectionCount, ref state, ref sender, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }
            else
            {
                foreach (IConnection connection in members)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (connection.ConnectionId == excludedId)
                    {
                        continue;
                    }

                    QueueSendGeneric(connection, connectionCount, ref state, ref sender, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }

            if (taskCount == 0 || tasks is null || owners is null)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, nameof(MulticastExceptAsync)).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    /// <inheritdoc />
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public async Task MulticastAsync<TState, TSender>(IConnectionGroupRegistry groupProvider, string groupName, TState state, TSender sender, CancellationToken cancellationToken = default)
        where TSender : struct, IConnectionSender<TState>
    {
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);

        IReadOnlyCollection<IConnection> members = groupProvider.GetGroupMembers(groupName);
        int connectionCount = members.Count;
        if (connectionCount == 0)
        {
            return;
        }

        Task[]? tasks = null;
        IConnection[]? owners = null;
        int taskCount = 0;

        try
        {
            if (members is IReadOnlyList<IConnection> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    QueueSendGeneric(list[i], connectionCount, ref state, ref sender, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }
            else
            {
                foreach (IConnection connection in members)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    QueueSendGeneric(connection, connectionCount, ref state, ref sender, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }

            if (taskCount == 0 || tasks is null || owners is null)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, nameof(MulticastAsync)).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    /// <summary>
    /// Multicasts a pre-serialized message buffer to a specific connection group, excluding a specific connection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    public async Task MulticastExceptAsync(
        IConnectionGroupRegistry groupProvider,
        string groupName,
        IConnection excludedConnection,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);
        ArgumentNullException.ThrowIfNull(excludedConnection);

        IReadOnlyCollection<IConnection> members = groupProvider.GetGroupMembers(groupName);
        int connectionCount = members.Count;
        if (connectionCount == 0 || message.IsEmpty || _disposed)
        {
            return;
        }

        Task[]? tasks = null;
        IConnection[]? owners = null;
        int taskCount = 0;

        ulong excludedId = excludedConnection.ConnectionId;

        try
        {
            if (members is IReadOnlyList<IConnection> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    IConnection conn = list[i];
                    if (conn.ConnectionId == excludedId)
                    {
                        continue;
                    }

                    QueueSend(conn, connectionCount, message, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }
            else
            {
                foreach (IConnection connection in members)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (connection.ConnectionId == excludedId)
                    {
                        continue;
                    }

                    QueueSend(connection, connectionCount, message, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }

            if (taskCount == 0 || tasks is null || owners is null)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, nameof(MulticastExceptAsync)).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    /// <summary>
    /// Multicasts a pre-serialized message buffer to a specific connection group.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    public async Task MulticastAsync(
        IConnectionGroupRegistry groupProvider,
        string groupName,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupProvider);
        ArgumentException.ThrowIfNullOrEmpty(groupName);

        IReadOnlyCollection<IConnection> members = groupProvider.GetGroupMembers(groupName);
        int connectionCount = members.Count;
        if (connectionCount == 0 || message.IsEmpty || _disposed)
        {
            return;
        }

        Task[]? tasks = null;
        IConnection[]? owners = null;
        int taskCount = 0;

        try
        {
            if (members is IReadOnlyList<IConnection> list)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    QueueSend(list[i], connectionCount, message, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }
            else
            {
                foreach (IConnection connection in members)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    QueueSend(connection, connectionCount, message, cancellationToken, ref tasks, ref owners, ref taskCount);
                }
            }

            if (taskCount == 0 || tasks is null || owners is null)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, nameof(MulticastAsync)).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    /// <summary>
    /// Broadcasts a message to all active connections.
    /// </summary>
    /// <param name="message">The pre-serialized message buffer to broadcast.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous broadcast operation.</returns>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [SuppressMessage("Performance", "CA1873:Avoid potentially expensive logging", Justification = "<Pending>")]
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "Existing overload; only the UDP transport parameter was removed.")]
    public async Task BroadcastAsync(
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken = default)
    {
        if (message.IsEmpty || _disposed)
        {
            return;
        }

        /*
         * [Broadcast Logic]
         * To broadcast a message, we first capture a stable snapshot of all 
         * active connections using a rented buffer. This ensures that we don't
         * hold the dictionary locks while performing I/O, and avoids heap
         * allocations entirely.
         */
        using RentedConnectionSnapshot snapshot = _registry.CaptureConnectionSnapshotRented();

        if (snapshot.IsEmpty)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Trace))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Trace,
                    new DiagnosticLog("NW.ConnectionHub:BroadcastAsync", "broadcast-skip total=0"));
            }

            return;
        }

        bool measureLatency = _options.IsEnableLatency && DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Information);
        TimingScope scope = measureLatency ? TimingScope.Start() : default;

        if (_options.BroadcastBatchSize > 0)
        {
            await this.BroadcastBatchedAsync(snapshot, message, cancellationToken)
                      .ConfigureAwait(false);
        }
        else
        {
            await this.BroadcastCoreAsync(
                snapshot, message, predicate: null, cancellationToken,
                nameof(BroadcastAsync)).ConfigureAwait(false);
        }

        if (measureLatency)
        {
            string latency = scope.GetElapsedMilliseconds().ToString("0.000", CultureInfo.InvariantCulture);
            int snapshotCount = snapshot.Count;
            DiagnosticsEvents.Write(
                DiagnosticsEvents.Internal.Information,
                new DiagnosticLog(
                    "PERF.NW.BroadcastAsync",
                    $"total={snapshotCount}, latency={latency} ms"));
        }
    }

    /// <summary>
    /// Broadcasts a message to connections matching the given predicate.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    [Obsolete(
        "This overload sends a pre-serialized buffer and bypasses the normal compression and encryption pipeline. Use the packet-based multicast overload instead.",
        error: false,
        DiagnosticId = "NALIX_NET001")]
    public async Task BroadcastWhereAsync(
        ReadOnlyMemory<byte> message,
        Func<IConnection, bool> predicate,
        CancellationToken cancellationToken = default)
    {
        if (message.IsEmpty || _disposed)
        {
            return;
        }

        using RentedConnectionSnapshot snapshot = _registry.CaptureConnectionSnapshotRented();
        if (snapshot.IsEmpty)
        {
            return;
        }

        await this.BroadcastCoreAsync(
            snapshot, message, predicate, cancellationToken,
            nameof(BroadcastWhereAsync)).ConfigureAwait(false);
    }

    #endregion Public API

    #region Private Helpers

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueSend(
        IConnection connection,
        int connectionCount,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken,
        ref Task[]? tasks,
        ref IConnection[]? owners,
        ref int taskCount)
    {
        if (connection.IsDisposed)
        {
            return;
        }

        IConnection.ITransport? targetTransport =
            connection.Transport;

        if (targetTransport is null)
        {
            return;
        }

        try
        {
            ValueTask sendTask = targetTransport.SendAsync(message, cancellationToken);
            if (sendTask.IsCompletedSuccessfully)
            {
                return;
            }

            tasks ??= ArrayPool<Task>.Shared.Rent(connectionCount);
            owners ??= s_connectionPool.Rent(connectionCount);

            tasks[taskCount] = sendTask.AsTask();
            owners[taskCount] = connection;
            taskCount++;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("NW.ConnectionHub:MulticastAsync", $"send-failure id={connection.ConnectionId:X16}", ex));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void QueueSendGeneric<TState, TSender>(
        IConnection connection,
        int connectionCount,
        ref TState state,
        ref TSender sender,
        CancellationToken cancellationToken,
        ref Task[]? tasks,
        ref IConnection[]? owners,
        ref int taskCount)
        where TSender : struct, IConnectionSender<TState>
    {
        if (connection.IsDisposed)
        {
            return;
        }

        try
        {
            ValueTask sendTask = sender.SendAsync(connection, ref state, cancellationToken);
            if (sendTask.IsCompletedSuccessfully)
            {
                return;
            }

            tasks ??= ArrayPool<Task>.Shared.Rent(connectionCount);
            owners ??= s_connectionPool.Rent(connectionCount);

            tasks[taskCount] = sendTask.AsTask();
            owners[taskCount] = connection;
            taskCount++;
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog("NW.ConnectionHub", $"send-failure id={connection.ConnectionId:X16}", ex));
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void LogGenericFailures(Task[] tasks, IConnection[] owners, int taskCount, string operationName)
    {
        for (int i = 0; i < taskCount; i++)
        {
            Task task = tasks[i];
            if (!task.IsFaulted)
            {
                continue;
            }

            Exception? exception = task.Exception?.GetBaseException();
            if (exception is null || !ExceptionClassifier.IsNonFatal(exception))
            {
                continue;
            }

            // A connection that closed while the broadcast was sending to it is normal churn, not a failure.
            if (exception is ObjectDisposedException)
            {
                continue;
            }

            IConnection owner = owners[i];
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Error,
                    new DiagnosticLog($"NW.ConnectionHub:{operationName}", $"send-failure id={owner.ConnectionId:X16}", exception));
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task BroadcastCoreAsync(
        RentedConnectionSnapshot snapshot,
        ReadOnlyMemory<byte> message,
        Func<IConnection, bool>? predicate,
        CancellationToken cancellationToken,
        string operationName)
    {
        int connectionCount = snapshot.Count;
        Task[]? tasks = ArrayPool<Task>.Shared.Rent(connectionCount);
        IConnection[]? owners = s_connectionPool.Rent(connectionCount);
        int taskCount = 0;

        try
        {
            for (int i = 0; i < connectionCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                IConnection connection = snapshot[i];
                if (predicate is not null && !predicate(connection))
                {
                    continue;
                }

                IConnection.ITransport? targetTransport = connection.Transport;
                if (targetTransport == null)
                {
                    continue;
                }

                try
                {
                    ValueTask sendValueTask = targetTransport.SendAsync(message, cancellationToken);
                    if (!sendValueTask.IsCompletedSuccessfully)
                    {
                        tasks[taskCount] = sendValueTask.AsTask();
                        owners[taskCount] = connection;
                        taskCount++;
                    }
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                    {
                        DiagnosticsEvents.Write(
                            DiagnosticsEvents.Internal.Error,
                            new DiagnosticLog("NW.ConnectionHub:BroadcastAsync", $"send-failure op={operationName} id={connection.ConnectionId:X16}", ex));
                    }
                }
            }

            if (taskCount == 0)
            {
                return;
            }

            await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken, operationName).ConfigureAwait(false);
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private async Task BroadcastBatchedAsync(
        RentedConnectionSnapshot snapshot,
        ReadOnlyMemory<byte> message,
        CancellationToken cancellationToken)
    {
        int connectionCount = snapshot.Count;
        int batchSize = Math.Max(1, _options.BroadcastBatchSize);
        Task[]? tasks = ArrayPool<Task>.Shared.Rent(batchSize);
        IConnection[]? owners = s_connectionPool.Rent(batchSize);
        int taskCount = 0;

        try
        {
            for (int i = 0; i < connectionCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                IConnection connection = snapshot[i];

                IConnection.ITransport? targetTransport = connection.Transport;
                if (targetTransport == null)
                {
                    continue;
                }

                try
                {
                    ValueTask sendValueTask = targetTransport.SendAsync(message, cancellationToken);
                    if (!sendValueTask.IsCompletedSuccessfully)
                    {
                        tasks[taskCount] = sendValueTask.AsTask();
                        owners[taskCount] = connection;
                        taskCount++;
                    }
                }
                catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
                {
                    if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Error))
                    {
                        DiagnosticsEvents.Write(
                            DiagnosticsEvents.Internal.Error,
                            new DiagnosticLog("NW.ConnectionHub:BroadcastAsync", $"send-failure id={connection.ConnectionId}", ex));
                    }
                }

                if (taskCount < batchSize)
                {
                    continue;
                }

                await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken,
                              nameof(BroadcastBatchedAsync)).ConfigureAwait(false);
                Array.Clear(tasks, 0, taskCount);
                Array.Clear(owners, 0, taskCount);
                taskCount = 0;
            }

            if (taskCount > 0)
            {
                await AwaitTasksAsync(tasks, owners, taskCount, cancellationToken,
                              nameof(BroadcastBatchedAsync)).ConfigureAwait(false);
            }
        }
        finally
        {
            ReturnArrays(ref tasks, ref owners);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static async Task AwaitTasksAsync(
        Task[] tasks,
        IConnection[] owners,
        int taskCount,
        CancellationToken cancellationToken,
        string operationName)
    {
        try
        {
            await Task.WhenAll(MemoryExtensions.AsSpan(tasks, 0, taskCount)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (DiagnosticsEvents.Source.IsEnabled(DiagnosticsEvents.Internal.Debug))
            {
                DiagnosticsEvents.Write(
                    DiagnosticsEvents.Internal.Debug,
                    new DiagnosticLog("NW.ConnectionHub:BroadcastAsync", $"broadcast-cancel op={operationName}"));
            }
        }
        catch (Exception ex) when (ExceptionClassifier.IsNonFatal(ex))
        {
            LogGenericFailures(tasks, owners, taskCount, operationName);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ReturnArrays(ref Task[]? tasks, ref IConnection[]? owners)
    {
        if (tasks is not null)
        {
            ArrayPool<Task>.Shared.Return(tasks, clearArray: true);
            tasks = null;
        }

        if (owners is not null)
        {
            s_connectionPool.Return(owners, clearArray: true);
            owners = null;
        }
    }

    #endregion Private Helpers
}
