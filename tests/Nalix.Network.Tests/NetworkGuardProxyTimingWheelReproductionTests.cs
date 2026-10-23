// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Configuration;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Udp;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class NetworkGuardProxyTimingWheelReproductionTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private static ConnectionGuard CreateIsolatedGuard(ConnectionQuotaOptions options)
    {
        ConfigurationManager.Instance.Get<ConnectionBlacklistStoreOptions>().Enabled = false;
        return new ConnectionGuard(options);
    }

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public void High07_PassthroughConnection_DefaultsToExcludeFromIdleTimeoutFalse()
    {
        IPEndPoint ep = new(IPAddress.Parse("198.51.100.10"), 9000);
        using PassthroughConnection conn = new(s_testOpCodeExtractor, ep);

        conn.ExcludeFromIdleTimeout.Should().BeFalse("UDP passthrough connections must participate in idle timeout eviction by default");
    }

    [Fact]
    public void High08_TryAccept_WhenMapAtCapacity_DoesNotEvictBannedEndpoints()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 5,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        List<IPAddress> bannedIps = [];
        for (int i = 1; i <= 5; i++)
        {
            IPAddress ip = new([198, 51, (byte)i, 1]);
            bannedIps.Add(ip);
            guard.BanEndpoint(ip, TimeSpan.FromHours(24));
        }

        // Trigger eviction attempts with new IPs when all 5 tracked entries are banned
        for (int i = 10; i < 20; i++)
        {
            IPEndPoint newEp = new(new IPAddress([203, 0, (byte)i, 1]), 1234);
            _ = guard.TryAccept(newEp);
        }

        // None of the 5 banned IPs may be evicted or allowed to connect
        foreach (IPAddress bannedIp in bannedIps)
        {
            guard.TryAccept(new IPEndPoint(bannedIp, 1234)).Should().BeFalse($"banned IP {bannedIp} must never be evicted by TRY_RANDOM_SAMPLE_EVICT");
        }
    }

    [Fact]
    public void High08_SubnetMaps_BoundedAtMaxTrackedEndpoints_AndClearedOnDispose()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 5,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        ConnectionGuard guard = CreateIsolatedGuard(options);

        // Connect and release 5 distinct IPv4 /24 subnets so all 5 have CurrentConnections = 0
        for (int i = 1; i <= 5; i++)
        {
            IPEndPoint ep = new(new IPAddress([198, (byte)i, 1, 1]), 1234);
            guard.TryAccept(ep).Should().BeTrue();
            guard.Release(ep);
        }

        FieldInfo subnetV4Field = typeof(ConnectionGuard).GetField("_subnetMapV4", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo subnetV6Field = typeof(ConnectionGuard).GetField("_subnetMapV6", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var subnetMapV4 = (System.Collections.ICollection)subnetV4Field.GetValue(guard)!;
        var subnetMapV6 = (System.Collections.ICollection)subnetV6Field.GetValue(guard)!;

        subnetMapV4.Count.Should().Be(5);

        // Adding a 6th distinct /24 subnet when the previous 5 are idle should evict one idle subnet entry
        IPEndPoint sixthEp = new(new IPAddress([198, 99, 1, 1]), 1234);
        guard.TryAccept(sixthEp).Should().BeTrue();
        subnetMapV4.Count.Should().BeLessThanOrEqualTo(5, "_subnetMapV4 must evict idle subnet entries when reaching MaxTrackedEndpoints");

        // Also add an IPv6 entry
        IPEndPoint v6Ep = new(IPAddress.Parse("2606:4700:4700::1111"), 1234);
        guard.TryAccept(v6Ep).Should().BeTrue();
        subnetMapV6.Count.Should().BeGreaterThan(0);

        guard.Dispose();

        subnetMapV4.Count.Should().Be(0, "Dispose() must clear _subnetMapV4");
        subnetMapV6.Count.Should().Be(0, "Dispose() must clear _subnetMapV6");
    }

    // =========================================================================
    // 1. [HIGH-06] SocketEndpoint.FromRawBytes IPv4-mapped IPv6 normalization
    // =========================================================================

    [Fact]
    public void High06_FromRawBytes_WithIPv4MappedIPv6_NormalizesToIPv4AndReleasesSlots()
    {
        // ::ffff:203.0.113.42 in 16 raw bytes (as sent by PROXY v1 TCP6 / v2 AF_INET6)
        byte[] ipv4MappedBytes =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0xFF, 0xFF, 203, 0, 113, 42
        ];

        SocketEndpoint fromRaw = SocketEndpoint.FromRawBytes(ipv4MappedBytes, 54321, isIPv6: true);
        IPAddress realIp = new(ipv4MappedBytes);
        SocketEndpoint fromIp = SocketEndpoint.FromIpAddress(realIp);

        fromRaw.IsIPv6.Should().BeFalse("IPv4-mapped IPv6 raw bytes must be normalized to IPv4 just like NormalizeAddress");
        fromRaw.Should().Be(fromIp);
        fromRaw.GetHashCode().Should().Be(fromIp.GetHashCode());

        ConnectionQuotaOptions options = new()
        {
            MaxConnectionsPerIpAddress = 1,
            MaxConnectionsPerSubnet = 1,
            MaxConnectionsPerWindow = 100,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        // Proxy accept path uses TryAccept(SocketEndpoint) from FromRawBytes
        guard.TryAccept(fromRaw).Should().BeTrue();
        guard.TryAccept(fromRaw).Should().BeFalse("per-IP and per-subnet limit is 1");

        // Connection close path uses Connection.NetworkEndpoint (created via FromIpAddress / NormalizeAddress)
        IConnectionEventArgs args = Substitute.For<IConnectionEventArgs>();
        args.Connection.NetworkEndpoint.Returns(fromIp);
        guard.OnConnectionClosed(null, args);

        // Both IP slot and subnet slot must be released
        guard.TryAccept(fromRaw).Should().BeTrue("closing the normalized connection must release the slot acquired by FromRawBytes");
    }

    // =========================================================================
    // 2. [HIGH-07] PassthroughConnection ExcludeFromIdleTimeout & UdpPassthroughListener & TimingWheel
    // =========================================================================

    [Fact]
    public async Task High07_TimingWheel_WhenConnectionExcludedFromIdleTimeout_DecrementsRegisteredCount()
    {
        using TimingWheel wheel = new();
        IPEndPoint ep = new(IPAddress.Parse("198.51.100.11"), 9001);
        using PassthroughConnection conn = new(s_testOpCodeExtractor, ep)
        {
            ExcludeFromIdleTimeout = true,
            IdleTimeoutMs = 50
        };

        wheel.Activate();
        try
        {
            wheel.Register(conn);
            wheel.GetStatistics().RegisteredCount.Should().Be(1);

            // Wait for TimingWheel RUN_LOOP to process the bucket and evict the excluded connection
            long deadline = System.Environment.TickCount64 + 3000;
            while (conn.IsRegisteredInWheel && System.Environment.TickCount64 < deadline)
            {
                await Task.Delay(25);
            }

            conn.IsRegisteredInWheel.Should().BeFalse();
            wheel.GetStatistics().RegisteredCount.Should().Be(0, "TimingWheel must decrement _registeredCount when skipping an ExcludeFromIdleTimeout connection");
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    [Fact]
    public void High07_UdpPassthroughListener_MultipleDatagrams_DoesNotDuplicateConnectionClosedSubscription()
    {
        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.OpCodeExtractor.Returns(s_testOpCodeExtractor);
        IConnectionHub hub = Substitute.For<IConnectionHub>();
        using ConnectionGuard guard = new(new ConnectionQuotaOptions { ExemptLoopback = true });

        using UdpPassthroughListener listener = new(0, protocol, hub, guard);
        using Socket dummySocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dummySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        FieldInfo socketField = typeof(UdpListenerBase).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!;
        socketField.SetValue(listener, dummySocket);

        MethodInfo processDatagram = typeof(UdpPassthroughListener).GetMethod("ProcessDatagram", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo connectionsField = typeof(UdpPassthroughListener).GetField("_connections", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo connectionClosedField = typeof(PassthroughConnection).GetField("_connectionClosed", BindingFlags.Instance | BindingFlags.NonPublic)!;

        IPEndPoint clientEp = new(IPAddress.Loopback, 19000);

        for (int i = 0; i < 4; i++)
        {
            BufferLease lease = BufferLease.CopyFrom([1, 2, 3, 4, 5, 6]);
            processDatagram.Invoke(listener, [lease, clientEp]);

            var map = (ConcurrentDictionary<EndPoint, PassthroughConnection>)connectionsField.GetValue(listener)!;
            map.TryGetValue(clientEp, out PassthroughConnection? conn).Should().BeTrue();

            // Even if IsRegisteredInWheel becomes false on an existing connection, subsequent datagrams must not re-subscribe OnConnectionClosed
            conn!.IsRegisteredInWheel = false;
        }

        var connections = (ConcurrentDictionary<EndPoint, PassthroughConnection>)connectionsField.GetValue(listener)!;
        connections.TryGetValue(clientEp, out PassthroughConnection? trackedConn).Should().BeTrue();

        var multicast = (MulticastDelegate?)connectionClosedField.GetValue(trackedConn);
        multicast.Should().NotBeNull();

        // Count how many times UdpPassthroughListener.OnConnectionClosed is subscribed
        int listenerHandlerCount = 0;
        foreach (Delegate d in multicast!.GetInvocationList())
        {
            if (ReferenceEquals(d.Target, listener))
            {
                listenerHandlerCount++;
            }
        }

        listenerHandlerCount.Should().Be(1, "OnConnectionClosed must only be subscribed once when the connection is created");
    }

    // =========================================================================
    // 3. [HIGH-08] ConnectionGuard random sample eviction & subnet bounds
    // =========================================================================

    [Fact]
    public void High08_TryRandomSampleEvict_WhenFirstFiveEntriesAreActive_EvictsIdleEntryFurtherInMap()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 12,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        for (int i = 1; i <= 12; i++)
        {
            IPEndPoint ep = new(new IPAddress([198, 18, (byte)i, 1]), 1234);
            guard.TryAccept(ep).Should().BeTrue();
        }

        FieldInfo mapField = typeof(ConnectionGuard).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (ConcurrentDictionary<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry>)mapField.GetValue(guard)!;

        // Keep the first 6 entries in enumerator order active (CurrentConnections = 1),
        // and release the remaining 6 entries so CurrentConnections = 0.
        int index = 0;
        foreach (KeyValuePair<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry> kvp in map)
        {
            if (index >= 6)
            {
                IConnectionEventArgs args = Substitute.For<IConnectionEventArgs>();
                args.Connection.NetworkEndpoint.Returns(kvp.Key);
                guard.OnConnectionClosed(null, args);
            }
            index++;
        }

        FieldInfo cleanedField = typeof(ConnectionGuard).GetField("_totalCleanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long beforeCleaned = (long)cleanedField.GetValue(guard)!;

        // Trigger eviction by accepting a 13th unique IP
        IPEndPoint extraEp = new(new IPAddress([198, 19, 99, 1]), 1234);
        guard.TryAccept(extraEp).Should().BeTrue();

        long afterCleaned = (long)cleanedField.GetValue(guard)!;
        afterCleaned.Should().BeGreaterThan(beforeCleaned, "TRY_RANDOM_SAMPLE_EVICT must skip active entries and evict an idle entry beyond the first 5 elements");
    }

    // =========================================================================
    // 4. [MED-03] TimingWheel RUN_LOOP race overwriting task.Version on Unregister
    // =========================================================================

    private sealed class RaceTriggeredTimeoutConnection : TimingWheel.ITimeoutTrackedConnection
    {
        private readonly TimingWheel _wheel;
        private readonly TaskCompletionSource<bool> _unregisteredDuringLoop;
        private int _hookFired;

        public RaceTriggeredTimeoutConnection(TimingWheel wheel, TaskCompletionSource<bool> unregisteredDuringLoop)
        {
            _wheel = wheel;
            _unregisteredDuringLoop = unregisteredDuringLoop;
            ConnectionId = Snowflake.NewId(SnowflakeType.Session).ToUInt64();
            LastPingTime = Clock.UnixMillisecondsNow();
            NetworkEndpoint = SocketEndpoint.FromIpAddress(IPAddress.Loopback);
        }

        public int IdleTimeoutMs
        {
            get
            {
                // When RUN_LOOP reads IdleTimeoutMs (at line 715, after DequeueAll and after the initial version check),
                // simulate a concurrent thread calling Unregister(this) right before RUN_LOOP re-enqueues the task.
                if (System.Environment.CurrentManagedThreadId != 0 &&
                    ((TimingWheel.ITimeoutTrackedConnection)this).TimeoutTask is { Bucket: null } &&
                    Interlocked.CompareExchange(ref _hookFired, 1, 0) == 0)
                {
                    _wheel.Unregister(this);
                    _unregisteredDuringLoop.TrySetResult(true);
                    return 60_000; // Not expired yet, so RUN_LOOP enters the re-enqueue path
                }

                return 50;
            }
            set { }
        }

        public int TimeoutVersion { get; set; }
        public bool IsRegisteredInWheel { get; set; }
        public TimingWheel.TimeoutTask? TimeoutTask { get; set; }

        public bool IsDisposed => false;
        public ulong ConnectionId { get; }
        public string? UserId { get; set; }
        public INetworkEndpoint NetworkEndpoint { get; }
        public IOpCodeExtractor PacketClassifier => s_testOpCodeExtractor;
        public long UpTime => 0;
        public long LastPingTime { get; set; }
        public bool IsUdpCreated => false;
        public bool ExcludeFromIdleTimeout { get; set; }
        public IObjectMap<AttributeKey, object> Attributes => throw new NotSupportedException();
        public ConcurrentDictionary<ushort, object> RateLimitCache { get; } = new();
        public int ErrorCount => 0;
        public PermissionLevel Level { get; set; }
        public CipherSuiteType Algorithm { get; set; }
        public Bytes32 Secret { get; set; }
        public long BytesSent => 0;
        public long BytesReceived => 0;
        public long PacketsDropped => 0;
        public IConnection.ITransport TCP => throw new NotSupportedException();
        public IConnection.ITransport UDP => throw new NotSupportedException();

        public event EventHandler<IConnectionEventArgs>? ConnectionClosed { add { } remove { } }
        public event EventHandler<IConnectionEventArgs>? MessageProcessed { add { } remove { } }
        public event EventHandler<IConnectionEventArgs>? MessageProcessing { add { } remove { } }

        public void Disconnect(string? reason = null) { }
        public void Dispose() { }
        public void IncrementBytesReceived(int bytes) { }
        public void IncrementBytesSent(int bytes) { }
        public void IncrementErrorCount() { }
        public void IncrementPacketsDropped() { }
        public void UpdateIdleTimeout(int newTimeoutMs) { }
    }

    [Fact]
    public async Task Med03_TimingWheel_ConcurrentUnregisterDuringRunLoop_DoesNotReenqueueCancelledTask()
    {
        using TimingWheel wheel = new();
        TaskCompletionSource<bool> unregisteredTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RaceTriggeredTimeoutConnection conn = new(wheel, unregisteredTcs);

        wheel.Activate();
        try
        {
            wheel.Register(conn);
            TimingWheel.TimeoutTask? capturedTask = conn.TimeoutTask;
            capturedTask.Should().NotBeNull();

            await unregisteredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150); // Allow RUN_LOOP to finish processing the tick

            // After concurrent Unregister, RUN_LOOP must NOT have re-enqueued the task into a bucket
            capturedTask!.Bucket.Should().BeNull("a task whose connection was unregistered mid-tick must not be re-enqueued into a wheel bucket");
            capturedTask.Conn.Should().BeNull("the stale task must be returned to the pool (ResetForPool clears Conn)");
        }
        finally
        {
            wheel.Deactivate();
        }
    }

    // =========================================================================
    // 5. [MED-02a] SocketConnection fragmented BufferLease leak & HeaderSize check
    // =========================================================================

    private static (Socket listener, Socket client, Socket server) CreateConnectedSocketPair()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Task<Socket> acceptTask = Task.Run(() => listener.Accept());
        client.Connect(IPAddress.Loopback, port);
        Socket server = acceptTask.GetAwaiter().GetResult();
        return (listener, client, server);
    }

    private static byte[] WriteFragmentPayloadToBuffer(SocketConnection socketConn, ushort streamId, ushort chunkIndex, ushort totalChunks, bool isLast, ReadOnlySpan<byte> body)
    {
        FieldInfo bufferField = typeof(SocketConnection).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!;
        byte[] buffer = (byte[])bufferField.GetValue(socketConn)!;
        FragmentHeader header = new(streamId, chunkIndex, totalChunks, isLast);
        header.WriteTo(buffer);
        body.CopyTo(buffer.AsSpan(FragmentHeader.WireSize));
        return buffer;
    }

    [Fact]
    public void Med02a_HandleFragmentedFrameDirect_WhenRejectedByOwner_DisposesAssembledLeaseCompletely()
    {
        var (listener, client, server) = CreateConnectedSocketPair();
        try
        {
            Connection realConn = new(server, s_testOpCodeExtractor);
            FieldInfo backingField = typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object backing = backingField.GetValue(realConn)!;
            FieldInfo socketField = backing.GetType().GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            SocketConnection socketConn = (SocketConnection)socketField.GetValue(backing)!;

            // Force OnFrameReceived to return false by setting PendingProcessCallbacks above MaxPerConnectionPendingPackets
            // and attaching a non-inlineable handler.
            realConn.MessageProcessing += (_, args) => args.Lease?.Dispose();
            FieldInfo pendingField = backing.GetType().GetField("PendingProcessCallbacks", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            pendingField.SetValue(backing, 1_000_000);

            MethodInfo processFrameMethod = typeof(SocketConnection).GetMethod("PROCESS_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo assemblerField = typeof(SocketConnection).GetField("_fragmentAssembler", BindingFlags.Instance | BindingFlags.NonPublic)!;

            // Send chunk 0 of 2 via PROCESS_FRAME_FROM_BUFFER -> HANDLE_FRAGMENTED_FRAME_DIRECT
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 1, chunkIndex: 0, totalChunks: 2, isLast: false, [1, 2, 3, 4]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 4]);

            // Inspect the internal AccumLease before chunk 1 completes reassembly
            FragmentAssembler assembler = (FragmentAssembler)assemblerField.GetValue(socketConn)!;
            FieldInfo streamsField = typeof(FragmentAssembler).GetField("_streams", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var streams = (System.Collections.IDictionary)streamsField.GetValue(assembler)!;
            object streamState = streams[(ushort)1]!;
            FieldInfo accumLeaseField = streamState.GetType().GetField("AccumLease", BindingFlags.Instance | BindingFlags.NonPublic)!;
            BufferLease accumLease = (BufferLease)accumLeaseField.GetValue(streamState)!;

            // Send chunk 1 of 2 (total assembled = 8 bytes >= PacketConstants.HeaderSize)
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 1, chunkIndex: 1, totalChunks: 2, isLast: true, [5, 6, 7, 8]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 4]);

            // Because OnFrameReceived returned false, HANDLE_FRAGMENTED_FRAME_DIRECT must have disposed accumLease to RefCount 0
            FieldInfo refCountField = typeof(BufferLease).GetField("_refCount", BindingFlags.Instance | BindingFlags.NonPublic)!;
            int refCount = (int)refCountField.GetValue(accumLease)!;
            refCount.Should().Be(0, "when OnFrameReceived returns false, assembledLease must not have an extra Retain() leaving _refCount == 1");

            pendingField.SetValue(backing, 0);
            realConn.Dispose();
        }
        finally
        {
            client.Dispose();
            server.Dispose();
            listener.Dispose();
        }
    }

    [Fact]
    public async Task Med02a_FragmentedAndVarIntFrames_SmallerThanPacketHeaderSize_AreDropped()
    {
        var (listener, client, server) = CreateConnectedSocketPair();
        try
        {
            using Connection connection = new(server, s_testOpCodeExtractor);
            int dispatchedCount = 0;
            connection.MessageProcessing += (_, args) =>
            {
                Interlocked.Increment(ref dispatchedCount);
                args.Lease?.Dispose();
            };

            FieldInfo backingField = typeof(Connection).GetField("_backing", BindingFlags.Instance | BindingFlags.NonPublic)!;
            object backing = backingField.GetValue(connection)!;
            FieldInfo socketField = backing.GetType().GetField("Socket", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            SocketConnection socketConn = (SocketConnection)socketField.GetValue(backing)!;

            // 1. Test HANDLE_FRAGMENTED_FRAME_DIRECT with 3-byte reassembled payload (< PacketConstants.HeaderSize = 6)
            MethodInfo processFrameMethod = typeof(SocketConnection).GetMethod("PROCESS_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            _ = WriteFragmentPayloadToBuffer(socketConn, streamId: 7, chunkIndex: 0, totalChunks: 1, isLast: true, [0xAA, 0xBB, 0xCC]);
            processFrameMethod.Invoke(socketConn, [0, FragmentHeader.WireSize + 3]);

            // 2. Test PROCESS_VARINT_FRAME_FROM_BUFFER with 3-byte payload (< PacketConstants.HeaderSize = 6)
            MethodInfo processVarIntMethod = typeof(SocketConnection).GetMethod("PROCESS_VARINT_FRAME_FROM_BUFFER", BindingFlags.Instance | BindingFlags.NonPublic)!;
            processVarIntMethod.Invoke(socketConn, [0, 3]);

            await Task.Delay(100);

            Volatile.Read(ref dispatchedCount).Should().Be(0, $"reassembled fragmented and VarInt payloads smaller than PacketConstants.HeaderSize ({PacketConstants.HeaderSize}) must be dropped");
        }
        finally
        {
            client.Dispose();
            server.Dispose();
            listener.Dispose();
        }
    }
}
