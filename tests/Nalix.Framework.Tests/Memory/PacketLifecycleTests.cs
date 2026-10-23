// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Environment.Configuration;
using Nalix.Framework.Memory.Objects;
using Nalix.Framework.Options;
using Nalix.Codec.Pooling;

namespace Nalix.Framework.Tests.Memory;

[Collection("Sequential Pooling Tests")]
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
    public void PacketLease_Struct_Disposal_ReturnsToPool()
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
    public void PacketBase_AtomicDisposal_PreventsDoubleReturn()
    {
        Assert.NotNull(PacketRegistry.Manager);
        Assert.Same(_manager, PacketRegistry.Manager);
        Control packet = _manager.Get<Control>();
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);

        // First disposal
        packet.Dispose();
        Assert.Equal(0L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["TotalReturns"]);

        // Second disposal should be a no-op
        packet.Dispose();
        Assert.Equal(0L, (long)_manager.GetTypeInfo<Control>()["Outstanding"]);
        Assert.Equal(1L, (long)_manager.GetTypeInfo<Control>()["TotalReturns"]); // Should NOT be 2
    }
}















