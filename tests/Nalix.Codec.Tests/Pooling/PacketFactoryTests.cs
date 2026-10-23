// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.DataFrames;
using Nalix.Codec.Pooling;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Memory.Objects;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Serialization;

namespace Nalix.Codec.Tests.Pooling;

[Collection(TestCollections.PooledBufferScrub)]
public sealed partial class PacketFactoryTests
{
    [Fact]
    public void Acquire_WhenScopeIsDisposedTwice_ReturnsValueAndIgnoresSecondDispose()
    {
        using PacketScope<Control> lease = PacketFactory<Control>.Acquire();

        Assert.NotNull(lease.Value);

        // explicit second dispose should be ignored
        lease.Dispose();
        lease.Dispose();
    }

    [Fact]
    public void Prealloc_WhenFollowedByClearPool_ReturnsNonNegativeCounts()
    {
        ObjectPoolManager manager = new();
        PacketRegistry.Configure(manager);

        try
        {
            int preallocated = manager.Prealloc<Control>(3);
            int cleared = manager.ClearPool<Control>();

            Assert.True(preallocated >= 0);
            Assert.True(cleared >= 0);
        }
        finally
        {
            PacketRegistry.Configure(null!);
        }
    }

    [Fact]
    public void Acquire_WhenPreviousScopeWasDisposed_ReturnsPacketWithClearedCollections()
    {
        ObjectPoolManager manager = new();
        PacketRegistry.Configure(manager);

        try
        {
            using (PacketScope<PooledCollectionPacket> lease = PacketFactory<PooledCollectionPacket>.Acquire())
            {
                lease.Value.Items ??= [];
                lease.Value.Items.Add(42);
            }

            using PacketScope<PooledCollectionPacket> nextLease = PacketFactory<PooledCollectionPacket>.Acquire();

            Assert.NotNull(nextLease.Value.Items);
            Assert.Empty(nextLease.Value.Items);
        }
        finally
        {
            PacketRegistry.Configure(null);
        }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class PooledCollectionPacket : PacketBase<PooledCollectionPacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7B01;

        [SerializeOrder(0)]
        public List<int>? Items { get; set; }
    }
}
















