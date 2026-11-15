// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Networking.Packets;
using NSubstitute;

namespace Nalix.Tunneling.Tests;

/// <summary>
/// Verifies channel-to-provider bookkeeping in <see cref="ProviderRegistry"/>.
/// </summary>
public sealed class ProviderRegistryTests
{
    [Fact]
    public void TryRegister_WhenChannelIsFree_ReturnsTrue()
    {
        ProviderRegistry registry = new();

        bool registered = registry.TryRegister(1, Substitute.For<IConnection>(), Substitute.For<IPacketSender>());

        Assert.True(registered);
    }

    [Fact]
    public void TryRegister_WhenChannelIsAlreadyTaken_ReturnsFalseAndKeepsFirstProvider()
    {
        ProviderRegistry registry = new();
        IConnection first = Substitute.For<IConnection>();
        IPacketSender firstSender = Substitute.For<IPacketSender>();
        _ = registry.TryRegister(9, first, firstSender);

        bool second = registry.TryRegister(9, Substitute.For<IConnection>(), Substitute.For<IPacketSender>());

        Assert.False(second);
        Assert.True(registry.TryGetProvider(9, out IConnection? connection, out IPacketSender? sender));
        Assert.Same(first, connection);
        Assert.Same(firstSender, sender);
    }

    [Fact]
    public void TryGetProvider_WhenChannelWasNeverRegistered_ReturnsFalseAndNullOutputs()
    {
        ProviderRegistry registry = new();

        bool found = registry.TryGetProvider(42, out IConnection? connection, out IPacketSender? sender);

        Assert.False(found);
        Assert.Null(connection);
        Assert.Null(sender);
    }

    [Fact]
    public void TryRegister_WhenConnectionIsNull_ThrowsArgumentNullException()
    {
        ProviderRegistry registry = new();

        _ = Assert.Throws<ArgumentNullException>(() =>
            registry.TryRegister(1, null!, Substitute.For<IPacketSender>()));
    }

    [Fact]
    public void TryRegister_WhenSenderIsNull_ThrowsArgumentNullException()
    {
        ProviderRegistry registry = new();

        _ = Assert.Throws<ArgumentNullException>(() =>
            registry.TryRegister(1, Substitute.For<IConnection>(), null!));
    }
}
