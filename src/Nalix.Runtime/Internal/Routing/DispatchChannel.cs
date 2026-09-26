// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Extensions;
using Nalix.Framework.Injection;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Options;

namespace Nalix.Runtime.Internal.Routing;

/// <summary>
/// Provides a priority-aware dispatch channel optimized for high-frequency enqueue/dequeue traffic.
/// </summary>
/// <typeparam name="TPacket">The packet type handled by the channel.</typeparam>
[SkipLocalsInit]
[DebuggerNonUserCode]
[EditorBrowsable(EditorBrowsableState.Never)]
[DebuggerDisplay("TotalPackets={TotalPackets}")]
internal sealed class DispatchChannel<TPacket> : IDispatchChannel<TPacket>, IDisposable where TPacket : IPacket
{
    #region Constants

    private const int LowestPriorityIndex = (int)PacketPriority.NONE;
    private const int HighestPriorityIndex = (int)PacketPriority.URGENT;
    private const int PriorityLevels = HighestPriorityIndex + 1;

    #endregion Constants

    #region Fields

    private readonly DropPolicy _dropPolicy;
    private readonly DispatchOptions _options;
    private readonly int[] _prioWeights;
    private readonly int[] _prioBudgets;

    private readonly long _blockTimeoutTicks;
    private readonly int _maxPerConnectionQueue;
    private readonly bool _boundedPerPriorityMode;
    private readonly int _boundedPerPriorityCapacity;

    private readonly System.Collections.Concurrent.ConcurrentQueue<ConnectionState>[] _readyByPrio;
    private readonly int[] _readyEntriesByPrio;

    private readonly Node?[] _stateBuckets;
    private readonly int _stateMask;

    /// <summary>
    /// Serializes structural bucket mutation (tombstone reuse, node append, node reclamation)
    /// so removed nodes are recycled in place instead of leaking. The per-packet fast path in
    /// <see cref="GetOrCreateState"/> that finds an existing live node stays lock-free; only the
    /// rare once-per-connection create/remove transitions take this lock.
    /// </summary>
    private readonly System.Threading.Lock _stateMutationLock = new();

    private int _activeConnections;
    private int _readyConnections;
    private long _totalEvicted;
    private long _peakPacketCount;
    private PaddedSequence _packetCount;

    #endregion Fields

    #region Properties

    /// <summary>
    /// Gets the total queued packet count across all connections.
    /// </summary>
    public long TotalPackets => Interlocked.Read(ref _packetCount.Value);

    /// <summary>
    /// Gets the highest number of queued packets reached.
    /// </summary>
    public long PeakPackets => Interlocked.Read(ref _peakPacketCount);

    /// <summary>
    /// Gets a value indicating whether any packet is available.
    /// </summary>
    public bool HasPacket => Interlocked.Read(ref _packetCount.Value) > 0;

    /// <summary>
    /// Gets the total number of active connections currently tracked by the channel.
    /// </summary>
    public int TotalConnections => Volatile.Read(ref _activeConnections);

    /// <summary>
    /// Gets the number of connections that currently have at least one packet ready to be dispatched.
    /// </summary>
    public int ReadyConnections => Volatile.Read(ref _readyConnections);

