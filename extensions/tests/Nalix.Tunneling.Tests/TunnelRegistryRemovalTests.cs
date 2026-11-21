// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Primitives;
using Nalix.Tunneling.Tests.TestSupport;

namespace Nalix.Tunneling.Tests;

public sealed class TunnelRegistryRemovalTests
{
    [Fact]
    public void Remove_WhenTokenIsPending_ReturnsTrueAndDropsPendingEntry()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();

        bool removed = registry.Remove(token);

        Assert.True(removed);
        Assert.Equal(0, registry.PendingCount);
    }

    [Fact]
    public void Resolve_WhenTokenWasRemoved_ReturnsFalse()
    {
        TunnelRegistry registry = new();
        (_, Bytes32 token) = registry.Register();
        _ = registry.Remove(token);

        bool resolved = registry.Resolve(TunnelRegistry.ComputeTunnelId(token), new FakeConnection());

        Assert.False(resolved);
    }

    [Fact]
    public void Remove_WhenTokenIsUnknown_ReturnsFalse()
    {
        TunnelRegistry registry = new();

        bool removed = registry.Remove(default);

        Assert.False(removed);
    }
}
