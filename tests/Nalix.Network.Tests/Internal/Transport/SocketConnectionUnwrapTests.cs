// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Connections;

namespace Nalix.Network.Tests.Internal.Transport;

public sealed class SocketConnectionUnwrapTests
{
    private SocketConnection CreateConnection(out Socket originalSocket)
    {
        originalSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var ownerMock = new Mock<IConnection>();
        var endpointMock = new Mock<INetworkEndpoint>();
        ownerMock.Setup(m => m.NetworkEndpoint).Returns(endpointMock.Object);

        var connection = new SocketConnection();
        connection.Initialize(originalSocket, ownerMock.Object);
        return connection;
    }

    [Fact]
    public void Unwrap_WhenConnectionDisposed_KeepsSocketOpen()
    {
        // Arrange
        var conn = CreateConnection(out var originalSocket);

        // Act
        Socket unwrappedSocket = conn.Unwrap();

        // Assert
        Assert.Same(originalSocket, unwrappedSocket);

        // Act - Dispose the connection
        conn.Dispose();

        // Assert - The socket should still be usable (not disposed/closed)
        // A simple way to check if a socket is disposed is to access its Handle property
        // or check if it throws ObjectDisposedException
        var handle = unwrappedSocket.Handle;
        Assert.NotEqual(IntPtr.Zero, handle);

        // Clean up our detached socket
        unwrappedSocket.Dispose();
    }
}

