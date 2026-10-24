// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Security;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

public sealed class TransportSequenceBoxingTests : IDisposable
{
    private readonly Socket _socket;
    private readonly IOpCodeExtractor _extractor;
    private readonly Connection _connection;

    public TransportSequenceBoxingTests()
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _extractor = Substitute.For<IOpCodeExtractor>();
        _connection = new Connection(_socket, _extractor, new IPEndPoint(IPAddress.Loopback, 0));
    }

    [Fact]
    public void SequenceCounter_OnTcpTransport_ReturnsSameReferenceAndPersistsState()
    {
        // Arrange
        IConnection.ITransport transport = _connection.TCP;

        // Act & Assert
        // 1. Same reference check (verifies boxing is eliminated)
        ISequenceCounter rx1 = transport.ReceiveSequence;
        ISequenceCounter rx2 = transport.ReceiveSequence;
        rx1.Should().BeSameAs(rx2);

        ISequenceCounter tx1 = transport.SendSequence;
        ISequenceCounter tx2 = transport.SendSequence;
        tx1.Should().BeSameAs(tx2);

        // 2. State persistence checks
        rx1.IsValid(1).Should().BeTrue();
        rx1.UpdateTo(1);
        rx1.Current().Should().Be(1);

        // Next read of property should see the updated state (1)
        transport.ReceiveSequence.Current().Should().Be(1);
        transport.ReceiveSequence.IsValid(1).Should().BeFalse(); // Replay check

        // Send sequence increment/monotony
        tx1.Current().Should().Be(0);
        tx1.Next().Should().Be(1);
        tx1.Current().Should().Be(1);

        transport.SendSequence.Current().Should().Be(1);
        transport.SendSequence.Next().Should().Be(2);
    }

    [Fact]
    public void SequenceCounter_OnUdpTransport_ReturnsSameReferenceAndPersistsState()
    {
        // Arrange
        using var transport = new SocketUdpTransport();

        // Act & Assert
        // 1. Same reference check (verifies boxing is eliminated)
        ISequenceCounter rx1 = transport.ReceiveSequence;
        ISequenceCounter rx2 = transport.ReceiveSequence;
        rx1.Should().BeSameAs(rx2);

        ISequenceCounter tx1 = transport.SendSequence;
        ISequenceCounter tx2 = transport.SendSequence;
        tx1.Should().BeSameAs(tx2);

        // 2. State persistence checks
        rx1.IsValid(1).Should().BeTrue();
        rx1.UpdateTo(1);
        rx1.Current().Should().Be(1);

        // Next read of property should see the updated state (1)
        transport.ReceiveSequence.Current().Should().Be(1);
        transport.ReceiveSequence.IsValid(1).Should().BeFalse(); // Replay check

        // Send sequence increment/monotony
        tx1.Current().Should().Be(0);
        tx1.Next().Should().Be(1);
        tx1.Current().Should().Be(1);

        transport.SendSequence.Current().Should().Be(1);
        transport.SendSequence.Next().Should().Be(2);
    }

    [Fact]
    public void SequenceCounter_OnUdpTransportAfterPoolReset_StartsFromZero()
    {
        // Arrange
        using var transport = new SocketUdpTransport();

        // Modify state
        transport.ReceiveSequence.UpdateTo(42);
        transport.SendSequence.Next();
        transport.SendSequence.Next();

        transport.ReceiveSequence.Current().Should().Be(42);
        transport.SendSequence.Current().Should().Be(2);

        // Act - reset for pooling
        transport.ResetForPool();

        // Assert - state is reset to 0
        transport.ReceiveSequence.Current().Should().Be(0);
        transport.SendSequence.Current().Should().Be(0);

        // References should still be the same (not re-allocated)
        ISequenceCounter rx = transport.ReceiveSequence;
        ISequenceCounter tx = transport.SendSequence;
        rx.Current().Should().Be(0);
        tx.Current().Should().Be(0);
    }

    public void Dispose()
    {
        _connection.Dispose();
        _socket.Dispose();
    }
}

