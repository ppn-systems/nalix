// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nalix.Abstractions;
using Nalix.Abstractions.Identity;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Security;
using Nalix.Environment.Fragments;
using Nalix.Environment.Memory;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Nalix.Framework.Memory.Objects;
using Nalix.Network.Connections;
using Nalix.Network.Internal.Time;
using Nalix.Network.Internal.Transport;
using Nalix.Network.Listeners.Udp;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Nalix.Network.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Internal.Transport;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class SocketEndpointTests : IDisposable
{
    private readonly ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> _blacklistScope = new();

    private ConnectionGuard CreateIsolatedGuard(ConnectionQuotaOptions options)
    {
        _blacklistScope.Options.Enabled = false;
        return new ConnectionGuard(options);
    }

    public void Dispose() => _blacklistScope.Dispose();

    [Fact]
    public void FromRawBytes_WithIPv4MappedIPv6_NormalizesToIPv4AndReleasesGuardSlots()
    {
        // ::ffff:203.0.113.42 in 16 raw bytes (as sent by PROXY v1 TCP6 / v2 AF_INET6)
        byte[] ipv4MappedBytes =
        [
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0xFF, 0xFF, 203, 0, 113, 42
        ];

        SocketEndpoint fromRaw = SocketEndpoint.FromRawBytes(ipv4MappedBytes, 54321, isIPv6: true);
        IPAddress realIp = new(ipv4MappedBytes);
        SocketEndpoint fromIp = SocketEndpoint.FromIpAddress(realIp);

        fromRaw.IsIPv6.Should().BeFalse("IPv4-mapped IPv6 raw bytes must be normalized to IPv4 just like NormalizeAddress");
        fromRaw.Should().Be(fromIp);
        fromRaw.GetHashCode().Should().Be(fromIp.GetHashCode());

        ConnectionQuotaOptions options = new()
        {
            MaxConnectionsPerIpAddress = 1,
            MaxConnectionsPerSubnet = 1,
            MaxConnectionsPerWindow = 100,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        // Proxy accept path uses TryAccept(SocketEndpoint) from FromRawBytes
        guard.TryAccept(fromRaw).Should().BeTrue();
        guard.TryAccept(fromRaw).Should().BeFalse("per-IP and per-subnet limit is 1");

        // Connection close path uses Connection.NetworkEndpoint (created via FromIpAddress / NormalizeAddress)
        IConnectionEventArgs args = Substitute.For<IConnectionEventArgs>();
        args.Connection.NetworkEndpoint.Returns(fromIp);
        guard.OnConnectionClosed(null, args);

        // Both IP slot and subnet slot must be released
        guard.TryAccept(fromRaw).Should().BeTrue("closing the normalized connection must release the slot acquired by FromRawBytes");
    }
}