    /// <summary>
    /// Gets a value indicating whether at least one connection sits in a ready queue waiting to be
    /// claimed. Unlike <see cref="ReadyConnections"/>, connections already claimed by a worker are
    /// not counted, so an idle worker can use this to decide whether it may sleep.
    /// </summary>
    internal bool HasClaimableConnection
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            for (int p = HighestPriorityIndex; p >= LowestPriorityIndex; p--)
            {
                if (Volatile.Read(ref _readyEntriesByPrio[p]) > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Gets the total number of packets evicted (dropped) because of capacity limits.
    /// </summary>
    public long TotalEvicted => Interlocked.Read(ref _totalEvicted);

    /// <summary>
    /// Gets a snapshot of the number of ready connections per priority level.
    /// </summary>
    public int[] PendingPerPriority
    {
        get
        {
            int[] snapshot = new int[PriorityLevels];
            this.CopyPendingPerPriority(snapshot);
            return snapshot;
        }
    }

    #endregion Properties

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="DispatchChannel{TPacket}"/> class.
    /// </summary>
    public DispatchChannel()
    {
        _options = ConfigurationManager.Instance.Get<DispatchOptions>();
        _options.Validate();

        _dropPolicy = _options.DropPolicy;
        _maxPerConnectionQueue = _options.MaxPerConnectionQueue;
        _boundedPerPriorityMode = _maxPerConnectionQueue > 0;
        _boundedPerPriorityCapacity = _boundedPerPriorityMode
            ? RoundUpToPowerOf2(Math.Max(4, _maxPerConnectionQueue))
            : 0;
        _blockTimeoutTicks = ToStopwatchTicks(_options.BlockTimeout);

        _readyByPrio = new System.Collections.Concurrent.ConcurrentQueue<ConnectionState>[PriorityLevels];
        _readyEntriesByPrio = new int[PriorityLevels];
        _prioWeights = new int[PriorityLevels];
        _prioBudgets = new int[PriorityLevels];

        string[] weightParts = (_options.PriorityWeights ?? string.Empty)
                               .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        for (int i = 0; i < PriorityLevels; i++)
        {
            int weight = (i < weightParts.Length && int.TryParse(weightParts[i], out int parsed))
                         ? Math.Max(1, parsed)
                         : (1 << i);

            _prioWeights[i] = weight;
            _prioBudgets[i] = weight;

            // A plain lock-free MPMC queue: this ready-list is only ever touched via
            // TryDequeue/Enqueue (see TryClaimWeighted/EnqueueReady below), never the
            // async ReadAsync/WaitToReadAsync surface that System.Threading.Channels
            // exists to serve. Channel<T> still pays for that machinery (a monitor
            // lock guarding both write and read once SingleReader/SingleWriter are
            // false) on every call; ConcurrentQueue<T> gives the same TryWrite/TryRead
            // semantics lock-free and measured ~2-4x cheaper per op under the same
            // producer/consumer shape (see ReadyQueueBenchmarks).
            _readyByPrio[i] = new System.Collections.Concurrent.ConcurrentQueue<ConnectionState>();
        }

        int bucketCount = GetBucketCount(_options);
        _stateBuckets = new Node[bucketCount];
        _stateMask = bucketCount - 1;

        InstanceManager.Instance.GetExistingInstance<IConnectionHub>()?
                       .ConnectionUnregistered += this.OnUnregistered;
    }

    #endregion Constructors

    #region APIs

    /// <summary>
    /// Attempts to claim exclusive processing rights over a connection's mailbox.
    /// Uses Weighted Round-Robin (DRR) to prevent priority starvation.
    /// The returned session grants sole ownership of the connection's packet queue;
    /// disposing it releases the claim and re-enqueues the connection if it still
    /// has pending packets.
    /// </summary>
    /// <param name="session">The exclusive dispatch session when successful.</param>
    /// <returns><see langword="true"/> when a connection was claimed.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public bool TryClaim([NotNullWhen(true)] out IDispatchSession session)
    {
        // Attempt 1: Weighted selection based on current budgets
        if (this.TryClaimWeighted(out session))
        {
            return true;
        }

        // Attempt 2: If we failed to claim but packets or ready entries remain, all active
        // priorities might have exhausted their budgets. Reset and try one more time.
        // Ready entries must count too: a removed connection leaves a stale entry in the
        // ready queue after its packets were drained (HasPacket == false). Without a reset
        // an exhausted budget would never let a worker purge that entry, so
        // HasClaimableConnection would stay true and idle workers would never park (busy spin).
        if (this.HasPacket || this.HasClaimableConnection)
        {
            this.ResetBudgets();
            return this.TryClaimWeighted(out session);
        }

        session = null!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryClaimWeighted([NotNullWhen(true)] out IDispatchSession session)
    {
        session = null!;

        // SCAN: Highest to Lowest, but gated by individual priority budgets.
        for (int p = HighestPriorityIndex; p >= LowestPriorityIndex; p--)
        {
            // First check: does this priority even have ready connections?
            if (Volatile.Read(ref _readyEntriesByPrio[p]) <= 0)
            {
                continue;
            }

            // Second check: do we have budget left for this priority?
            int b = Volatile.Read(ref _prioBudgets[p]);
            if (b <= 0)
            {
                continue;
            }

            // Attempt to claim one budget slot atomically.
            if (Interlocked.CompareExchange(ref _prioBudgets[p], b - 1, b) != b)
            {
                // Failed to claim (contention or budget changed), retry this level one more time.
                p++;
                continue;
            }

            // Successfully claimed budget. Now try to read the connection from the queue.
            if (!_readyByPrio[p].TryDequeue(out ConnectionState? state) || state is null)
            {
                // Queue was empty (race between Volatile.Read and TryRead).
                // Refund the budget and move to the next priority.
                _ = Interlocked.Increment(ref _prioBudgets[p]);
                continue;
            }

            DecrementNonNegative(ref _readyEntriesByPrio[p]);

            if (!state.IsActive)
            {
                // Connection died: purging its stale entry is not a dispatch, so refund the
                // budget slot and try another one at THIS priority.
                _ = Interlocked.Increment(ref _prioBudgets[p]);
                p++;
                continue;
            }

            // Claim succeeded — return an exclusive session.
            // The connection is NOT re-enqueued here; that happens when the
            // session is disposed (via Release) if packets remain.
            session = new DispatchSession(state, this);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Releases a previously claimed connection back to the ready queue
    /// if it still has pending packets.
    /// </summary>
    /// <param name="state">The connection state to release.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void Release(ConnectionState state)
    {
        if (!state.IsActive)
        {
            if (state.TryReleaseReady())
            {
                DecrementNonNegative(ref _readyConnections);
            }
            return;
        }

        if (state.TotalCount > 0)
        {
            int highest = state.GetHighestPriority();
            if (highest >= LowestPriorityIndex)
            {
                this.EnqueueReady(state, highest);
                return;
            }
        }

        if (state.TryReleaseReady())
        {
            DecrementNonNegative(ref _readyConnections);
        }

        if (state.TotalCount > 0 && state.TryMarkReady())
        {
            _ = Interlocked.Increment(ref _readyConnections);
            this.EnqueueReady(state, state.GetHighestPriority());
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void ResetBudgets()
    {
        for (int i = 0; i < PriorityLevels; i++)
        {
            // Reset each priority budget back to its original weight.
            // We use Exchange to ensure the write is visible and atomic.
            _ = Interlocked.Exchange(ref _prioBudgets[i], _prioWeights[i]);
        }
    }

    /// <summary>
    /// Enqueues a packet for a specific connection.
    /// </summary>
    /// <param name="connection">The destination connection.</param>
    /// <param name="raw">The packet lease to enqueue.</param>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void Push(IConnection connection, [Borrowed] IBufferLease raw)
    {
        if (!this.PushCore(connection, raw, out _))
        {
            raw?.Dispose();
        }
    }

    /// <summary>
    /// Copies the pending ready-connection counts per priority into a caller-owned buffer.
    /// </summary>
    /// <param name="destination">Destination span with at least one slot per packet priority.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="destination"/> is too small.</exception>
    public void CopyPendingPerPriority(Span<int> destination)
    {
        if (destination.Length < PriorityLevels)
        {
            throw new ArgumentException("Destination must contain at least one slot per packet priority.", nameof(destination));
        }

        for (int i = 0; i < PriorityLevels; i++)
        {
            int current = Volatile.Read(ref _readyEntriesByPrio[i]);
            destination[i] = current < 0 ? 0 : current;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        InstanceManager.Instance.GetExistingInstance<IConnectionHub>()?
                       .ConnectionUnregistered -= this.OnUnregistered;

        for (int i = 0; i < _stateBuckets.Length; i++)
        {
            Node? node = Interlocked.Exchange(ref _stateBuckets[i], null);
            while (node is not null)
            {
                ConnectionState? state = node.State;
                if (state is not null)
                {
                    _ = state.TryDeactivate();
                    _ = state.DrainAndDisposeAll();
                }
                node = node.Next;
            }
        }

        GC.SuppressFinalize(this);
    }

    #endregion APIs

    #region Internal Methods

    /// <summary>
    /// Enqueues a packet and reports whether a new ready entry was generated.
    /// </summary>
    /// <param name="connection">The destination connection.</param>
    /// <param name="raw">The packet lease to enqueue.</param>
    /// <param name="readyEmitted">When <see langword="true"/>, indicates that a ready queue entry was emitted.</param>
    /// <param name="noBlock">When <see langword="true"/>, block-mode overflow will fail fast instead of waiting for capacity.</param>
    /// <returns><see langword="true"/> if the packet was successfully enqueued.</returns>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal bool PushCore(IConnection connection, [Borrowed] IBufferLease raw, out bool readyEmitted, bool noBlock = false)
    {
        readyEmitted = false;

        if (connection is null)
        {
            return false;
        }

        if (raw is null)
        {
            return false;
        }

        ConnectionState state = this.GetOrCreateState(connection);
        if (!state.IsActive)
        {
            return false;
        }

        int priority = ClassifyPriorityIndex(raw.Span);

        if (_maxPerConnectionQueue > 0 && !this.EnsureCapacity(state, noBlock))
        {
            return false;
        }

        // Count the packet BEFORE it becomes visible to the dispatching worker. If the counters
        // were bumped after TryEnqueue, a worker could dequeue the packet first; its decrements
        // clamp at 0 and the late increments then leave phantom counts (TotalCount / TotalPackets
        // > 0 over empty queues). A phantom count makes Release() re-enqueue the connection
        // forever, so idle workers never park (busy spin). Counting first keeps every counter
        // >= the number of queued packets.
        _ = state.OnEnqueued(priority);
        long currentTotal = Interlocked.Increment(ref _packetCount.Value);

        if (!state.TryEnqueue(priority, raw))
        {
            if (_maxPerConnectionQueue > 0 &&
                (_dropPolicy is DropPolicy.DropOldest or DropPolicy.Coalesce) &&
                this.TryEvictOldest(state) &&
                state.TryEnqueue(priority, raw))
            {
                // retry succeeded
            }
            else
            {
                _ = state.OnDequeued(priority);
                DecrementNonNegative(ref _packetCount.Value);
                return false;
            }
        }

        long peak = Volatile.Read(ref _peakPacketCount);
        while (currentTotal > peak)
        {
            long prev = Interlocked.CompareExchange(ref _peakPacketCount, currentTotal, peak);
            if (prev == peak)
            {
                break;
            }

            peak = prev;
        }

        if (!state.IsActive)
        {

#pragma warning disable CA2000
            if (state.TryDequeue(priority, out IBufferLease? rolledBack))
            {
                rolledBack.Dispose();
                _ = state.OnDequeued(priority);
                DecrementNonNegative(ref _packetCount.Value);
            }
#pragma warning restore CA2000

            return false;
        }

        // Only enqueue the connection once when it transitions from "not ready"
        // to "ready"; the per-priority ready queue then acts as a wake-up list.
        if (state.TryMarkReady())
        {
            _ = Interlocked.Increment(ref _readyConnections);
            this.EnqueueReady(state, priority);
            readyEmitted = true;
        }

        return true;
    }

    #endregion Internal Methods

    #region Private Methods

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool EnsureCapacity(ConnectionState state, bool noBlock)
    {
        while (state.TotalCount >= _maxPerConnectionQueue)
        {
            switch (_dropPolicy)
            {
                case DropPolicy.DropNewest:
                    return false;
                case DropPolicy.DropOldest:
                case DropPolicy.Coalesce:
                    if (!this.TryEvictOldest(state))
                    {
                        return false;
                    }
                    continue;
                case DropPolicy.Block:
                    if (noBlock)
                    {
                        return false;
                    }
                    return this.WaitForQueueSpace(state);
                default:
                    return false;
            }
        }

        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool WaitForQueueSpace(ConnectionState state)
    {
        if (_blockTimeoutTicks <= 0)
        {
            return false;
        }

        long start = Stopwatch.GetTimestamp();
        int spin = 0;

        while (state.IsActive && state.TotalCount >= _maxPerConnectionQueue)
        {
            if (Stopwatch.GetTimestamp() - start >= _blockTimeoutTicks)
            {
                return false;
            }

            if (spin < 64)
            {
                Thread.SpinWait(4 << (spin & 7));
                spin++;
                continue;
            }

            if (spin < 128)
            {
                _ = Thread.Yield();
                spin++;
                continue;
            }

            Thread.Sleep(0);
        }

        return state.IsActive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryEvictOldest(ConnectionState state)
    {
        for (int p = LowestPriorityIndex; p <= HighestPriorityIndex; p++)
        {
            if (state.ReadPriorityCount(p) <= 0)
            {
                continue;
            }

#pragma warning disable CA2000
            if (!state.TryDequeue(p, out IBufferLease? evicted))
            {
                continue;
            }
#pragma warning restore CA2000

            evicted.Dispose();
            _ = state.OnDequeued(p);
            DecrementNonNegative(ref _packetCount.Value);
            _ = Interlocked.Increment(ref _totalEvicted);
            return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool TryDequeueHighest(ConnectionState state, [NotNullWhen(true)] out IBufferLease raw, out int dequeuedFrom)
    {
        // The mask tells us which priorities are non-empty without scanning all
        // queues. We always pop the highest bit first to preserve priority order.
        int mask = state.NonEmptyMask;

        while (mask != 0)
        {
            int priority = 31 - BitOperations.LeadingZeroCount((uint)mask);

            if (state.TryDequeue(priority, out raw))
            {
                dequeuedFrom = priority;
                return true;
            }

            state.ClearPriorityBitIfEmpty(priority);
            mask = state.NonEmptyMask;
        }

        // The mask can under-report: a dequeuer that took a priority's count to 0 may clear the
        // bit AFTER a concurrent producer re-set it for a new packet. Without this scan that
        // packet is stranded while TotalCount stays > 0, so the connection is re-queued and
        // claimed over and over without progress. Rare path: only when the mask found nothing.
        if (state.TotalCount > 0)
        {
            for (int priority = HighestPriorityIndex; priority >= LowestPriorityIndex; priority--)
            {
                if (state.TryDequeue(priority, out raw))
                {
                    dequeuedFrom = priority;
                    return true;
                }
            }
        }

        raw = null!;
        dequeuedFrom = -1;
        return false;
    }



    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void EnqueueReady(ConnectionState state, int priority)
    {
        if ((uint)priority > HighestPriorityIndex)
        {
            priority = LowestPriorityIndex;
        }

        // Count the entry BEFORE publishing it. If the increment came after Enqueue, a worker
        // could read the entry first and its DecrementNonNegative would clamp at 0; the late
        // increment then leaves the counter at 1 over an empty queue forever, so
        // HasClaimableConnection stays true and idle workers never park (busy spin).
        // Counting first keeps the counter >= the queue length at all times.
        // ConcurrentQueue<T>.Enqueue is unbounded and never fails (unlike Channel<T>.TryWrite,
        // which can in principle reject once completed), so there is no rollback path here.
        _ = Interlocked.Increment(ref _readyEntriesByPrio[priority]);
        _readyByPrio[priority].Enqueue(state);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private ConnectionState GetOrCreateState(IConnection connection)
    {
        int index = RuntimeHelpers.GetHashCode(connection) & _stateMask;

        // Fast path (lock-free): find an existing live node for this connection.
        for (Node? node = Volatile.Read(ref _stateBuckets[index]); node is not null; node = node.Next)
        {
            if (!ReferenceEquals(node.Connection, connection))
            {
                continue;
            }

            ConnectionState? existingState = node.State;
            if (existingState is null)
            {
                continue;
            }

            if (Volatile.Read(ref node.Removed) != 0 &&
                Interlocked.CompareExchange(ref node.Removed, 0, 1) == 1)
            {
                existingState.Reactivate();
                _ = Interlocked.Increment(ref _activeConnections);
            }

            return existingState;
        }

        // Slow path: no live node found. Serialize with RemoveConnection so we can either
        // recycle a tombstone in place or append exactly once — this bounds chain length to
        // peak concurrent connections per bucket instead of leaking a node per reconnect.
        return this.CreateOrReuseState(connection, index);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ConnectionState CreateOrReuseState(IConnection connection, int index)
    {
        lock (_stateMutationLock)
        {
            Node? tombstone = null;
            for (Node? node = _stateBuckets[index]; node is not null; node = node.Next)
            {
                // Another thread may have created a live node for this connection meanwhile.
                if (ReferenceEquals(node.Connection, connection) && node.State is ConnectionState live)
                {
                    if (Volatile.Read(ref node.Removed) != 0 &&
                        Interlocked.CompareExchange(ref node.Removed, 0, 1) == 1)
                    {
                        live.Reactivate();
                        _ = Interlocked.Increment(ref _activeConnections);
                    }

                    return live;
                }

                // A fully-cleared tombstone (removed, references broken) we can repopulate.
                tombstone ??= node.Connection is null && node.State is null && Volatile.Read(ref node.Removed) != 0
                    ? node
                    : null;
            }

            ConnectionState createdState = new(connection, _boundedPerPriorityMode, _boundedPerPriorityCapacity);

            if (tombstone is not null)
            {
                // Reuse in place. Publish State before Connection so the lock-free reader that
                // sees a non-null Connection is guaranteed to also see the matching State.
                tombstone.State = createdState;
                _ = Interlocked.Exchange(ref tombstone.Removed, 0);
                tombstone.Connection = connection;
            }
            else
            {
                Node? head = _stateBuckets[index];
                Node createdNode = new(connection, createdState, head);
                Volatile.Write(ref _stateBuckets[index], createdNode);
            }

            _ = Interlocked.Increment(ref _activeConnections);
            return createdState;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private bool TryFindNode(IConnection connection, [NotNullWhen(true)] out Node? found)
    {
        int index = RuntimeHelpers.GetHashCode(connection) & _stateMask;

        for (Node? node = Volatile.Read(ref _stateBuckets[index]); node is not null; node = node.Next)
        {
            IConnection? nodeConnection = node.Connection;
            if (nodeConnection is not null && ReferenceEquals(nodeConnection, connection))
            {
                found = node;
                return true;
            }
        }

        found = null;
        return false;
    }

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private void OnUnregistered(IConnection connection) => this.RemoveConnection(connection);

    [StackTraceHidden]
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private void RemoveConnection(IConnection connection)
    {
        if (connection is null)
        {
            return;
        }

        if (!this.TryFindNode(connection, out Node? node) || node is null)
        {
            return;
        }

        if (Interlocked.Exchange(ref node.Removed, 1) != 0)
        {
            return;
        }

        ConnectionState? state = node.State;
        if (state is null || !state.TryDeactivate())
        {
            return;
        }

        DecrementNonNegative(ref _activeConnections);

        if (state.TryReleaseReady())
        {
            DecrementNonNegative(ref _readyConnections);
        }

        int drained = state.DrainAndDisposeAll();
        if (drained > 0)
        {
            DecrementNonNegative(ref _packetCount.Value, drained);
        }

        // Break the tombstone node's strong references to the closed connection graph so it
        // retains no ConnectionState, Connection, SocketConnection, or Socket. Serialize with
        // CreateOrReuseState so this node can later be recycled in place. Clear Connection first
        // (stops new readers matching), then State — the mirror of the publish order on reuse.
        lock (_stateMutationLock)
        {
            node.Connection = null;
            node.State = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
    private static int ClassifyPriorityIndex(ReadOnlySpan<byte> span)
    {
        if ((uint)span.Length < PacketHeader.Size)
        {
            return LowestPriorityIndex;
        }

        ref readonly PacketHeader header = ref span.AsHeaderRef();
        return (uint)header.Priority <= HighestPriorityIndex ? (int)header.Priority : LowestPriorityIndex;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int RoundUpToPowerOf2(int value)
    {
        uint rounded = BitOperations.RoundUpToPowerOf2((uint)value);
        return rounded == 0 ? int.MaxValue : (int)Math.Min(rounded, int.MaxValue);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int GetBucketCount(DispatchOptions options)
    {
        int target = Math.Clamp(System.Environment.ProcessorCount * options.BucketCountMultiplier, options.MinBucketCount, options.MaxBucketCount);
        uint rounded = BitOperations.RoundUpToPowerOf2((uint)target);
        return rounded == 0 ? options.MinBucketCount : (int)rounded;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ToStopwatchTicks(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return 0;
        }

        double ticks = timeout.TotalSeconds * Stopwatch.Frequency;
        if (ticks <= 0d)
        {
            return 0;
        }

        if (ticks >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return (long)ticks;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecrementNonNegative(ref int value)
    {
        int after = Interlocked.Decrement(ref value);
        if (after < 0)
        {
            _ = Interlocked.Exchange(ref value, 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecrementNonNegative(ref long value)
    {
        long after = Interlocked.Decrement(ref value);
        if (after < 0)
        {
            _ = Interlocked.Exchange(ref value, 0);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void DecrementNonNegative(ref long value, int amount)
    {
        if (amount <= 0)
        {
            return;
        }

        long after = Interlocked.Add(ref value, -amount);
        if (after < 0)
        {
            _ = Interlocked.Exchange(ref value, 0);
        }
    }

    #endregion Private Methods

    #region Nested Types

    /// <summary>
    /// Grants exclusive processing rights over a single connection's mailbox.
    /// While the session is active, only the holder may dequeue packets from
    /// the underlying connection, guaranteeing strict in-order delivery.
    /// </summary>
    [SkipLocalsInit]
    private struct DispatchSession : IDispatchSession
    {
        private readonly ConnectionState _state;
        private readonly DispatchChannel<TPacket> _owner;
        private int _disposed;

        public DispatchSession(ConnectionState state, DispatchChannel<TPacket> owner)
        {
            _state = state;
            _owner = owner;
            _disposed = 0;
        }

        /// <inheritdoc/>
        public readonly IConnection Connection => _state.Connection;

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDequeue([NotNullWhen(true)] out IBufferLease raw)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                raw = null!;
                return false;
            }

            ConnectionState state = _state;
            if (!state.IsActive || state.TotalCount <= 0)
            {
                raw = null!;
                return false;
            }

            if (TryDequeueHighest(state, out raw, out int dequeuedFrom))
            {
                _ = state.OnDequeued(dequeuedFrom);
                DecrementNonNegative(ref _owner._packetCount.Value);
                return true;
            }

            raw = null!;
            return false;
        }

        /// <summary>
        /// Releases the exclusive claim. If the connection still has pending
        /// packets, it is re-enqueued into the ready queue for any worker
        /// to claim next.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _owner.Release(_state);
        }
    }

    private sealed class Node(IConnection connection, ConnectionState state, Node? next)
    {
        public int Removed;

        public readonly Node? Next = next;

        // Mutable so RemoveConnection can null them to break the tombstone's
        // retention of the closed connection graph (Connection -> SocketConnection
        // -> Socket), and so a later GetOrCreateState can repopulate the tombstone
        // in place instead of appending a new node (bounds chain length to peak
        // concurrent connections/bucket rather than total-ever connections).
        //
        // Volatile: the lock-free reader in GetOrCreateState/TryFindNode observes
        // these without holding _stateMutationLock. Connection is always published
        // AFTER State so a reader that sees a non-null Connection also sees its State.
        public volatile ConnectionState? State = state;
        public volatile IConnection? Connection = connection;
    }

    private sealed class UnboundedQueue
    {
        private readonly Channel<IBufferLease> _channel = Channel.CreateUnbounded<IBufferLease>(
            new UnboundedChannelOptions
            {
                // SingleReader = true: only one DispatchSession consumer holds
                // exclusive access via Interlocked.Exchange(ref _disposed, 1) in
                // DispatchSession.Dispose.  No concurrent readers are possible.
                //
                // SingleWriter = false: MPSC — multiple ThreadPool process callbacks
                // (up to MaxPerConnectionPendingPackets = 16) can call TryEnqueue
                // concurrently for the same connection.
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryEnqueue(IBufferLease lease) => _channel.Writer.TryWrite(lease);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDequeue([NotNullWhen(true)] out IBufferLease lease) => _channel.Reader.TryRead(out lease!);
    }

    private sealed class MpmcRing
    {
        private struct Slot
        {
            public long Sequence;
            public IBufferLease? Item;
        }

        private readonly Slot[] _slots;
        private readonly int _mask;
        private PaddedSequence _enqueuePos;
        private PaddedSequence _dequeuePos;

        public MpmcRing(int capacity)
        {
            capacity = RoundUpToPowerOf2(Math.Max(2, capacity));
            _slots = new Slot[capacity];
            _mask = capacity - 1;

            for (int i = 0; i < capacity; i++)
            {
                _slots[i].Sequence = i;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryEnqueue([Borrowed] IBufferLease lease)
        {
            SpinWait spin = default;

            while (true)
            {
                long pos = Volatile.Read(ref _enqueuePos.Value);
                ref Slot slot = ref _slots[(int)(pos & _mask)];
                long seq = Volatile.Read(ref slot.Sequence);
                long diff = seq - pos;

                if (diff == 0)
                {
                    // Sequence matches the enqueue position, so this slot is free.
                    // The slot is reserved with a CAS on the producer cursor so two
                    // writers never claim the same slot at once.
                    if (Interlocked.CompareExchange(ref _enqueuePos.Value, pos + 1, pos) == pos)
                    {
                        slot.Item = lease;
                        // Publish the item by advancing the slot sequence. The
                        // consumer will not observe this slot until the sequence
                        // number moves forward, which acts as the release fence.
                        Volatile.Write(ref slot.Sequence, pos + 1);
                        return true;
                    }

                    continue;
                }

                if (diff < 0)
                {
                    // The slot sequence is behind the producer cursor, so the ring
                    // is full at this position and the caller must retry later.
                    // This is the backpressure signal that prevents overwriting data.
                    return false;
                }

                spin.SpinOnce();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDequeue([NotNullWhen(true)] out IBufferLease lease)
        {
            SpinWait spin = default;

            while (true)
            {
                long pos = Volatile.Read(ref _dequeuePos.Value);
                ref Slot slot = ref _slots[(int)(pos & _mask)];
                long seq = Volatile.Read(ref slot.Sequence);
                long diff = seq - (pos + 1);

                if (diff == 0)
                {
                    // Sequence matches the dequeue position, so this slot has data.
                    // The consumer wins the slot with a CAS on the dequeue cursor.
                    if (Interlocked.CompareExchange(ref _dequeuePos.Value, pos + 1, pos) == pos)
                    {
                        IBufferLease? item = slot.Item;
                        slot.Item = null;
                        // Move the sequence forward by one full ring to mark the slot
                        // free for the next producer lap.
                        Volatile.Write(ref slot.Sequence, pos + _slots.Length);

                        if (item is null)
                        {
                            lease = null!;
                            return false;
                        }

                        lease = item;
                        return true;
                    }

                    continue;
                }

                if (diff < 0)
                {
                    // The producer has not published anything for this position yet.
                    // Spin until the write becomes visible or the slot is confirmed empty.
                    lease = null!;
                    return false;
                }

                spin.SpinOnce();
            }
        }
    }

    private sealed class ConnectionState
    {
        #region Fields

        private readonly bool _boundedMode;
        private readonly int _boundedCapacity;
        private readonly IConnection _connection;

        private readonly MpmcRing?[] _boundedQueues = new MpmcRing[PriorityLevels];
        private readonly UnboundedQueue?[] _unboundedQueues = new UnboundedQueue[PriorityLevels];
        private readonly int[] _priorityCounts = new int[PriorityLevels];

        private int _readyFlag;
        private int _activeFlag;
        private int _nonEmptyMask;
        private int _totalCount;

        #endregion Fields

        #region Constructor

        public ConnectionState(IConnection connection, bool boundedMode, int boundedCapacity)
        {
            _activeFlag = 1;

            _connection = connection;
            _boundedMode = boundedMode;
            _boundedCapacity = boundedCapacity;
        }

        #endregion Constructor

        #region Properties

        public IConnection Connection => _connection;

        public int TotalCount => Volatile.Read(ref _totalCount);

        public int NonEmptyMask => Volatile.Read(ref _nonEmptyMask);

        public bool IsActive => Volatile.Read(ref _activeFlag) == 1;

        #endregion Properties

        #region APIs

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void Reactivate() => _ = Interlocked.Exchange(ref _activeFlag, 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDeactivate() => Interlocked.Exchange(ref _activeFlag, 0) == 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryMarkReady() => this.IsActive && Interlocked.CompareExchange(ref _readyFlag, 1, 0) == 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryReleaseReady() => Interlocked.Exchange(ref _readyFlag, 0) == 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int ReadPriorityCount(int priority) => Volatile.Read(ref _priorityCounts[priority]);

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int GetHighestPriority()
        {
            int mask = Volatile.Read(ref _nonEmptyMask);
            // Highest set bit == highest non-empty priority, so we can jump
            // directly to the hottest queue without scanning every priority.
            return mask == 0 ? -1 : 31 - BitOperations.LeadingZeroCount((uint)mask);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryEnqueue(int priority, IBufferLease lease)
        {
            if (_boundedMode)
            {
                return this.GetOrCreateBoundedQueue(priority).TryEnqueue(lease);
            }

            return this.GetOrCreateUnboundedQueue(priority).TryEnqueue(lease);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDequeue(int priority, [NotNullWhen(true)] out IBufferLease lease)
        {
            if (_boundedMode)
            {
                MpmcRing? queue = Volatile.Read(ref _boundedQueues[priority]);
                if (queue is not null && queue.TryDequeue(out lease))
                {
                    return true;
                }

                lease = null!;
                return false;
            }

            UnboundedQueue? unbounded = Volatile.Read(ref _unboundedQueues[priority]);
            if (unbounded is not null && unbounded.TryDequeue(out lease))
            {
                return true;
            }

            lease = null!;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public bool TryDequeueAny([NotNullWhen(true)] out IBufferLease lease, out int priority)
        {
            for (int p = LowestPriorityIndex; p <= HighestPriorityIndex; p++)
            {
                if (this.TryDequeue(p, out lease))
                {
                    priority = p;
                    return true;
                }
            }

            lease = null!;
            priority = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int OnEnqueued(int priority)
        {
            int next = Interlocked.Increment(ref _priorityCounts[priority]);
            if (next == 1)
            {
                // First item in this priority range: mark it non-empty in the bitset
                // so Pull() can find this priority without scanning all queues.
                this.SetPriorityBit(priority);
            }

            return Interlocked.Increment(ref _totalCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public int OnDequeued(int priority)
        {
            int next = Interlocked.Decrement(ref _priorityCounts[priority]);
            if (next <= 0)
            {
                if (next < 0)
                {
                    // Counter underflow is corrected here so the state stays usable
                    // even if a dequeue path races with a drain/reset path.
                    _ = Interlocked.Exchange(ref _priorityCounts[priority], 0);
                }

                // When the last item leaves a priority, clear its bit so scans can
                // skip the queue entirely on future pulls.
                this.ClearPriorityBit(priority);
            }

            int remaining = Interlocked.Decrement(ref _totalCount);
            if (remaining >= 0)
            {
                return remaining;
            }

            _ = Interlocked.Exchange(ref _totalCount, 0);
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        public void ClearPriorityBitIfEmpty(int priority)
        {
            // If a dequeue raced and the queue became empty, make sure the bitset
            // does not keep advertising this priority as available.
            if (this.ReadPriorityCount(priority) <= 0)
            {
                this.ClearPriorityBit(priority);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
        public int DrainAndDisposeAll()
        {
            int drained = 0;

#pragma warning disable CA2000
            while (this.TryDequeueAny(out IBufferLease? lease, out int priority))
            {
                lease.Dispose();
                _ = this.OnDequeued(priority);
                drained++;
            }
#pragma warning restore CA2000

            // After draining, force every counter and bit back to a clean idle
            // state so a later reuse starts from known-zero bookkeeping.
            for (int i = 0; i < _priorityCounts.Length; i++)
            {
                _ = Interlocked.Exchange(ref _priorityCounts[i], 0);
            }

            _ = Interlocked.Exchange(ref _totalCount, 0);
            _ = Interlocked.Exchange(ref _nonEmptyMask, 0);
            _ = Interlocked.Exchange(ref _readyFlag, 0);

            return drained;
        }

        #endregion APIs

        #region Private Methods

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private MpmcRing GetOrCreateBoundedQueue(int priority)
        {
            MpmcRing? current = Volatile.Read(ref _boundedQueues[priority]);
            if (current is not null)
            {
                return current;
            }

            MpmcRing created = new(_boundedCapacity);
            MpmcRing? prior = Interlocked.CompareExchange(ref _boundedQueues[priority], created, null);
            return prior ?? created;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private UnboundedQueue GetOrCreateUnboundedQueue(int priority)
        {
            UnboundedQueue? current = Volatile.Read(ref _unboundedQueues[priority]);
            if (current is not null)
            {
                return current;
            }

            UnboundedQueue created = new();
            UnboundedQueue? prior = Interlocked.CompareExchange(ref _unboundedQueues[priority], created, null);
            return prior ?? created;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private void SetPriorityBit(int priority)
        {
            int bit = 1 << priority;

            while (true)
            {
                int mask = Volatile.Read(ref _nonEmptyMask);
                if ((mask & bit) != 0)
                {
                    return;
                }

                // CAS loop avoids losing concurrent updates to other priority bits.
                if (Interlocked.CompareExchange(ref _nonEmptyMask, mask | bit, mask) == mask)
                {
                    return;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining | MethodImplOptions.AggressiveOptimization)]
        private void ClearPriorityBit(int priority)
        {
            int bit = 1 << priority;
            int clearMask = ~bit;

            while (true)
            {
                int mask = Volatile.Read(ref _nonEmptyMask);
                if ((mask & bit) == 0)
                {
                    return;
                }

                // Same CAS pattern as SetPriorityBit, but clearing the single bit.
                if (Interlocked.CompareExchange(ref _nonEmptyMask, mask & clearMask, mask) == mask)
                {
                    return;
                }
            }
        }

        #endregion Private Methods
    }

    #endregion Nested Types
}
