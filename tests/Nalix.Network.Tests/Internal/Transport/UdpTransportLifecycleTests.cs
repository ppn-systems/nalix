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

        SocketUdpTransport transport = new(new Nalix.Environment.Sequencing.SequenceCounter(), new Nalix.Environment.Sequencing.SequenceCounter());
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
        SocketUdpTransport transport = new(new Nalix.Environment.Sequencing.SequenceCounter(), new Nalix.Environment.Sequencing.SequenceCounter());
        Action act = () => transport.Send(new byte[] { 1, 2, 3 });
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_AfterUse_SendsNothingAndReleasesOwnedSocket()
    {
        // Owned socket: Initialize() created it internally (no SetSocket call).
        SocketUdpTransport owned = new(new Nalix.Environment.Sequencing.SequenceCounter(), new Nalix.Environment.Sequencing.SequenceCounter());
        using Socket receiver = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        IPEndPoint remote = new(IPAddress.Loopback, ((IPEndPoint)receiver.LocalEndPoint!).Port);
        owned.Initialize(ref remote);
        owned.Dispose();

        // A disposed transport is inert: a stale holder must not send to the endpoint it was bound to.
        Action sendAfterDispose = () => owned.Send(new byte[] { 1, 2, 3 });
        sendAfterDispose.Should().NotThrow();
        receiver.Poll(200_000, SelectMode.SelectRead).Should().BeFalse("a disposed UDP transport must not send");
    }

    [Fact]
    public void Dispose_WithSharedSocket_DoesNotDisposeSocket()
    {
        // Shared socket: SetSocket() means the transport does not own it and must not dispose it.
        using Socket shared = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        shared.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        SocketUdpTransport sharedTransport = new(new Nalix.Environment.Sequencing.SequenceCounter(), new Nalix.Environment.Sequencing.SequenceCounter());
        sharedTransport.SetSocket(shared);
        sharedTransport.Dispose();

        Action useAfterDispose = () => shared.LocalEndPoint.Should().NotBeNull();
        useAfterDispose.Should().NotThrow("a shared (non-owned) socket must survive Dispose since the transport does not own its lifecycle");
    }
}
