// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Configuration;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;

namespace Nalix.Framework.Tests.Memory;

[Collection(TestCollections.SequentialPooling)]
public sealed class PacketLifecycleTests : IDisposable
{
    private readonly ObjectPoolManager _manager;
    public PacketLifecycleTests()
    {
        var config = new ObjectPoolOptions { EnableDiagnostics = true };
        _manager = new ObjectPoolManager(config);
        PacketRegistry.Configure(_manager);
    }

    public void Dispose()
    {
        // Restore previous manager if any, otherwise clear
        PacketRegistry.Configure(null!);

        _manager.ResetStatistics();
    }

    [Fact]
    public void Dispose_WhenScopeDisposed_ReturnsPacketToPool()
    {
        Control packet = _manager.Get<Control>();
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);

        // Create a lease
        using (var lease = new PacketScope<Control>(packet))
        {
            Assert.Same(packet, lease.Value);
        }

        // Disposal of lease should have called packet.Dispose() which returns to manager
        Assert.Equal(0L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["TotalReturns"]);
    }

    [Fact]
    public void Dispose_WhenCalledTwice_ReturnsPacketOnce()
    {
        Assert.NotNull(PacketRegistry.Manager);
        Assert.Same(_manager, PacketRegistry.Manager);
        Control packet = _manager.Get<Control>();
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);

        // First disposal
        packet.Dispose();
        Assert.Equal(0L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["TotalReturns"]);

        // A second disposal never returns the packet again. Release builds ignore it; Debug builds report it, because
        // a stale second owner could otherwise put a packet that another thread has rented back into the pool.
#if DEBUG
        Assert.Throws<InvalidOperationException>(packet.Dispose);
#else
        packet.Dispose();
#endif
        Assert.Equal(0L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["TotalReturns"]); // Should NOT be 2
    }
}
