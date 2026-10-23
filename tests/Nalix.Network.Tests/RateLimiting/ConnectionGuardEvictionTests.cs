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

namespace Nalix.Network.Tests.RateLimiting;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class ConnectionGuardEvictionTests : IDisposable
{
    private readonly ConfigurationOptionsScope<ConnectionBlacklistStoreOptions> _blacklistScope = new();

    private ConnectionGuard CreateIsolatedGuard(ConnectionQuotaOptions options)
    {
        _blacklistScope.Options.Enabled = false;
        return new ConnectionGuard(options);
    }

    public void Dispose() => _blacklistScope.Dispose();

    [Fact]
    public void TryAccept_WhenMapAtCapacity_DoesNotEvictBannedEndpoints()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 5,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        List<IPAddress> bannedIps = [];
        for (int i = 1; i <= 5; i++)
        {
            IPAddress ip = new([198, 51, (byte)i, 1]);
            bannedIps.Add(ip);
            guard.BanEndpoint(ip, TimeSpan.FromHours(24));
        }

        // Trigger eviction attempts with new IPs when all 5 tracked entries are banned
        for (int i = 10; i < 20; i++)
        {
            IPEndPoint newEp = new(new IPAddress([203, 0, (byte)i, 1]), 1234);
            _ = guard.TryAccept(newEp);
        }

        // None of the 5 banned IPs may be evicted or allowed to connect
        foreach (IPAddress bannedIp in bannedIps)
        {
            guard.TryAccept(new IPEndPoint(bannedIp, 1234)).Should().BeFalse($"banned IP {bannedIp} must never be evicted by TRY_RANDOM_SAMPLE_EVICT");
        }
    }

    [Fact]
    public void TryAccept_WhenSubnetMapsAtCapacity_EvictsIdleEntriesAndClearsOnDispose()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 5,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        ConnectionGuard guard = CreateIsolatedGuard(options);

        // Connect and release 5 distinct IPv4 /24 subnets so all 5 have CurrentConnections = 0
        for (int i = 1; i <= 5; i++)
        {
            IPEndPoint ep = new(new IPAddress([198, (byte)i, 1, 1]), 1234);
            guard.TryAccept(ep).Should().BeTrue();
            guard.Release(ep);
        }

        FieldInfo subnetV4Field = typeof(ConnectionGuard).GetField("_subnetMapV4", BindingFlags.Instance | BindingFlags.NonPublic)!;
        FieldInfo subnetV6Field = typeof(ConnectionGuard).GetField("_subnetMapV6", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var subnetMapV4 = (System.Collections.ICollection)subnetV4Field.GetValue(guard)!;
        var subnetMapV6 = (System.Collections.ICollection)subnetV6Field.GetValue(guard)!;

        subnetMapV4.Count.Should().Be(5);

        // Adding a 6th distinct /24 subnet when the previous 5 are idle should evict one idle subnet entry
        IPEndPoint sixthEp = new(new IPAddress([198, 99, 1, 1]), 1234);
        guard.TryAccept(sixthEp).Should().BeTrue();
        subnetMapV4.Count.Should().BeLessThanOrEqualTo(5, "_subnetMapV4 must evict idle subnet entries when reaching MaxTrackedEndpoints");

        // Also add an IPv6 entry
        IPEndPoint v6Ep = new(IPAddress.Parse("2606:4700:4700::1111"), 1234);
        guard.TryAccept(v6Ep).Should().BeTrue();
        subnetMapV6.Count.Should().BeGreaterThan(0);

        guard.Dispose();

        subnetMapV4.Count.Should().Be(0, "Dispose() must clear _subnetMapV4");
        subnetMapV6.Count.Should().Be(0, "Dispose() must clear _subnetMapV6");
    }

    [Fact]
    public void TryAccept_WhenFirstEntriesAreActive_EvictsIdleEntryFurtherInMap()
    {
        ConnectionQuotaOptions options = new()
        {
            MaxTrackedEndpoints = 12,
            MaxConnectionsPerIpAddress = 5,
            MaxConnectionsPerWindow = 50,
            MaxConnectionsPerSubnet = 50,
            MaxSubnetConnectionsPerWindow = 100,
            ExemptLoopback = false
        };
        using ConnectionGuard guard = CreateIsolatedGuard(options);

        for (int i = 1; i <= 12; i++)
        {
            IPEndPoint ep = new(new IPAddress([198, 18, (byte)i, 1]), 1234);
            guard.TryAccept(ep).Should().BeTrue();
        }

        FieldInfo mapField = typeof(ConnectionGuard).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (ConcurrentDictionary<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry>)mapField.GetValue(guard)!;

        // Keep the first 6 entries in enumerator order active (CurrentConnections = 1),
        // and release the remaining 6 entries so CurrentConnections = 0.
        int index = 0;
        foreach (KeyValuePair<SocketEndpoint, ConnectionGuard.ConnectionLimitEntry> kvp in map)
        {
            if (index >= 6)
            {
                IConnectionEventArgs args = Substitute.For<IConnectionEventArgs>();
                args.Connection.NetworkEndpoint.Returns(kvp.Key);
                guard.OnConnectionClosed(null, args);
            }
            index++;
        }

        FieldInfo cleanedField = typeof(ConnectionGuard).GetField("_totalCleanedEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long beforeCleaned = (long)cleanedField.GetValue(guard)!;

        // Trigger eviction by accepting a 13th unique IP
        IPEndPoint extraEp = new(new IPAddress([198, 19, 99, 1]), 1234);
        guard.TryAccept(extraEp).Should().BeTrue();

        long afterCleaned = (long)cleanedField.GetValue(guard)!;
        afterCleaned.Should().BeGreaterThan(beforeCleaned, "TRY_RANDOM_SAMPLE_EVICT must skip active entries and evict an idle entry beyond the first 5 elements");
    }
}
