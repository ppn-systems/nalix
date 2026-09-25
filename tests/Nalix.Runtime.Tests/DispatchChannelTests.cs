// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Runtime.Dispatching;
using Nalix.Runtime.Internal.Routing;
using Xunit;

namespace Nalix.Runtime.Tests;

#if DEBUG
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class DispatchChannelTests
{
    #region Test Case 1: Strict In-Order Delivery

    /// <summary>
    /// Chứng minh lỗi race condition: khi nhiều consumer cùng TryClaim trên cùng 1 connection,
    /// packet bị dequeue ra không đúng thứ tự (out-of-order).
    /// </summary>
    [Fact]
    public void Should_Process_Packets_Strictly_In_Order_Under_High_Concurrency()
    {
        // Arrange
        const int packetCount = 2000;
        const int consumerCount = 4;

        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        List<int> dequeuedSequences = [];
        object lockObj = new();
        int consumedCount = 0;

        // Act: Producer push tuần tự, Consumers chạy đa luồng
        // Kịch bản: push 1 packet -> yield -> consumer khác có cơ hội claim cùng lúc
        Task producer = Task.Run(() =>
        {
            for (int seq = 1; seq <= packetCount; seq++)
            {
                FakeBufferLease lease = CreatePacketLease(seq, PacketPriority.NONE);
                channel.Push(connection, lease);

                // Nhường CPU định kỳ để tăng window cho race condition
                if (seq % 50 == 0)
                {
                    Thread.Yield();
                }
            }
        });

        Task[] consumers = new Task[consumerCount];
        for (int c = 0; c < consumerCount; c++)
        {
            consumers[c] = Task.Run(() =>
            {
                while (Volatile.Read(ref consumedCount) < packetCount)
                {
                    if (!channel.TryClaim(out IDispatchSession? session))
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    using (session)
                    {
                        while (session.TryDequeue(out IBufferLease? raw))
                        {
                            int seq = ReadSequenceId(raw);
                            lock (lockObj)
                            {
                                dequeuedSequences.Add(seq);
                            }
                            raw.Dispose();
                            _ = Interlocked.Increment(ref consumedCount);
                        }
                    }
                }
            });
        }

        Task.WaitAll([producer, .. consumers]);

        // Assert: Thứ tự PHẢI tăng dần đều (1, 2, 3, ...)
        // Nếu bug tồn tại, test sẽ FAIL tại đây vì packet bị out-of-order.
        Assert.Equal(packetCount, dequeuedSequences.Count);

        for (int i = 0; i < dequeuedSequences.Count; i++)
        {
            int expected = i + 1;
            if (dequeuedSequences[i] != expected)
            {
                Assert.Fail(
                    $"Out-of-order delivery detected at index {i}: " +
                    $"expected {expected}, got {dequeuedSequences[i]}. " +
                    $"Exclusivity violated: multiple consumers dequeued from the same connection.");
            }
        }
    }

    #endregion

    #region Test Case 2: WDRR Load Balance

    /// <summary>
    /// Kiểm tra DispatchChannel phân phối load công bằng giữa nhiều connection (WDRR).
    /// </summary>
    [Fact]
    public void Should_Distribute_Load_Fairly_Between_Connections()
    {
        // Arrange
        const int connectionCount = 4;
        const int packetsPerConnection = 500;

        using DispatchChannel<FakePacket> channel = new();

        FakeConnection[] connections = new FakeConnection[connectionCount];
        for (int i = 0; i < connectionCount; i++)
        {
            connections[i] = new FakeConnection();
        }

        // Push packets cho từng connection
        for (int c = 0; c < connectionCount; c++)
        {
            for (int seq = 1; seq <= packetsPerConnection; seq++)
            {
                FakeBufferLease lease = CreatePacketLease(seq, PacketPriority.NONE);
                channel.Push(connections[c], lease);
            }
        }

        int totalExpected = connectionCount * packetsPerConnection;
        int consumedTotal = 0;
        ConcurrentDictionary<IConnection, int> perConnection = new();

        // Act: Claim và dequeue tất cả
        while (Volatile.Read(ref consumedTotal) < totalExpected)
        {
            if (!channel.TryClaim(out IDispatchSession? session))
            {
                Thread.SpinWait(4);
                continue;
            }

            IConnection claimedConn = session.Connection;
            _ = perConnection.TryAdd(claimedConn, 0);

            using (session)
            {
                while (session.TryDequeue(out IBufferLease? raw))
                {
                    _ = perConnection.AddOrUpdate(claimedConn, 1, (_, v) => v + 1);
                    raw.Dispose();
                    _ = Interlocked.Increment(ref consumedTotal);
                }
            }
        }

        // Assert: Mỗi connection phải nhận đúng số packet đã push
        Assert.Equal(totalExpected, consumedTotal);
        Assert.Equal(connectionCount, perConnection.Count);

        foreach (KeyValuePair<IConnection, int> kvp in perConnection)
        {
            Assert.True(
                kvp.Value == packetsPerConnection,
                $"Connection unfairness: expected {packetsPerConnection} packets, got {kvp.Value}");
        }
    }

    #endregion

    #region Test Case 3: RemoveConnection clears node Connection reference

    [Fact]
    public void DispatchChannel_RemoveConnection_ClearsNodeConnectionReference()
    {
        // Arrange
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        FakeBufferLease lease = CreatePacketLease(1, PacketPriority.NONE);
        channel.Push(connection, lease);

        // Verify node exists with non-null Connection before removal
        object? nodeBefore = FindNodeForConnection(channel, connection);
        Assert.NotNull(nodeBefore);
        Assert.NotNull(GetNodeFieldValue(channel, nodeBefore!, "Connection"));

        // Simulate ConnectionUnregistered by calling RemoveConnection via reflection
        InvokeRemoveConnection(channel, connection);

        // Assert: After removal, all nodes with Removed=1 must have Connection=null.
        // (FindNodeForConnection won't find cleared nodes, so walk all buckets directly.)
        bool foundClearedNode = false;
        FieldInfo removedField = GetNodeField(channel, "Removed");
        FieldInfo connectionField = GetNodeField(channel, "Connection");

        foreach (object? node in GetAllNodes(channel))
        {
            int removed = (int)removedField.GetValue(node)!;
            if (removed != 0)
            {
                object? connValue = connectionField.GetValue(node);
                Assert.Null(connValue);
                foundClearedNode = true;
            }
        }

        Assert.True(foundClearedNode, "Expected at least one removed (tombstone) node with Connection=null.");
    }

    #endregion

    #region Test Case 4: RemoveConnection clears node State reference

    [Fact]
    public void DispatchChannel_RemoveConnection_ClearsNodeStateReference()
    {
        // Arrange
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        FakeBufferLease lease = CreatePacketLease(1, PacketPriority.NONE);
        channel.Push(connection, lease);

        // Verify node exists with non-null State before removal
        object? nodeBefore = FindNodeForConnection(channel, connection);
        Assert.NotNull(nodeBefore);
        Assert.NotNull(GetNodeFieldValue(channel, nodeBefore!, "State"));

        // Simulate ConnectionUnregistered
        InvokeRemoveConnection(channel, connection);

        // Assert: After removal, all nodes with Removed=1 must have State=null.
        bool foundClearedNode = false;
        FieldInfo removedField = GetNodeField(channel, "Removed");
        FieldInfo stateField = GetNodeField(channel, "State");

        foreach (object? node in GetAllNodes(channel))
        {
            int removed = (int)removedField.GetValue(node)!;
            if (removed != 0)
            {
                object? stateValue = stateField.GetValue(node);
                Assert.Null(stateValue);
                foundClearedNode = true;
            }
        }

        Assert.True(foundClearedNode, "Expected at least one removed (tombstone) node with State=null.");
    }

    #endregion

    #region Test Case 5: RemoveConnection is idempotent

    [Fact]
    public void DispatchChannel_RemoveConnection_IsIdempotent()
    {
        // Arrange
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        FakeBufferLease lease = CreatePacketLease(1, PacketPriority.NONE);
        channel.Push(connection, lease);

        // Act: Call RemoveConnection multiple times — no exception should be thrown
        InvokeRemoveConnection(channel, connection);
        InvokeRemoveConnection(channel, connection);
        InvokeRemoveConnection(channel, connection);

        // Assert: No exception thrown, all tombstoned nodes have cleared references
        FieldInfo removedField = GetNodeField(channel, "Removed");
        FieldInfo connectionField = GetNodeField(channel, "Connection");
        FieldInfo stateField = GetNodeField(channel, "State");

        foreach (object? node in GetAllNodes(channel))
        {
            int removed = (int)removedField.GetValue(node)!;
            if (removed != 0)
            {
                Assert.Null(connectionField.GetValue(node));
                Assert.Null(stateField.GetValue(node));
            }
        }
    }

    #endregion

    #region Test Case 6: Removed node is skipped by traversal

    [Fact]
    public void DispatchChannel_RemovedNode_IsSkippedByTraversal()
    {
        // Arrange
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        FakeBufferLease lease = CreatePacketLease(1, PacketPriority.NONE);
        channel.Push(connection, lease);

        // Remove the connection (simulating ConnectionUnregistered event)
        InvokeRemoveConnection(channel, connection);

        // Act: TryClaim should not return the removed connection
        bool claimed = channel.TryClaim(out IDispatchSession? session);
        if (claimed)
        {
            using (session)
            {
                // If a session was claimed, it must NOT be for the removed connection
                Assert.NotSame(connection, session!.Connection);
            }
        }

        // The removed connection should have 0 pending packets
        Assert.Equal(0, channel.TotalPackets);
    }

    #endregion

    #region Test Case 7: RemoveConnection drains pending packets

    [Fact]
    public void DispatchChannel_RemoveConnection_DrainsPendingPackets()
    {
        // Arrange
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        const int packetCount = 10;
        for (int i = 1; i <= packetCount; i++)
        {
            FakeBufferLease lease = CreatePacketLease(i, PacketPriority.NONE);
            channel.Push(connection, lease);
        }

        Assert.Equal(packetCount, channel.TotalPackets);

        // Act: Remove the connection
        InvokeRemoveConnection(channel, connection);

        // Assert: All pending packets must have been drained
        Assert.Equal(0, channel.TotalPackets);
    }

    #endregion

    #region Test Case 7b: Stale ready entry with exhausted budget is purged

    /// <summary>
    /// Regression for the idle busy-spin after #369: a removed connection leaves a stale entry in
    /// the ready queue after its packets are drained. When the priority budget was already spent,
    /// TryClaim never reset it (HasPacket == false), so the entry was never purged and
    /// HasClaimableConnection stayed true forever, keeping dispatch workers from parking.
    /// </summary>
    [Fact]
    public void TryClaim_StaleEntryWithExhaustedBudget_PurgesEntrySoWorkersCanPark()
    {
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection[] live = [new(), new()];
        FakeConnection dead = new();

        // NONE priority has weight 1: two claims exhaust its budget.
        foreach (FakeConnection c in live)
        {
            channel.Push(c, CreatePacketLease(1, PacketPriority.NONE));
        }

        channel.Push(dead, CreatePacketLease(1, PacketPriority.NONE));

        for (int i = 0; i < live.Length; i++)
        {
            Assert.True(channel.TryClaim(out IDispatchSession? s));
            using (s)
            {
                while (s.TryDequeue(out IBufferLease? lease))
                {
                    lease.Dispose();
                }
            }
        }

        InvokeRemoveConnection(channel, dead);
        Assert.Equal(0, channel.TotalPackets);

        Assert.False(channel.TryClaim(out _));
        Assert.False(channel.HasClaimableConnection);
        Assert.Equal(0, channel.ReadyConnections);
    }

    #endregion

    #region Test Case 7c: Ready-entry counter never drifts above the queue

    /// <summary>
    /// Regression for the idle busy-spin after #369: EnqueueReady published the entry before
    /// counting it, so a racing TryClaim could read it and clamp the decrement at 0; the late
    /// increment then left a phantom ready entry that kept HasClaimableConnection true forever.
    /// After a concurrent push/claim/remove storm fully drains, nothing may look claimable.
    /// </summary>
    [Fact]
    public async Task ConcurrentPushClaimRemove_AfterDrain_NoPhantomClaimableEntries()
    {
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection[] conns = new FakeConnection[16];
        for (int i = 0; i < conns.Length; i++)
        {
            conns[i] = new FakeConnection();
        }

        using CancellationTokenSource stop = new();
        Task[] consumers = new Task[4];
        for (int c = 0; c < consumers.Length; c++)
        {
            consumers[c] = Task.Run(() =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!channel.TryClaim(out IDispatchSession? session))
                    {
                        Thread.SpinWait(4);
                        continue;
                    }

                    using (session)
                    {
                        if (session.TryDequeue(out IBufferLease? lease))
                        {
                            lease.Dispose();
                        }
                    }
                }
            });
        }

        Task[] producers = new Task[conns.Length];
        for (int p = 0; p < conns.Length; p++)
        {
            FakeConnection conn = conns[p];
            bool remove = (p & 3) == 0;
            producers[p] = Task.Run(() =>
            {
                for (int n = 1; n <= 5_000; n++)
                {
                    channel.Push(conn, CreatePacketLease(n, (n & 7) == 0 ? PacketPriority.HIGH : PacketPriority.NONE));
                }

                if (remove)
                {
                    InvokeRemoveConnection(channel, conn);
                }
            });
        }

        await Task.WhenAll(producers);

        System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
        while ((channel.TotalPackets > 0 || channel.HasClaimableConnection) && sw.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(5);
        }

        stop.Cancel();
        await Task.WhenAll(consumers);

        int[] pending = new int[8];
        channel.CopyPendingPerPriority(pending);
        Assert.True(channel.TotalPackets == 0 && !channel.HasClaimableConnection,
            $"total={channel.TotalPackets} ready={channel.ReadyConnections} pending=[{string.Join(',', pending)}]");
        Assert.Equal(0, channel.TotalPackets);
        Assert.False(channel.HasClaimableConnection);
    }

    #endregion

    #region Test Case 8: Weak reference — removed connection is GC-collectible

    [Fact]
    public void DispatchChannel_RemoveConnection_ConnectionIsGcCollectible()
    {
        // Arrange: Create channel and connection in a helper to avoid root on stack
        WeakReference weakRef = RegisterPushRemoveAndDropReference();

        // Act: Force full GC
        ForceFullGC();

        // Assert: The connection should have been collected
        Assert.False(weakRef.IsAlive, "Connection should be GC-collectible after RemoveConnection clears node references. " +
                                      "If this fails, the DispatchChannel node tombstone is still retaining the connection graph.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RegisterPushRemoveAndDropReference()
    {
        DispatchChannel<FakePacket> channel = new();
        FakeConnection connection = new();

        FakeBufferLease lease = CreatePacketLease(1, PacketPriority.NONE);
        channel.Push(connection, lease);

        InvokeRemoveConnection(channel, connection);

        WeakReference weakRef = new(connection);

        // Drop the strong reference
        connection = null!;

        // Keep channel alive (it's a GC root via InstanceManager in production,
        // but here we just hold it to prove the node doesn't retain the connection)
        GC.KeepAlive(channel);
        channel.Dispose();

        return weakRef;
    }

    #endregion

    #region Test Case 9: Pending priority copy avoids caller allocation

    [Fact]
    public void DispatchChannel_CopyPendingPerPriority_CopiesSnapshotIntoCallerBuffer()
    {
        using DispatchChannel<FakePacket> channel = new();
        FakeConnection lowConnection = new();
        FakeConnection urgentConnection = new();

        channel.Push(lowConnection, CreatePacketLease(1, PacketPriority.LOW));
        channel.Push(urgentConnection, CreatePacketLease(2, PacketPriority.URGENT));

        Span<int> snapshot = stackalloc int[(int)PacketPriority.URGENT + 1];
        channel.CopyPendingPerPriority(snapshot);

        int[] allocatedSnapshot = channel.PendingPerPriority;
        for (int i = 0; i < allocatedSnapshot.Length; i++)
        {
            Assert.Equal(allocatedSnapshot[i], snapshot[i]);
        }

        int[] tooSmall = new int[(int)PacketPriority.URGENT];
        Assert.Throws<ArgumentException>(() => channel.CopyPendingPerPriority(tooSmall));
    }

    #endregion

    #region Reflection Helpers (test-only access to private Node type)

    private static void ForceFullGC()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    }

    private static Type GetNodeType<TPacket>(DispatchChannel<TPacket> channel) where TPacket : IPacket
    {
        // Derive the closed Node type from the _stateBuckets field's element type.
        // This avoids the ContainsGenericParameters issue with GetNestedType on generic parents.
        FieldInfo? bucketsField = typeof(DispatchChannel<TPacket>).GetField("_stateBuckets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(bucketsField);
        Type? elementType = bucketsField!.FieldType.GetElementType();
        Assert.NotNull(elementType);
        Assert.False(elementType!.ContainsGenericParameters, "Node element type should be closed.");
        return elementType;
    }

    private static FieldInfo GetNodeField<TPacket>(DispatchChannel<TPacket> channel, string fieldName) where TPacket : IPacket
    {
        Type nodeType = GetNodeType(channel);
        FieldInfo? field = nodeType.GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!;
    }

    private static Array GetStateBuckets<TPacket>(DispatchChannel<TPacket> channel) where TPacket : IPacket
    {
        FieldInfo? field = typeof(DispatchChannel<TPacket>).GetField("_stateBuckets", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        object? value = field!.GetValue(channel);
        Assert.NotNull(value);
        return (Array)value!;
    }

    private static object? FindNodeForConnection<TPacket>(DispatchChannel<TPacket> channel, IConnection connection) where TPacket : IPacket
    {
        Array buckets = GetStateBuckets(channel);
        FieldInfo connectionField = GetNodeField(channel, "Connection");
        FieldInfo nextField = GetNodeField(channel, "Next");

        for (int i = 0; i < buckets.Length; i++)
        {
            object? node = buckets.GetValue(i);
            while (node is not null)
            {
                object? nodeConnection = connectionField.GetValue(node);
                if (ReferenceEquals(nodeConnection, connection))
                {
                    return node;
                }
                node = nextField.GetValue(node);
            }
        }
        return null;
    }

    private static List<object> GetAllNodes<TPacket>(DispatchChannel<TPacket> channel) where TPacket : IPacket
    {
        List<object> nodes = [];
        Array buckets = GetStateBuckets(channel);
        FieldInfo nextField = GetNodeField(channel, "Next");

        for (int i = 0; i < buckets.Length; i++)
        {
            object? node = buckets.GetValue(i);
            while (node is not null)
            {
                nodes.Add(node);
                node = nextField.GetValue(node);
            }
        }
        return nodes;
    }

    private static object? GetNodeFieldValue<TPacket>(DispatchChannel<TPacket> channel, object node, string fieldName) where TPacket : IPacket
    {
        FieldInfo field = GetNodeField(channel, fieldName);
        return field.GetValue(node);
    }

    private static void InvokeRemoveConnection<TPacket>(DispatchChannel<TPacket> channel, IConnection connection) where TPacket : IPacket
    {
        MethodInfo? method = typeof(DispatchChannel<TPacket>).GetMethod("RemoveConnection", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(channel, [connection]);
    }

    #endregion

    #region Helpers

    private static FakeBufferLease CreatePacketLease(int sequence, PacketPriority priority)
    {
        byte[] buffer = new byte[PacketHeader.Size];
        PacketHeader header = default;
        header.OpCode = 0x0001;
        header.Priority = priority;
        header.SequenceId = (ushort)sequence;

        MemoryMarshal.Write(buffer, in header);
        return new FakeBufferLease(buffer);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadSequenceId(IBufferLease raw)
    {
        ReadOnlySpan<byte> span = raw.Span;
        ref readonly PacketHeader header = ref MemoryMarshal.AsRef<PacketHeader>(span);
        return header.SequenceId;
    }

    #endregion

    #region Fakes

    private sealed class FakePacket : IPacket
    {
        public int Length => PacketHeader.Size;
        public PacketHeader Header { get; set; }
        public byte[] Serialize() => [];
        public int Serialize(Span<byte> buffer) => 0;
    }

    private sealed class FakeBufferLease : IBufferLease
    {
        private readonly byte[] _buffer;
        private int _disposed;

        public FakeBufferLease(byte[] buffer) => _buffer = buffer;

        public int Length => _buffer.Length;
        public bool IsReliable { get; set; }
        public bool EncryptedOnWire { get; set; }
        public int Capacity => _buffer.Length;
        public Span<byte> Span => _disposed != 0 ? throw new ObjectDisposedException(nameof(FakeBufferLease)) : _buffer;
        public Span<byte> SpanFull => Span;
        public ReadOnlyMemory<byte> Memory => _buffer;

        public void Retain() { }
        public void CommitLength(int length) { }
        public bool ReleaseOwnership(out byte[]? buffer, out int start, out int length)
        {
            buffer = _buffer; start = 0; length = _buffer.Length; return true;
        }

        public void Dispose()
        {
            _ = Interlocked.Exchange(ref _disposed, 1);
        }
    }

    private sealed class FakeConnection : IConnection
    {
        public bool IsDisposed => false;
        public bool IsUdpCreated => false;
        public ulong ConnectionId => 0;
        public string? UserId { get; set; }
        public long UpTime => 0;
        public long LastPingTime => 0;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IOpCodeExtractor PacketClassifier => null!;
        public INetworkEndpoint NetworkEndpoint => null!;
        public IObjectMap<AttributeKey, object> Attributes => null!;
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public Bytes32 Secret { get; set; }
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }

        public IConnection.ITransport TCP => null!;
        public IConnection.ITransport UDP => null!;

#pragma warning disable CS0067
        public event EventHandler<IConnectionEventArgs>? ConnectionClosed;
        public event EventHandler<IConnectionEventArgs>? MessageProcessing;
        public event EventHandler<IConnectionEventArgs>? MessageProcessed;
#pragma warning restore CS0067

        public void Disconnect(string? reason = null) { }
        public void Dispose() { }
        public int ErrorCount => 0;
        public void IncrementErrorCount() { }

        public int IdleTimeoutMs { get; set; } = 60000;
        public void UpdateIdleTimeout(int newTimeoutMs) { this.IdleTimeoutMs = newTimeoutMs; }
    }

    #endregion
}
#endif
