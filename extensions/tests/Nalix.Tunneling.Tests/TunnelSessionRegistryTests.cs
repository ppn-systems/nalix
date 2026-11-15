// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using NSubstitute;

namespace Nalix.Tunneling.Tests;

/// <summary>
/// Verifies the one-session-per-connection rule and removal behaviour of <see cref="TunnelSessionRegistry"/>.
/// </summary>
public sealed class TunnelSessionRegistryTests
{
    [Fact]
    public void Register_WhenConnectionAlreadyHasSession_ThrowsInvalidOperationException()
    {
        TunnelSessionRegistry registry = new();
        IConnection consumer = Substitute.For<IConnection>();
        _ = consumer.ConnectionId.Returns(5UL);
        registry.Register(5UL, new TunnelSession(consumer, new TunnelOptions()));

        _ = Assert.Throws<InvalidOperationException>(() =>
            registry.Register(5UL, new TunnelSession(consumer, new TunnelOptions())));
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Register_WhenSessionIsNull_ThrowsArgumentNullException()
    {
        TunnelSessionRegistry registry = new();

        _ = Assert.Throws<ArgumentNullException>(() => registry.Register(1UL, null!));
    }

    [Fact]
    public void TryRemove_WhenSessionIsRegistered_ReturnsItAndDecrementsCount()
    {
        TunnelSessionRegistry registry = new();
        TunnelSession session = new(Substitute.For<IConnection>(), new TunnelOptions());
        registry.Register(3UL, session);

        bool removed = registry.TryRemove(3UL, out TunnelSession? found);

        Assert.True(removed);
        Assert.Same(session, found);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void TryRemove_WhenNoSessionIsRegistered_ReturnsFalse()
    {
        TunnelSessionRegistry registry = new();

        bool removed = registry.TryRemove(99UL, out TunnelSession? found);

        Assert.False(removed);
        Assert.Null(found);
    }
}
