// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Traversal.Reflector;
using NSubstitute;

namespace Nalix.Traversal.Tests.Reflector;

/// <summary>
/// Verifies reflector session allocation and lookup in <see cref="ReflectorManager"/>.
/// </summary>
public sealed class ReflectorManagerTests
{
    [Fact]
    public void CreateSession_WhenCalled_ReturnsNonZeroTokenThatResolvesToSession()
    {
        ReflectorManager manager = new();
        IConnection requester = Substitute.For<IConnection>();

        ulong token = manager.CreateSession(peerA: 1UL, peerB: 2UL, requester);

        Assert.NotEqual(0UL, token);
        Assert.True(manager.TryGetSession(token, out ReflectorSession? session));
        Assert.Equal(token, session.Token);
        Assert.Equal(1UL, session.PeerAId);
        Assert.Equal(2UL, session.PeerBId);
    }

    [Fact]
    public void CreateSession_WhenCalledTwice_IssuesDistinctTokens()
    {
        ReflectorManager manager = new();

        ulong first = manager.CreateSession(1UL, 2UL, Substitute.For<IConnection>());
        ulong second = manager.CreateSession(1UL, 2UL, Substitute.For<IConnection>());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TryGetSession_WhenTokenIsUnknown_ReturnsFalse()
    {
        ReflectorManager manager = new();

        bool found = manager.TryGetSession(0xDEADUL, out ReflectorSession? session);

        Assert.False(found);
        Assert.Null(session);
    }

    [Fact]
    public void RemoveSession_WhenSessionExists_MakesTokenUnresolvable()
    {
        ReflectorManager manager = new();
        ulong token = manager.CreateSession(1UL, 2UL, Substitute.For<IConnection>());

        manager.RemoveSession(token);

        Assert.False(manager.TryGetSession(token, out _));
    }

    [Fact]
    public void RemoveSession_WhenTokenIsUnknown_DoesNotThrow()
    {
        ReflectorManager manager = new();

        manager.RemoveSession(123UL);
    }
}
