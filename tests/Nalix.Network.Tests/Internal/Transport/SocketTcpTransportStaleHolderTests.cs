// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Regression: <see cref="Connection.TCP"/> returns a pooled <c>SocketTcpTransport</c>. A caller that kept that
/// reference after the connection closed could send through it once the instance was re-rented to another
/// connection, injecting bytes into that connection's stream.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class SocketTcpTransportStaleHolderTests : IDisposable
{
    private static readonly byte[] Payload = [0xAA, 0xBB, 0xCC, 0xDD];

    private readonly System.Collections.Generic.List<Socket> _sockets = [];
    private readonly System.Collections.Generic.List<Connection> _connections = [];

    public void Dispose()
    {
        foreach (Connection c in _connections)
        {
            c.Dispose();
        }

        foreach (Socket s in _sockets)
        {
            s.Dispose();
        }
    }

    [Fact]
    public void Send_OnStaleTcpHolderAfterOwnerClosed_DoesNotReachNewOwnerPeer()
    {
        IConnection.ITransport stale = OpenConnection(out _);
        _connections[0].Dispose();

        // A new session takes over the pooled instance.
        _ = OpenConnection(out Socket newOwnerPeer);

        Exception? error = Record.Exception(() => stale.Send(Payload));

        AssertNoFailureOtherThanDisposed(error);
        newOwnerPeer.Poll(300_000, SelectMode.SelectRead).Should().BeFalse(
            "bytes sent through a stale TCP handle must not reach the stream of the connection that now owns the recycled transport");
    }

    [Fact]
    public async Task SendAsync_OnStaleTcpHolderAfterOwnerClosed_DoesNotReachNewOwnerPeer()
    {
        IConnection.ITransport stale = OpenConnection(out _);
        _connections[0].Dispose();

        _ = OpenConnection(out Socket newOwnerPeer);

        Exception? error = await Record.ExceptionAsync(() => stale.SendAsync(Payload).AsTask());

        AssertNoFailureOtherThanDisposed(error);
        newOwnerPeer.Poll(300_000, SelectMode.SelectRead).Should().BeFalse(
            "bytes sent through a stale TCP handle must not reach the stream of the connection that now owns the recycled transport");
    }

    [Fact]
    public void NextSendSequence_OnStaleTcpHolderAfterOwnerClosed_DoesNotAdvanceNewOwnerSendSequence()
    {
        IConnection.ITransport stale = OpenConnection(out _);
        _connections[0].Dispose();

        IConnection.ITransport newOwner = OpenConnection(out _);
        uint before = newOwner.CurrentSendSequence;

        _ = stale.NextSendSequence();

        newOwner.CurrentSendSequence.Should().Be(before,
            "a stale TCP handle must not advance the send sequence of the connection that now owns the recycled transport");
    }

    private static void AssertNoFailureOtherThanDisposed(Exception? error)
    {
        if (error is not null)
        {
            error.Should().BeOfType<ObjectDisposedException>("a closed connection's TCP transport may refuse to send, but must not send elsewhere");
        }
    }

    /// <summary>
    /// Creates a connection over a fresh TCP pair and returns its TCP transport as a caller would hold it.
    /// The connection's peer is returned through <paramref name="peer"/>.
    /// </summary>
    private IConnection.ITransport OpenConnection(out Socket peer)
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _sockets.Add(listener);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _sockets.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndPoint!);

        Socket server = listener.Accept();
        _sockets.Add(server);

        peer = client;
        Connection connection = new(server, new DummyOpCodeExtractor());
        _connections.Add(connection);
        return connection.TCP;
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
