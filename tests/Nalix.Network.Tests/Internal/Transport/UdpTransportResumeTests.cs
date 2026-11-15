// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class UdpTransportResumeTests : IDisposable
{
    private readonly Socket _tcpSocket;
    private readonly Socket _udpSocket;
    private readonly Connection _connection;

    public UdpTransportResumeTests()
    {
        _tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _connection = new Connection(_tcpSocket, new DummyOpCodeExtractor(), new IPEndPoint(IPAddress.Loopback, 0));

        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _udpSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        _connection.Dispose();
        _tcpSocket.Dispose();
        _udpSocket.Dispose();
    }

    [Fact]
    public void EnsureUDP_WhenStaleResumeCountersExist_StartsReceiveCounterAtZero()
    {
        // A resumed connection can carry the old UDP counters in its sequence state. They must not
        // move the receive window of the transport bound later (the client's UDP counters start at 0).
        _connection.Attributes[ConnectionAttributes.SequenceState] = new ConnectionSequenceState
        {
            UdpSendSequence = 5000,
            UdpReceiveSequence = 5000,
        };

        IPEndPoint remote = new(IPAddress.Loopback, 40000);
        SocketUdpTransport.EnsureUDP(_connection, remote, _udpSocket);

        Assert.NotNull(_connection.UDP);
        Assert.True(_connection.UDP!.ReceiveSequence.IsValid(1, window: 32), "The first UDP frame of a resumed client must be accepted.");
        Assert.Equal(0u, _connection.UDP.SendSequence.Current());
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
