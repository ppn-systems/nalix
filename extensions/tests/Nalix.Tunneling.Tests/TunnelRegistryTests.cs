// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking;
using Nalix.Abstractions.Primitives;
using NSubstitute;

namespace Nalix.Tunneling.Tests;

/// <summary>
/// Verifies the pending tunnel handshake table in <see cref="TunnelRegistry"/>.
/// </summary>
public sealed class TunnelRegistryTests
{
    [Fact]
    public void Register_WhenCalled_ReturnsDistinctTokensAndTracksEachPendingRequest()
    {
        TunnelRegistry registry = new();

        (Task<IConnection> firstTask, Bytes32 firstToken) = registry.Register();
        (Task<IConnection> secondTask, Bytes32 secondToken) = registry.Register();

        Assert.NotEqual(firstToken, secondToken);
        Assert.False(firstToken.IsZero);
        Assert.Equal(2, registry.PendingCount);
        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
    }

    [Fact]
    public async Task Resolve_WhenTokenIsPending_CompletesTaskWithProviderConnectionAndRemovesEntry()
    {
        TunnelRegistry registry = new();
        IConnection provider = Substitute.For<IConnection>();
        (Task<IConnection> task, Bytes32 token) = registry.Register();

        bool resolved = registry.Resolve(TunnelRegistry.ComputeTunnelId(token), provider);

        Assert.True(resolved);
        Assert.Same(provider, await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void Resolve_WhenRawTokenIsUsedAsKey_ReturnsFalse()
    {
        // The wire carries Keccak256(token); presenting the secret token itself must not resolve anything.
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();

        bool resolved = registry.Resolve(token, Substitute.For<IConnection>());

        Assert.False(resolved);
        Assert.Equal(1, registry.PendingCount);
    }

    [Fact]
    public void Resolve_WhenTokenIsReusedAfterResolution_ReturnsFalse()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();
        _ = registry.Resolve(TunnelRegistry.ComputeTunnelId(token), Substitute.For<IConnection>());

        bool secondAttempt = registry.Resolve(TunnelRegistry.ComputeTunnelId(token), Substitute.For<IConnection>());

        Assert.False(secondAttempt);
    }

    [Fact]
    public void Resolve_WhenTokenIsUnknown_ReturnsFalseAndLeavesPendingRequestsAlone()
    {
        TunnelRegistry registry = new();
        _ = registry.Register();

        bool resolved = registry.Resolve(Bytes32.Parse(new string('1', Bytes32.Size * 2)), Substitute.For<IConnection>());

        Assert.False(resolved);
        Assert.Equal(1, registry.PendingCount);
    }

    [Fact]
    public void Resolve_WhenProviderConnectionIsNull_ThrowsArgumentNullException()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();

        _ = Assert.Throws<ArgumentNullException>(() => registry.Resolve(TunnelRegistry.ComputeTunnelId(token), null!));
        Assert.Equal(1, registry.PendingCount);
    }

    [Fact]
    public void CleanupStale_WhenEntriesAreWithinTimeout_RemovesNothing()
    {
        TunnelRegistry registry = new();
        _ = registry.Register();
        _ = registry.Register();

        int cleaned = registry.CleanupStale(TimeSpan.FromHours(1));

        Assert.Equal(0, cleaned);
        Assert.Equal(2, registry.PendingCount);
    }
}
