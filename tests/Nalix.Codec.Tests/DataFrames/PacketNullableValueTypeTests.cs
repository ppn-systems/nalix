// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Abstractions.Serialization;
using Nalix.Codec.DataFrames;

namespace Nalix.Codec.Tests.DataFrames;

public sealed partial class PacketNullableValueTypeTests
{
    public PacketNullableValueTypeTests()
    {
        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    [Fact]
    public void RoundTrip_WhenTerminalNullableFieldsAreNull_PreservesNulls()
    {
        NullableValuePacket packet = new()
        {
            IsEndOfStream = true,
            Capacity = null,
            Duration = null
        };

        NullableValuePacket result = RoundTrip(packet);

        Assert.True(result.IsEndOfStream);
        Assert.Null(result.Capacity);
        Assert.Null(result.Duration);
    }

    [Fact]
    public void RoundTrip_WhenNullableFieldsArePopulated_PreservesValues()
    {
        NullableValuePacket packet = new()
        {
            IsEndOfStream = false,
            Capacity = 128,
            Duration = TimeSpan.FromSeconds(5)
        };

        NullableValuePacket result = RoundTrip(packet);

        Assert.False(result.IsEndOfStream);
        Assert.Equal(128, result.Capacity);
        Assert.Equal(TimeSpan.FromSeconds(5), result.Duration);
    }

    [Fact]
    public void RoundTrip_WhenNullableFieldsAreMixed_PreservesNullsAndValues()
    {
        NullableValuePacket packet = new()
        {
            IsEndOfStream = true,
            Capacity = null,
            Duration = TimeSpan.FromMinutes(2)
        };

        NullableValuePacket result = RoundTrip(packet);

        Assert.True(result.IsEndOfStream);
        Assert.Null(result.Capacity);
        Assert.Equal(TimeSpan.FromMinutes(2), result.Duration);
    }

    [Fact]
    public void RoundTrip_WhenTupleElementsAreNull_PreservesNulls()
    {
        TupleNullableValuePacket packet = new()
        {
            OptionalPair = (null, null)
        };
        packet.Header = packet.Header with { SequenceId = 88 };

        byte[] bytes = packet.Serialize();
        IPacket deserialized = PacketRegistry.Deserialize(bytes);
        TupleNullableValuePacket result = Assert.IsType<TupleNullableValuePacket>(deserialized);

        Assert.Equal(TupleNullableValuePacket.StaticOpCode, result.Header.OpCode);
        Assert.Equal(packet.Header.SequenceId, result.Header.SequenceId);
        Assert.Null(result.OptionalPair.Capacity);
        Assert.Null(result.OptionalPair.Duration);
    }

    private static NullableValuePacket RoundTrip(NullableValuePacket packet)
    {
        packet.Header = packet.Header with { SequenceId = 77 };

        byte[] bytes = packet.Serialize();
        IPacket deserialized = PacketRegistry.Deserialize(bytes);
        NullableValuePacket result = Assert.IsType<NullableValuePacket>(deserialized);

        Assert.Equal(NullableValuePacket.StaticOpCode, result.Header.OpCode);
        Assert.Equal(packet.Header.SequenceId, result.Header.SequenceId);

        return result;
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class NullableValuePacket : PacketBase<NullableValuePacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7A88;

        [SerializeOrder(0)]
        public bool IsEndOfStream { get; set; }

        [SerializeOrder(1)]
        public int? Capacity { get; set; }

        [SerializeOrder(2)]
        public TimeSpan? Duration { get; set; }
    }

    [Packet]
    [GenerateFormatter]
    [SerializePackable(SerializeLayout.Explicit)]
    public sealed partial class TupleNullableValuePacket : PacketBase<TupleNullableValuePacket>, IPacketStaticOpcode
    {
        public static ushort StaticOpCode => 0x7A8B;

        [SerializeOrder(0)]
        public (int? Capacity, TimeSpan? Duration) OptionalPair { get; set; }
    }
}
