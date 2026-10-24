// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;
using Nalix.Abstractions.Networking;

namespace Nalix.Network.Internal.Connections;

/// <summary>
/// High-performance thread-safe registry for connection storage, sharding, and fast lookups.
/// Separates the data-structure concerns from the ConnectionHub routing and event logic.
/// </summary>
internal sealed class ConnectionRegistry
{
    private readonly int _shardMask;
    private readonly int _shardCount;
    private readonly bool _isPowerOfTwoShardCount;
    private readonly ConcurrentDictionary<ulong, IConnection>[] _shards;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<ulong, IConnection>> _endpointIndex;

    private int _count;
    private static readonly System.Buffers.ArrayPool<IConnection> s_connectionPool = System.Buffers.ArrayPool<IConnection>.Shared;

    /// <summary>
    /// Gets the current number of active connections.
    /// </summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>
    /// Exposes the underlying shards for aggregation operations like reporting.
    /// </summary>
    public IEnumerable<ConcurrentDictionary<ulong, IConnection>> Shards => _shards;

    public ConnectionRegistry(int shardCount, int perShardCapacity)
    {
        _shardCount = Math.Max(1, shardCount);
        _isPowerOfTwoShardCount = (_shardCount & (_shardCount - 1)) == 0;
        _shardMask = _shardCount - 1;

        _shards = new ConcurrentDictionary<ulong, IConnection>[_shardCount];
        _endpointIndex = new ConcurrentDictionary<string, ConcurrentDictionary<ulong, IConnection>>(StringComparer.Ordinal);

        for (int i = 0; i < _shardCount; i++)
        {
            _shards[i] = new ConcurrentDictionary<ulong, IConnection>(concurrencyLevel: System.Environment.ProcessorCount, capacity: perShardCapacity);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryAdd(ulong connectionKey, IConnection connection)
    {
        ConcurrentDictionary<ulong, IConnection> shard = this.GetShard(connectionKey);
        if (!shard.TryAdd(connectionKey, connection))
        {
            return false;
        }

        this.TrackEndpoint(connectionKey, connection);
        _ = Interlocked.Increment(ref _count);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRemove(ulong connectionKey, out IConnection? removedConnection)
    {
        ConcurrentDictionary<ulong, IConnection> shard = this.GetShard(connectionKey);
        if (!shard.TryRemove(connectionKey, out removedConnection))
        {
            return false;
        }

        this.UntrackEndpoint(connectionKey, removedConnection);
        _ = Interlocked.Decrement(ref _count);
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    [return: MaybeNull]
    public IConnection? GetConnection(ulong id)
    {
        ConcurrentDictionary<ulong, IConnection> shard = this.GetShard(id);
        return shard.TryGetValue(id, out IConnection? connection) ? connection : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public IConnection[] CaptureConnectionSnapshot()
    {
        int estimatedCount = Math.Max(4, Volatile.Read(ref _count));
        IConnection[] buffer = s_connectionPool.Rent(estimatedCount);

        try
        {
            int index = 0;
            foreach (ConcurrentDictionary<ulong, IConnection> shard in _shards)
            {
                foreach (KeyValuePair<ulong, IConnection> kvp in shard)
                {
                    if (index >= buffer.Length)
                    {
                        IConnection[] newBuffer = s_connectionPool.Rent(buffer.Length * 2);
                        Array.Copy(buffer, newBuffer, buffer.Length);
                        s_connectionPool.Return(buffer);
                        buffer = newBuffer;
                    }
                    buffer[index++] = kvp.Value;
                }
            }

            if (index == 0)
            {
                return Array.Empty<IConnection>();
            }

            IConnection[] snapshot = new IConnection[index];
            Array.Copy(buffer, snapshot, index);
            return snapshot;
        }
        finally
        {
            s_connectionPool.Return(buffer, clearArray: true);
        }
    }

    /// <summary>
    /// Captures a snapshot of all active connections using a rented buffer from the shared pool.
    /// The caller must dispose the returned <see cref="RentedConnectionSnapshot"/> to return the buffer.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public RentedConnectionSnapshot CaptureConnectionSnapshotRented()
    {
        int estimatedCount = Math.Max(4, Volatile.Read(ref _count));
        IConnection[] buffer = s_connectionPool.Rent(estimatedCount);

        int index = 0;
        foreach (ConcurrentDictionary<ulong, IConnection> shard in _shards)
        {
            foreach (KeyValuePair<ulong, IConnection> kvp in shard)
            {
                if (index >= buffer.Length)
                {
                    IConnection[] newBuffer = s_connectionPool.Rent(buffer.Length * 2);
                    Array.Copy(buffer, newBuffer, buffer.Length);
                    s_connectionPool.Return(buffer);
                    buffer = newBuffer;
                }
                buffer[index++] = kvp.Value;
            }
        }

        if (index == 0)
        {
            s_connectionPool.Return(buffer, clearArray: true);
            return default;
        }

        return new RentedConnectionSnapshot(buffer, index);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    public IConnection[] CaptureConnectionSnapshot(INetworkEndpoint networkEndpoint)
    {
        if (Volatile.Read(ref _count) == 0)
        {
            return Array.Empty<IConnection>();
        }

        if (!_endpointIndex.TryGetValue(networkEndpoint.Address,
                out ConcurrentDictionary<ulong, IConnection>? bucket))
        {
            return Array.Empty<IConnection>();
        }

        IConnection[] snapshot = [.. bucket.Values];
        return snapshot.Length == 0 ? Array.Empty<IConnection>() : snapshot;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Clear()
    {
        foreach (ConcurrentDictionary<ulong, IConnection> shard in _shards)
        {
            shard.Clear();
        }

        _endpointIndex.Clear();
        _ = Interlocked.Exchange(ref _count, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int GetShardIndex(ulong id)
    {
        ulong hash = MIX64(id);
        return _isPowerOfTwoShardCount ? (int)(hash & (uint)_shardMask) : (int)(hash % (uint)_shardCount);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static ulong MIX64(ulong value)
        {
            value ^= value >> 33;
            value *= 0xff51afd7ed558ccdUL;
            value ^= value >> 33;
            value *= 0xc4ceb9fe1a85ec53UL;
            value ^= value >> 33;
            return value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ConcurrentDictionary<ulong, IConnection> GetShard(ulong id)
    {
        int index = this.GetShardIndex(id);
        return _shards[index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void TrackEndpoint(ulong connectionKey, IConnection connection)
    {
        ConcurrentDictionary<ulong, IConnection> endpointConnections = _endpointIndex.GetOrAdd(
            connection.NetworkEndpoint.Address,
            static _ => new ConcurrentDictionary<ulong, IConnection>());

        endpointConnections[connectionKey] = connection;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void UntrackEndpoint(ulong connectionKey, IConnection connection)
    {
        string address = connection.NetworkEndpoint.Address;
        if (!_endpointIndex.TryGetValue(address, out ConcurrentDictionary<ulong, IConnection>? endpointConnections))
        {
            return;
        }

        _ = endpointConnections.TryRemove(connectionKey, out _);
        if (endpointConnections.IsEmpty)
        {
            _ = _endpointIndex.TryRemove(new KeyValuePair<string, ConcurrentDictionary<ulong, IConnection>>(address, endpointConnections));
        }
    }
}
