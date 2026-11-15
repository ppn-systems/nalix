// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Serialization;
using Nalix.Codec.Serialization;
using Nalix.Codec.Tests.TestSupport;
using Xunit;

namespace Nalix.Codec.Tests.Serialization;

/// <summary>
/// Round-trips enum members of every integral width through generated formatters, so the wire width and the
/// read/write helpers must agree for 1-, 2-, 4- and 8-byte underlying types, signed and unsigned.
/// </summary>
public sealed class EnumWidthRoundTripTests
{
    internal enum SByteEnum : sbyte { Min = sbyte.MinValue, Neg = -5 }
    internal enum ByteEnum : byte { Max = byte.MaxValue }
    internal enum ShortEnum : short { Neg = -1234 }
    internal enum UShortEnum : ushort { Max = ushort.MaxValue }
    internal enum IntEnum : int { Neg = -1234567 }
    internal enum UIntEnum : uint { Max = uint.MaxValue }
    internal enum LongEnum : long { Min = long.MinValue, Value = -1234567890123456789L }
    internal enum ULongEnum : ulong { Max = ulong.MaxValue, Value = 12345678901234567890UL }

    [GenerateFormatter]
    internal sealed partial class WidthDto
    {
        [SerializeOrder(0)] internal SByteEnum S8 { get; set; }
        [SerializeOrder(1)] internal ByteEnum U8 { get; set; }
        [SerializeOrder(2)] internal ShortEnum S16 { get; set; }
        [SerializeOrder(3)] internal UShortEnum U16 { get; set; }
        [SerializeOrder(4)] internal IntEnum S32 { get; set; }
        [SerializeOrder(5)] internal UIntEnum U32 { get; set; }
        [SerializeOrder(6)] internal LongEnum S64 { get; set; }
        [SerializeOrder(7)] internal ULongEnum U64 { get; set; }
        [SerializeOrder(8)] internal int Sentinel { get; set; }

        internal static WidthDto Create() => new();
    }

    [Fact]
    public void RoundTrip_AllEnumWidths_PreservesValues()
    {
        WidthDto original = new()
        {
            S8 = SByteEnum.Min,
            U8 = ByteEnum.Max,
            S16 = ShortEnum.Neg,
            U16 = UShortEnum.Max,
            S32 = IntEnum.Neg,
            U32 = UIntEnum.Max,
            S64 = LongEnum.Value,
            U64 = ULongEnum.Value,
            Sentinel = 42
        };

        WidthDto result = LiteSerializerRoundTrip.RoundTrip(original);

        Assert.Equal(original.S8, result.S8);
        Assert.Equal(original.U8, result.U8);
        Assert.Equal(original.S16, result.S16);
        Assert.Equal(original.U16, result.U16);
        Assert.Equal(original.S32, result.S32);
        Assert.Equal(original.U32, result.U32);
        Assert.Equal(original.S64, result.S64);
        Assert.Equal(original.U64, result.U64);
        Assert.Equal(42, result.Sentinel);
    }

    [Fact]
    public void Serialize_EightByteEnum_WritesEightBytes()
    {
        // Fixed widths: 1 + 1 + 2 + 2 + 4 + 4 + 8 + 8 + 4 (Sentinel) = 34 bytes of payload.
        WidthDto dto = new() { S64 = LongEnum.Value, U64 = ULongEnum.Value };

        byte[] bytes = LiteSerializer.Serialize(dto);
        WidthDto result = LiteSerializer.Deserialize<WidthDto>(bytes, out _);

        Assert.Equal(LongEnum.Value, result.S64);
        Assert.Equal(ULongEnum.Value, result.U64);
        Assert.Equal(0, result.Sentinel);
    }
}
