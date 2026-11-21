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
using Nalix.Abstractions.Security;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Connections;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// Regression: a stale connection that had read its pooled backing could advance the sequence counter of the connection that
/// re-rented that backing, because the counter was used without checking that the connection still owned the backing.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class ConnectionSequenceAdvanceTests : IDisposable
{
    private readonly List<IDisposable> _resources = [];

    public void Dispose()
    {
        Connection.s_afterSequenceCaptureHook = null;

        foreach (IDisposable resource in _resources)
        {
            try { resource.Dispose(); } catch (ObjectDisposedException) { }
        }
    }

    [Fact]
    public void NextSendSequence_WhenConnectionClosedAfterReadingBacking_DoesNotAdvanceReRentedCounter()
    {
        Connection stale = CreateConnection();
        ISequenceCounter? reRentedCounter = null;
        uint valueBeforeStaleCall = 0;
        Connection? newOwner = null;

        // Between the stale connection's read of its backing and its advance, the connection closes and the backing is
        // re-rented to a new connection (same thread, so the interleaving is exact).
        Connection.s_afterSequenceCaptureHook = backing =>
        {
            valueBeforeStaleCall = backing.SendSequence.Current();
            stale.Dispose();
            newOwner = CreateConnection();
            reRentedCounter = newOwner.Transport.SendSequence;
        };

        _ = ((ITransportSequencer)stale.Transport).NextSendSequence();
        Connection.s_afterSequenceCaptureHook = null;

        newOwner.Should().NotBeNull();
        ReferenceEquals(newOwner!.Transport.SendSequence, reRentedCounter).Should().BeTrue(
            "the test must exercise a re-rented backing; otherwise it cannot observe the stale advance");
        newOwner.Transport.CurrentSendSequence.Should().Be(valueBeforeStaleCall,
            "a stale connection must not advance the send sequence of the connection that re-rented its backing");
    }

    private Connection CreateConnection()
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

        Connection connection = new(server, new DummyOpCodeExtractor());
        _resources.Add(connection);
        return connection;
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
