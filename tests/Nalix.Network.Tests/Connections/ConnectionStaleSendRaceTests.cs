// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// Regression: a send that had already captured its socket of connection A could be paused while A was disposed and its
/// pooled socket instance was rented to connection B. The paused send then wrote into B's stream. A send must be admitted
/// only for the session it captured, and a closed session's sends must not reach a later session.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class ConnectionStaleSendRaceTests : IDisposable
{
    private static readonly byte[] Payload = [0xAA, 0xBB, 0xCC, 0xDD];

    private readonly List<Connection> _connections = [];
    private readonly List<Socket> _sockets = [];

    public void Dispose()
    {
        Connection.s_afterSendCaptureHook = null;

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
    public async Task Send_WhenPausedAfterCaptureAndSessionReRented_DoesNotWriteToNewOwnerPeer()
        => await AssertPausedSendIsNotDelivered(static transport => Task.Run(() => transport.Send(Payload)));

    [Fact]
    public async Task SendAsync_WhenPausedAfterCaptureAndSessionReRented_DoesNotWriteToNewOwnerPeer()
        => await AssertPausedSendIsNotDelivered(static transport => Task.Run(() => transport.SendAsync(Payload).AsTask()));

    /// <summary>
    /// Pauses the send of connection A right after it captured its socket, disposes A, rents the same socket instance to
    /// connection B on this thread, then lets the send continue. The test first asserts the instance was really re-rented,
    /// so it cannot pass without exercising the race.
    /// </summary>
    private async Task AssertPausedSendIsNotDelivered(Func<IConnection.ITransport, Task> send)
    {
        (Socket serverA, _) = CreatePair();
        Connection connectionA = CreateConnection(serverA);
        IConnection.ITransport staleTransport = connectionA.TCP;

        using ManualResetEventSlim paused = new(false);
        using ManualResetEventSlim resume = new(false);
        SocketConnection? captured = null;
        int capturedGeneration = 0;

        Connection.s_afterSendCaptureHook = socket =>
        {
            captured = socket;
            capturedGeneration = socket.Generation;
            paused.Set();
            _ = resume.Wait(TimeSpan.FromSeconds(10));
        };

        Task pending = send(staleTransport);
        paused.Wait(TimeSpan.FromSeconds(10)).Should().BeTrue("the send must reach the capture point");

        connectionA.Dispose();

        (Socket serverB, Socket peerB) = CreatePair();
        CreateConnection(serverB);

        captured.Should().NotBeNull();
        captured!.Generation.Should().NotBe(capturedGeneration,
            "the paused send's socket instance must have been re-rented to connection B, otherwise the test does not exercise the race");

        resume.Set();

        // A send that re-checks a closed connection reports ObjectDisposedException, which is the inert outcome.
        Exception? error = await Record.ExceptionAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
        if (error is not null)
        {
            error.Should().BeOfType<ObjectDisposedException>();
        }

        peerB.Poll(300_000, SelectMode.SelectRead).Should().BeFalse(
            "a send paused on the previous session must not write into the stream of the connection that re-rented the socket");
    }

    private (Socket Server, Socket Client) CreatePair()
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
        return (server, client);
    }

    private Connection CreateConnection(Socket server)
    {
        Connection connection = new(server, new DummyOpCodeExtractor());
        _connections.Add(connection);
        return connection;
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
