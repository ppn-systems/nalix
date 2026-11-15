// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Regression: a caller that read <c>Connection.UDP</c> before the connection closed kept a reference to a pooled
/// <see cref="SocketUdpTransport"/>. After the owner closed, the instance could be re-initialized for another
/// session, and the stale reference then sent its payload to the new owner's endpoint.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class UdpTransportStaleHolderTests : IDisposable
{
    private static readonly byte[] Payload = [0xAA, 0xBB, 0xCC, 0xDD];

    private readonly Socket _sender;
    private readonly Socket _staleReceiver;
    private readonly Socket _newOwnerReceiver;
    private readonly System.Collections.Generic.List<Connection> _connections = [];
    private readonly System.Collections.Generic.List<Socket> _tcpSockets = [];

    public UdpTransportStaleHolderTests()
    {
        _sender = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        _staleReceiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _staleReceiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        _newOwnerReceiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _newOwnerReceiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        foreach (Connection c in _connections)
        {
            c.Dispose();
        }
        foreach (Socket s in _tcpSockets)
        {
            s.Dispose();
        }
        _sender.Dispose();
        _staleReceiver.Dispose();
        _newOwnerReceiver.Dispose();
    }

    [Fact]
    public void Send_OnStaleUdpHolderAfterOwnerClosed_DoesNotReachNewOwnerEndpoint()
    {
        IConnection.ITransport stale = BindConnection(_staleReceiver);
        _connections[0].Dispose();

        // A new session binds the same pooled instance to its own endpoint.
        _ = BindConnection(_newOwnerReceiver);

        stale.Send(Payload);

        Assert.False(
            _newOwnerReceiver.Poll(300_000, SelectMode.SelectRead),
            "A payload sent through a stale UDP handle must not be delivered to the endpoint of the connection that now owns the recycled transport.");
    }

    [Fact]
    public async Task SendAsync_OnStaleUdpHolderAfterOwnerClosed_DoesNotReachNewOwnerEndpoint()
    {
        IConnection.ITransport stale = BindConnection(_staleReceiver);
        _connections[0].Dispose();

        _ = BindConnection(_newOwnerReceiver);

        await stale.SendAsync(Payload);

        Assert.False(
            _newOwnerReceiver.Poll(300_000, SelectMode.SelectRead),
            "A payload sent through a stale UDP handle must not be delivered to the endpoint of the connection that now owns the recycled transport.");
    }

    [Fact]
    public void Send_OnStaleUdpHolderAfterOwnerClosed_DoesNotReachOldEndpoint()
    {
        IConnection.ITransport stale = BindConnection(_staleReceiver);
        _connections[0].Dispose();

        stale.Send(Payload);

        Assert.False(
            _staleReceiver.Poll(300_000, SelectMode.SelectRead),
            "A closed connection's UDP transport must not send anything.");
    }

    /// <summary>
    /// Creates a connection bound to <paramref name="remote"/> over the shared sender socket and returns the
    /// UDP transport as a caller would hold it.
    /// </summary>
    private IConnection.ITransport BindConnection(Socket remote)
    {
        Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _tcpSockets.Add(tcp);

        Connection connection = new(tcp, new DummyOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));
        _connections.Add(connection);

        SocketUdpTransport.EnsureUDP(connection, (IPEndPoint)remote.LocalEndPoint!, _sender);
        return connection.UDP!;
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
