// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Traversal.Reflector;
using NSubstitute;

namespace Nalix.Traversal.Tests.Reflector;

/// <summary>
/// The Reflector relays raw datagrams on its own socket: the first two distinct senders of a token become
/// the session peers and their datagrams are forwarded unchanged to each other.
/// </summary>
public sealed class ReflectorRelayTests
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Relay_WhenBothPeersSend_ForwardsDatagramsBetweenThem()
    {
        ReflectorManager manager = new();
        ulong token = manager.CreateSession(1UL, 2UL, Substitute.For<IConnection>());

        using ReflectorRelay relay = new(manager, port: 0);
        relay.Activate();
        IPEndPoint relayEndPoint = new(IPAddress.Loopback, ((IPEndPoint)relay.LocalEndPoint!).Port);

        using UdpClient peerA = new(new IPEndPoint(IPAddress.Loopback, 0));
        using UdpClient peerB = new(new IPEndPoint(IPAddress.Loopback, 0));

        // Peer A registers first; nothing to forward to yet.
        _ = await peerA.SendAsync(Datagram(token, 0xA0), relayEndPoint);
        await WaitUntilAsync(() => manager.TryGetSession(token, out ReflectorSession? s) && s.PeerAEndPoint is not null);

        byte[] fromB = Datagram(token, 0xB1);
        _ = await peerB.SendAsync(fromB, relayEndPoint);
        UdpReceiveResult atA = await peerA.ReceiveAsync().WaitAsync(s_timeout);
        Assert.Equal(fromB, atA.Buffer);

        byte[] fromA = Datagram(token, 0xA2);
        _ = await peerA.SendAsync(fromA, relayEndPoint);
        UdpReceiveResult atB = await peerB.ReceiveAsync().WaitAsync(s_timeout);
        Assert.Equal(fromA, atB.Buffer);

        Assert.True(manager.TryGetSession(token, out ReflectorSession? session));
        Assert.Equal(((IPEndPoint)peerA.Client.LocalEndPoint!).Port, session.PeerAEndPoint!.Port);
        Assert.Equal(((IPEndPoint)peerB.Client.LocalEndPoint!).Port, session.PeerBEndPoint!.Port);
    }

    [Fact]
    public void TryRelay_WhenThirdAddressUsesToken_Drops()
    {
        ReflectorManager manager = new();
        ulong token = manager.CreateSession(1UL, 2UL, Substitute.For<IConnection>());
        using ReflectorRelay relay = new(manager, port: 0);

        SocketAddress a = new IPEndPoint(IPAddress.Loopback, 1001).Serialize();
        SocketAddress b = new IPEndPoint(IPAddress.Loopback, 1002).Serialize();
        SocketAddress c = new IPEndPoint(IPAddress.Loopback, 1003).Serialize();
        byte[] datagram = Datagram(token, 1);

        Assert.False(relay.TryRelay(datagram, a, out _));
        Assert.True(relay.TryRelay(datagram, b, out SocketAddress? toA));
        Assert.Equal(a, toA);
        Assert.False(relay.TryRelay(datagram, c, out _));
    }

    [Fact]
    public void TryRelay_WhenTokenIsUnknownOrDatagramTooShort_Drops()
    {
        ReflectorManager manager = new();
        using ReflectorRelay relay = new(manager, port: 0);
        SocketAddress sender = new IPEndPoint(IPAddress.Loopback, 1001).Serialize();

        Assert.False(relay.TryRelay(Datagram(0xDEADUL, 1), sender, out _));
        Assert.False(relay.TryRelay(new byte[7], sender, out _));
    }

    private static byte[] Datagram(ulong token, byte marker)
    {
        byte[] datagram = new byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(datagram, token);
        datagram[8] = marker;
        return datagram;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource cts = new(s_timeout);
        while (!condition())
        {
            await Task.Delay(10, cts.Token);
        }
    }
}
