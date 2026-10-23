// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Codec.Serialization.Formatters.Primitives;
using Nalix.Environment.Memory;

namespace Nalix.Codec.Tests.Serialization.Formatters.Primitives;

public sealed class EnumFormatterTests
{
    public enum ByteKind : byte { A = 1, B = 250 }
    public enum IntKind : int { Min = int.MinValue, Zero = 0, Max = int.MaxValue }
    public enum LongKind : long { Neg = -9_000_000_000, Pos = 9_000_000_000 }
    public enum ULongKind : ulong { Low = 7, High = ulong.MaxValue }

    [Theory]
    [InlineData(ByteKind.A)]
    [InlineData(ByteKind.B)]
    public void SerializeDeserialize_WhenByteEnum_RoundTripsValue(ByteKind value)
    {
        ByteKind actual = RoundTrip(value, new EnumFormatter<ByteKind>());
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData(IntKind.Min)]
    [InlineData(IntKind.Zero)]
    [InlineData(IntKind.Max)]
    public void SerializeDeserialize_WhenIntEnum_RoundTripsValue(IntKind value)
    {
        IntKind actual = RoundTrip(value, new EnumFormatter<IntKind>());
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData(LongKind.Neg)]
    [InlineData(LongKind.Pos)]
    public void SerializeDeserialize_WhenLongEnum_RoundTripsValue(LongKind value)
    {
        LongKind actual = RoundTrip(value, new EnumFormatter<LongKind>());
        Assert.Equal(value, actual);
    }

    [Theory]
    [InlineData(ULongKind.Low)]
    [InlineData(ULongKind.High)]
    public void SerializeDeserialize_WhenULongEnum_RoundTripsValue(ULongKind value)
    {
        ULongKind actual = RoundTrip(value, new EnumFormatter<ULongKind>());
        Assert.Equal(value, actual);
    }

    [Fact]
    public void Ctor_WhenTypeIsNotEnum_ThrowsTypeInitializationException() => _ = Assert.Throws<TypeInitializationException>(() => new EnumFormatter<int>());

    private static TEnum RoundTrip<TEnum>(TEnum value, EnumFormatter<TEnum> formatter)
    {
        DataWriter writer = new(8);
        try
        {
            formatter.Serialize(ref writer, value);
            byte[] bytes = writer.ToArray();
            DataReader reader = new(bytes);
            return formatter.Deserialize(ref reader);
        }
        finally
        {
            writer.Dispose();
        }
    }
}



















