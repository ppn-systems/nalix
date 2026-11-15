// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using FluentAssertions;
using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Protocols;
using Nalix.Network.Listeners.Udp;
using Nalix.Network.Options;
using Nalix.Network.RateLimiting;
using Nalix.Network.Tests.TestSupport;
using NSubstitute;
using Xunit;

namespace Nalix.Network.Tests.Listeners.UdpListener;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
[Collection(TestCollections.NetworkConfig)]
public sealed class UdpListenerSocketConfigTests
{
    [Fact]
    public void ConfigureSocket_WhenUdpReuseAddressDisabled_DoesNotEnableAddressSharing()
    {
        using ConfigurationOptionsScope<NetworkSocketOptions> scope = new();
        scope.Options.UdpReuseAddress = false;

        using ConnectionGuard guard = new(new ConnectionQuotaOptions { ExemptLoopback = true });
        using UdpPassthroughListener listener = new(0, Substitute.For<IProtocol>(), Substitute.For<IConnectionHub>(), guard);

        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        MethodInfo configure = typeof(UdpListenerBase).GetMethod("ConfigureSocket", BindingFlags.Instance | BindingFlags.NonPublic)!;
        configure.Invoke(listener, [socket]);

        socket.ExclusiveAddressUse.Should().BeTrue("a UDP listener must not allow another socket to share its address by default");
        ((int)socket.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress)).Should().Be(0);
    }
}
