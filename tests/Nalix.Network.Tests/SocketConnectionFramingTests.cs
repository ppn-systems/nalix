using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Moq;
using Xunit;
using Nalix.Abstractions.Networking;
using Nalix.Network.Internal.Transport;

namespace Nalix.Network.Tests;

public class SocketConnectionFramingTests
{
    private SocketConnection CreateConnection()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var ownerMock = new Mock<IConnection>();
        var endpointMock = new Mock<INetworkEndpoint>();
        ownerMock.Setup(m => m.NetworkEndpoint).Returns(endpointMock.Object);

        var connection = new SocketConnection();
        connection.Initialize(socket, ownerMock.Object);
        return connection;
    }

    [Fact]
    public void SetFraming_WhenNotStarted_Succeeds()
    {
        using var conn = CreateConnection();

        // Arrange & Act - Should not throw
        conn.SetFraming(TransportFraming.VarIntLengthPrefixed);
    }

    [Fact]
    public void SetFraming_WhenAlreadyLocked_ThrowsInvalidOperationException()
    {
        using var conn = CreateConnection();

        conn.SetFraming(TransportFraming.VarIntLengthPrefixed);

        // Calling it again should throw
        var ex = Assert.Throws<InvalidOperationException>(() => conn.SetFraming(TransportFraming.UInt16LengthPrefixed));
        Assert.Contains("Framing can only be configured once before receive", ex.Message);
    }

    [Fact]
    public void SetFraming_AfterBeginReceive_ThrowsInvalidOperationException()
    {
        using var conn = CreateConnection();

        // Start receive loop
        conn.BeginReceive(CancellationToken.None);

        // Setting framing after started should throw
        var ex = Assert.Throws<InvalidOperationException>(() => conn.SetFraming(TransportFraming.VarIntLengthPrefixed));
        Assert.Contains("Framing can only be configured once before receive", ex.Message);
    }
}
