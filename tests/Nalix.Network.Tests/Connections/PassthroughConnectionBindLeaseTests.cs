// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Transport;
using Xunit;

namespace Nalix.Network.Tests.Connections;

/// <summary>
/// Regression: a passthrough connection disposed between attaching its UDP transport and checking its disposed state
/// never returned the pooled counter holder of that binding, because the holder was only recorded after the attach.
/// </summary>
[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class PassthroughConnectionBindLeaseTests : IDisposable
{
    private readonly Socket _listener = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public PassthroughConnectionBindLeaseTests()
    {
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    }

    public void Dispose()
    {
        PassthroughConnection.s_afterUdpAttachHook = null;
        _listener.Dispose();
    }

    [Fact]
    public void BindUdp_WhenDisposedAfterAttach_ReturnsCounterHolderToPool()
    {
        // Pre-filling the pool keeps Get and Return on the stock path, so the available count moves only by bind/release.
        ObjectPoolManager.Shared.Prealloc<UdpSequenceState>(32);
        IPEndPoint remote = new(IPAddress.Loopback, 9);
        PassthroughConnection connection = new(new DummyOpCodeExtractor(), remote);
        long availableBefore = AvailableCounterHolders();

        PassthroughConnection.s_afterUdpAttachHook = static c => c.Dispose();
        connection.BindUdp(_listener, remote);
        PassthroughConnection.s_afterUdpAttachHook = null;

        AvailableCounterHolders().Should().Be(availableBefore,
            "the counter holder of a binding that was released during attach must go back to the pool");
    }

    private static long AvailableCounterHolders()
        => Convert.ToInt64(ObjectPoolManager.Shared.GetTypeInfo<UdpSequenceState>()["AvailableCount"]);

    private sealed class DummyOpCodeExtractor : IOpCodeExtractor
    {
        public ushort Extract(ReadOnlySpan<byte> payload) => 0;
    }
}
