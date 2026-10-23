// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net;
using FluentAssertions;
using Nalix.Network.RateLimiting;
using Xunit;

namespace Nalix.Network.Tests.RateLimiting;

public sealed class DatagramGuardTests
{
    [Fact]
    public void TryAccept_WhenIPv4TrackingCapReached_RejectsNewSourcesButAllowsExistingSource()
    {
        using DatagramGuard guard = new(new Nalix.Network.Options.DatagramGuardOptions { IPv4Windows = 2, IPv6Windows = 2 }, new Nalix.Network.Options.ConnectionGuardOptions { MaxPacketPerSecond = 10 });
        IPEndPoint first = new(IPAddress.Parse("10.0.0.1"), 12345);
        IPEndPoint second = new(IPAddress.Parse("10.0.0.2"), 12345);
        IPEndPoint third = new(IPAddress.Parse("10.0.0.3"), 12345);

        guard.TryAccept(first).Should().BeTrue();
        guard.TryAccept(second).Should().BeTrue();
        guard.TryAccept(third).Should().BeFalse();
        guard.TryAccept(first).Should().BeTrue();
    }

    [Fact]
    public void TryAccept_WhenPacketsPerSecondExceeded_ReturnsFalse()
    {
        using DatagramGuard guard = new(new Nalix.Network.Options.DatagramGuardOptions { IPv4Windows = 4, IPv6Windows = 4 }, new Nalix.Network.Options.ConnectionGuardOptions { MaxPacketPerSecond = 2 });
        IPEndPoint endpoint = new(IPAddress.Parse("10.0.1.1"), 12345);

        guard.TryAccept(endpoint).Should().BeTrue();
        guard.TryAccept(endpoint).Should().BeTrue();
        guard.TryAccept(endpoint).Should().BeFalse();
    }
}
