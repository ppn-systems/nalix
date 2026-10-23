// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Exceptions;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

public sealed class UdpTransportLifecycleTests
{
    private static (SocketUdpTransport transport, Socket socket) CreateInitialized()
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        SocketUdpTransport transport = new();
        transport.SetSocket(socket);
        IPEndPoint remote = new(IPAddress.Loopback, ((IPEndPoint)socket.LocalEndPoint!).Port);
        transport.Initialize(ref remote);

        return (transport, socket);
    }

    [Fact]
    public void Send_WithPayloadOverMaxDatagramSize_ThrowsNetworkException()
    {
        var (transport, socket) = CreateInitialized();
        try
        {
            byte[] oversized = new byte[2000]; // default MaxUdpDatagramSize is 1440
            Action act = () => transport.Send(oversized);
            act.Should().Throw<NetworkException>();
        }
        finally
        {
            transport.Dispose();
            socket.Dispose();
        }
    }

    [Fact]
    public async Task SendAsync_WithPayloadOverMaxDatagramSize_ThrowsNetworkException()
    {
        var (transport, socket) = CreateInitialized();
        try
        {
            byte[] oversized = new byte[2000];
            Func<Task> act = async () => await transport.SendAsync(oversized);
            await act.Should().ThrowAsync<NetworkException>();
        }
        finally
        {
            transport.Dispose();
            socket.Dispose();
        }
    }

    [Fact]
    public void Send_WithoutInitialize_DoesNotThrow()
    {
        SocketUdpTransport transport = new();
        Action act = () => transport.Send(new byte[] { 1, 2, 3 });
        act.Should().NotThrow();
    }

    [Fact]
    public void ResetForPool_AfterUse_ZeroesSequenceCountersAndBytes()
    {
        var (transport, socket) = CreateInitialized();
        try
        {
            transport.SendSequence.Next();
            transport.SendSequence.Next();
            transport.ReceiveSequence.Next();
            transport.RecordBytesReceived(128);

            transport.ResetForPool();

            transport.CurrentSendSequence.Should().Be(0, "ResetForPool must zero the send sequence so a pooled instance can't leak state to its next owner");
            transport.CurrentReceiveSequence.Should().Be(0, "ResetForPool must zero the receive sequence so a pooled instance can't leak state to its next owner");
            transport.BytesReceived.Should().Be(0);
            transport.BytesSent.Should().Be(0);
        }
        finally
        {
            socket.Dispose();
        }
    }

    [Fact]
    public void ResetForPool_WithOwnedAndSharedSocket_ReleasesOnlyOwnedSocket()
    {
        // Owned socket: Initialize() created it internally (no SetSocket call).
        SocketUdpTransport owned = new();
        IPEndPoint ep = new(IPAddress.Loopback, 0);
        owned.Initialize(ref ep);
        owned.ResetForPool();
        // Re-initializing after reset must succeed (proves the owned socket was disposed and cleared, not left dangling).
        IPEndPoint ep2 = new(IPAddress.Loopback, 0);
        Action act = () => owned.Initialize(ref ep2);
        act.Should().NotThrow();
        owned.Dispose();

        // Shared socket: SetSocket() means the transport does not own it and must not dispose it.
        using Socket shared = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        shared.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        SocketUdpTransport sharedTransport = new();
        sharedTransport.SetSocket(shared);
        sharedTransport.ResetForPool();

        Action useAfterReset = () => shared.LocalEndPoint.Should().NotBeNull();
        useAfterReset.Should().NotThrow("a shared (non-owned) socket must survive ResetForPool since the transport does not own its lifecycle");
    }
}
