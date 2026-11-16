// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Security;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// A passthrough connection owns the counters of its UDP binding. A transport that was attached and then released during the
/// bind must stay inert: it must not advance the counters the connection keeps for its next binding.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class PassthroughConnectionBindTests : IDisposable
{
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public PassthroughConnectionBindTests()
    {
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        PassthroughConnection.s_afterUdpAttachHook = null;
        _listener.Dispose();
    }

    [Fact]
    public void BindUdp_WhenDisposedAfterAttach_DetachesTransportFromItsCounters()
    {
        IPEndPoint remote = new(IPAddress.Loopback, 9);
        PassthroughConnection connection = new(new DummyOpCodeExtractor(), remote);

        SocketUdpTransport? attached = null;
        ISequenceCounter? counter = null;
        uint valueBeforeStaleCall = 0;
        PassthroughConnection.s_afterUdpAttachHook = c =>
        {
            attached = (SocketUdpTransport)c.UDP;
            counter = (ISequenceCounter)typeof(SocketUdpTransport)
                .GetField("_sendSequence", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(attached)!;
            valueBeforeStaleCall = counter.Current();
            c.Dispose();
        };

        connection.BindUdp(_listener, remote);
        PassthroughConnection.s_afterUdpAttachHook = null;

        attached.Should().NotBeNull("the hook must have observed the attached transport");
        _ = ((ITransportSequencer)attached!).NextSendSequence();

        counter!.Current().Should().Be(valueBeforeStaleCall,
            "a transport released during attach must be detached from its counters, so a stale call cannot advance them");
    }

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
