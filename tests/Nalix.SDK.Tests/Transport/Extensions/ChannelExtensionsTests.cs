// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;
using Nalix.Environment.Memory;
using Nalix.SDK.Options;
using Nalix.SDK.Transport;
using Nalix.SDK.Transport.Extensions;
using Nalix.SDK.Tests.TestSupport;

namespace Nalix.SDK.Tests.Transport.Extensions;

[SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "xUnit tests intentionally follow the test synchronization context.")]
public sealed class ChannelExtensionsTests
{
    public ChannelExtensionsTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public async Task SubscribeChannel_WhenBoundedChannelDropsWrite_DisposesDroppedPacket()
    {
        NullableStreamItem.DisposeCount = 0;

        FakeStreamSession session = new();
        ChannelReader<NullableStreamItem> reader = session.SubscribeChannel<NullableStreamItem>(
            boundedCapacity: 1,
            fullMode: BoundedChannelFullMode.DropWrite);

        session.Emit(new NullableStreamItem { IsEndOfStream = false, Capacity = 1 });
        session.Emit(new NullableStreamItem { IsEndOfStream = false, Capacity = 2 });

        NullableStreamItem retained = await reader.ReadAsync();

        Assert.Equal(1, retained.Capacity);
        Assert.Equal(1, NullableStreamItem.DisposeCount);
    }
}
