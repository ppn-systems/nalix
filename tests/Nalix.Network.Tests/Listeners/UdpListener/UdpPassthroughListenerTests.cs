// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
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
using Nalix.Network.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.UdpListener;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class UdpPassthroughListenerTests
{
    private static readonly IOpCodeExtractor s_testOpCodeExtractor = new TestOpCodeExtractor();

    private sealed class TestOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) =>
            payload.Length >= 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[0..]) : (ushort)0;
    }

    [Fact]
    public void ProcessDatagram_WithMultipleDatagramsFromSameSource_SubscribesConnectionClosedOnce()
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

    [Fact]
    public void ProcessDatagram_FromManyDistinctSources_DoesNotConsumeSharedGlobalConnectionBudget()
    {
        using ConfigurationOptionsScope<ConnectionGuardOptions> guardScope = new();
        guardScope.Options.MaxConnections = 3;

        IProtocol protocol = Substitute.For<IProtocol>();
        protocol.OpCodeExtractor.Returns(s_testOpCodeExtractor);
        IConnectionHub hub = Substitute.For<IConnectionHub>();
        using ConnectionGuard guard = new(new ConnectionQuotaOptions { ExemptLoopback = false });

        using UdpPassthroughListener listener = new(0, protocol, hub, guard);
        using Socket dummySocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        dummySocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        typeof(UdpListenerBase).GetField("_socket", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(listener, dummySocket);
        MethodInfo processDatagram = typeof(UdpPassthroughListener).GetMethod("ProcessDatagram", BindingFlags.Instance | BindingFlags.NonPublic)!;

        // Spoofed sources: one datagram each from many distinct addresses.
        for (int i = 0; i < 10; i++)
        {
            BufferLease lease = BufferLease.CopyFrom([1, 2, 3, 4, 5, 6]);
            processDatagram.Invoke(listener, [lease, new IPEndPoint(new IPAddress([(byte)(20 + i), 1, 1, 1]), 19000)]);
        }

        // A real TCP client must still be admitted by the same guard.
        guard.TryAccept(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 40000))
            .Should().BeTrue("passthrough pseudo-connections must not exhaust the TCP global connection budget");
    }
}
