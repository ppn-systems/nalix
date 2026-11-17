// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

/// <summary>
/// Regression: an admitted async send that finished while its connection's <c>Dispose</c> was still running released the
/// last count on the pooled socket instance. The instance was returned to the pool mid-dispose, and the rest of
/// <c>Dispose</c> then disposed the socket of whichever connection re-rented it.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class SocketConnectionDisposeTests : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public void Dispose()
    {
        SocketConnection.s_afterShutdownHook = null;

        foreach (IDisposable resource in _resources)
        {
            try { resource.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public async Task Dispose_WhenAdmittedSendFinishesDuringDispose_DoesNotReturnInstanceBeforeDisposeEnds()
    {
        (Socket server, Socket client) = CreatePair();
        server.SendBufferSize = 4096;
        client.ReceiveBufferSize = 4096;

        Connection connection = new(server, new DummyOpCodeExtractor());
        _resources.Add(connection);

        // The peer never reads, so an admitted async send stays pending until the socket is shut down.
        byte[] payload = new byte[60_000];
        Task pending = null!;
        for (int i = 0; i < 10_000 && pending is null; i++)
        {
            ValueTask vt = connection.TCP.SendAsync(payload);
            if (!vt.IsCompleted)
            {
                pending = vt.AsTask();
            }
        }

        pending.Should().NotBeNull("an async send must still be in flight for the test to exercise the race");

        SocketConnection? instance = null;
        int generationDuringDispose = 0;
        bool pendingCompletedBeforeDisposeContinued = false;
        int generationAfterPendingFinished = 0;

        SocketConnection.s_afterShutdownHook = socket =>
        {
            instance = socket;
            generationDuringDispose = socket.Generation;

            // Shutdown has released the pending send. Wait for it to finish while DISPOSE is still on the stack.
            try { _ = pending.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }

            pendingCompletedBeforeDisposeContinued = pending.IsCompleted;
            generationAfterPendingFinished = socket.Generation;
        };

        connection.Dispose();

        pendingCompletedBeforeDisposeContinued.Should().BeTrue("the pending send must have finished while DISPOSE was paused");
        generationAfterPendingFinished.Should().Be(generationDuringDispose,
            "the instance must not be returned to the pool while its own DISPOSE is still running");
        instance.Should().NotBeNull();
        instance!.Generation.Should().NotBe(generationDuringDispose,
            "once DISPOSE finishes, the instance must be returned to the pool");
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
