// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests;

public sealed class ProviderRegistryOwnershipTests
{
    [Fact]
    public void TryRegister_WhenChannelOwnedByAnotherConnection_ReturnsFalse()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new();
        FakeConnection squatter = new();

        Assert.True(registry.TryRegister(7, owner, new FakePacketSender()));

        bool registered = registry.TryRegister(7, squatter, new FakePacketSender());

        Assert.False(registered);
    }

    [Fact]
    public void TryRegister_WhenChannelOwnedByAnotherConnection_KeepsOriginalOwner()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new();
        FakeConnection squatter = new();
        _ = registry.TryRegister(7, owner, new FakePacketSender());

        _ = registry.TryRegister(7, squatter, new FakePacketSender());

        Assert.True(registry.TryGetProvider(7, out IConnection? connection, out _));
        Assert.Same(owner, connection);
    }

    [Fact]
    public void TryRegister_WhenOwnerDisconnects_ReleasesChannel()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new();
        _ = registry.TryRegister(7, owner, new FakePacketSender());

        owner.RaiseClosed();

        Assert.False(registry.TryGetProvider(7, out _, out _));
    }

    [Fact]
    public void TryRegister_WhenPreviousOwnerDisconnected_AllowsNewOwner()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new();
        FakeConnection replacement = new();
        _ = registry.TryRegister(7, owner, new FakePacketSender());
        owner.RaiseClosed();

        bool registered = registry.TryRegister(7, replacement, new FakePacketSender());

        Assert.True(registered);
        Assert.True(registry.TryGetProvider(7, out IConnection? connection, out _));
        Assert.Same(replacement, connection);
    }

    [Fact]
    public void TryUnregister_WhenCallerIsNotOwner_LeavesChannelOwned()
    {
        ProviderRegistry registry = new();
        FakeConnection owner = new();
        FakeConnection other = new();
        _ = registry.TryRegister(7, owner, new FakePacketSender());

        bool released = registry.TryUnregister(7, other);

        Assert.False(released);
        Assert.True(registry.TryGetProvider(7, out IConnection? connection, out _));
        Assert.Same(owner, connection);
    }
}
