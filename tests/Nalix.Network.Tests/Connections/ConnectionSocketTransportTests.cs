// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// <see cref="Connection.Transport"/> must expose the raw socket through <see cref="IConnection.ISocketTransport"/>. The tunneling
/// extension type-tests for that interface to unwrap connections, so losing it breaks tunnels without any compile error.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class ConnectionSocketTransportTests : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public void Dispose()
    {
        foreach (IDisposable resource in _resources)
        {
            try { resource.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public void TCP_OfConnection_IsSocketTransport()
    {
        (Socket server, _) = CreatePair();
        Connection connection = new(server, new DummyOpCodeExtractor());
        _resources.Add(connection);

        connection.Transport.Should().BeAssignableTo<IConnection.ISocketTransport>(
            "tunneling unwraps a connection through IConnection.ISocketTransport; a connection without it cannot carry a tunnel");
    }

    [Fact]
    public void Socket_OfSocketTransport_IsTheConnectionSocket()
    {
        (Socket server, _) = CreatePair();
        Connection connection = new(server, new DummyOpCodeExtractor());
        _resources.Add(connection);

        IConnection.ISocketTransport transport = (IConnection.ISocketTransport)connection.Transport;

        transport.Socket.Should().BeSameAs(server);
    }

    private (Socket Server, Socket Client) CreatePair()
    {
        Socket listener = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _resources.Add(listener);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);

        Socket client = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _resources.Add(client);
        client.Connect((IPEndPoint)listener.LocalEndPoint!);

        Socket server = listener.Accept();
        _resources.Add(server);
        return (server, client);
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
