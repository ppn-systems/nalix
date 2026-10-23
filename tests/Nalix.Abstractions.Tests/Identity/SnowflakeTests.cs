// Copyright (c) 2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nalix.Abstractions.Exceptions;
using Nalix.Abstractions.Identity;
using Nalix.Environment.Time;
using Nalix.Framework.Identifiers;
using Xunit;

namespace Nalix.Abstractions.Tests.Identity;

public sealed class SnowflakeTests
{
    [Fact]
    public void Ctor_WithZeroValue_IsEmptyAndWritesEightZeroBytes()
    {
        Snowflake id = new(0UL); // all zero
        Assert.True(id.IsEmpty); // not empty => false

        Span<byte> buf = stackalloc byte[8];
        Assert.True(id.TryWriteBytes(buf, out int written));
        Assert.Equal(8, written);
        Assert.Equal(new byte[8], buf.ToArray());
    }

    [Fact]
    public void NewId_WithFixedComponents_WritesLittleEndianAndRoundTrips()
    {
        const uint value = 0x11223344;
        const ushort machine = 0x166; // 10 bits max
        const SnowflakeType type = (SnowflakeType)0x77;

        Snowflake id = Snowflake.NewId(value, machine, type);
        Assert.Equal(value, id.Value);
        Assert.Equal(machine, id.MachineId);
        Assert.Equal(type, id.Type);

        Span<byte> buf = stackalloc byte[8];
        Assert.True(id.TryWriteBytes(buf, out int written));
        Assert.Equal(8, written);

        // new layout (little-endian): [0-2]=Machine/Seq, [3-6]=Timestamp, [7]=Type
        // Machine 0x166 (0001 0110 0110)
        Assert.Equal(0x66, buf[0]); // bottom 8 bits of machine
        Assert.Equal(0x01, buf[1]); // top 2 bits of machine (01) + sequence (0)
        Assert.Equal(0x00, buf[2]); // sequence (0)
        Assert.Equal(0x44, buf[3]); // timestamp byte 0
        Assert.Equal(0x33, buf[4]); // timestamp byte 1
        Assert.Equal(0x22, buf[5]); // timestamp byte 2
        Assert.Equal(0x11, buf[6]); // timestamp byte 3
        Assert.Equal(0x77, buf[7]); // type

        Snowflake back = Snowflake.FromBytes(buf);
        Assert.Equal(id, back);
        Assert.True(id == back);
    }

    [Fact]
    public void TryWriteBytes_WhenDestinationTooSmall_ReturnsFalse()
    {
        Snowflake id = Snowflake.NewId(0xAABBCCDD, 0xEEFF, (SnowflakeType)0x12);
        Span<byte> small = stackalloc byte[7];
        Assert.False(id.TryWriteBytes(small, out int written));
        Assert.Equal(0, written);
    }

    [Fact]
    public void ToByteArray_WithFixedComponents_ReturnsEightLittleEndianBytes()
    {
        Snowflake id = Snowflake.NewId(0x01020304, 0x0106, (SnowflakeType)0x07);
        byte[] arr = id.ToByteArray();
        Assert.Equal(8, arr.Length);
        Assert.Equal(new byte[] { 0x06, 0x01, 0x00, 0x04, 0x03, 0x02, 0x01, 0x07 }, arr);
    }

    [Fact]
    public void FromBytes_WhenLengthIsNotEight_ThrowsSerializationFailureException()
    {
        _ = Assert.Throws<SerializationFailureException>(() => Snowflake.FromBytes([]));
        _ = Assert.Throws<SerializationFailureException>(() => Snowflake.FromBytes(new byte[7]));
        _ = Assert.Throws<SerializationFailureException>(() => Snowflake.FromBytes(new byte[9]));
    }

    [Fact]
    public void ToString_WithAnyId_ReturnsSixteenHexCharsOfSerializedBytes()
    {
        Snowflake id = Snowflake.NewId(0x00112233, 0x4455, (SnowflakeType)0x66);
        Span<byte> buf = stackalloc byte[8];
        Assert.True(id.TryWriteBytes(buf, out _));
        string hex = id.ToString(); // Convert.ToHexString(8 bytes) => 16 hex chars
        Assert.Equal(Convert.ToHexString(buf.ToArray()), hex);
        Assert.Equal(16, hex.Length);
    }

    [Fact]
    public void Equals_WithSameAndDifferentComponents_ComparesAllComponents()
    {
        Snowflake id1 = Snowflake.NewId(0xAAAAAAAA, 0xBBBB, (SnowflakeType)0xCC);
        Snowflake id2 = Snowflake.NewId(0xAAAAAAAA, 0xBBBB, (SnowflakeType)0xCC);
        Snowflake id3 = Snowflake.NewId(0xAAAAAAAA, 0xBBBB, (SnowflakeType)0xCD);

        // Same components => equal
        Assert.True(id1.Equals(id2));
        Assert.True(id1 == id2);
        Assert.False(id1 != id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());

        // Different type => not equal
        Assert.False(id1.Equals(id3));
        Assert.True(id1 != id3);

        // Dictionary key usage stable
        Dictionary<Snowflake, int> dict = new() { [id1] = 42 };
        Assert.True(dict.TryGetValue(id2, out int v));
        Assert.Equal(42, v);
    }

    [Fact]
    public void NewId_WithTypeAndMachineId_RoundTripsThroughBytes()
    {
        // NewId(type, machineId) uses SecureRandom for Value; we just check round-trip
        Snowflake id = Snowflake.NewId((SnowflakeType)0x11, machineId: 0x2222);
        Span<byte> buf = stackalloc byte[8];
        Assert.True(id.TryWriteBytes(buf, out _));
        Snowflake back = Snowflake.FromBytes(buf);
        Assert.Equal(id, back);
    }

    [Fact]
    public void SizeOf_WithExplicitLayout_IsEightBytes()
    {
        int size = Unsafe.SizeOf<Snowflake>();
        Assert.Equal(8, size);
    }

    [Fact]
    public void TryParse_WhenHexIsValid_ReturnsTrueAndParsedValue()
    {
        Snowflake source = Snowflake.NewId(0x12345678, 0x99AA, (SnowflakeType)0x11);
        string hex = source.ToString();

        bool ok = Snowflake.TryParse(hex, out Snowflake parsed);

        Assert.True(ok);
        Assert.Equal(source, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ABC")]
    [InlineData("00112233445566ZZ")]
    public void TryParse_WhenInputIsInvalid_ReturnsFalseAndEmpty(string? input)
    {
        bool ok = Snowflake.TryParse(input, out Snowflake parsed);

        Assert.False(ok);
        Assert.True(parsed.IsEmpty);
    }

    [Fact]
    public void FromBytes_WhenArrayIsNull_ThrowsSerializationFailureException()
    {
        byte[]? bytes = null;

        _ = Assert.Throws<SerializationFailureException>(() => Snowflake.FromBytes(bytes!));
    }

    [Fact]
    public void FromUInt64_WithToUInt64Output_ReturnsSameId()
    {
        Snowflake source = Snowflake.NewId(0xDEADBEEF, 0xABCD, (SnowflakeType)0x22);
        Snowflake roundTripped = Snowflake.FromUInt64(source.ToUInt64());

        Assert.Equal(source, roundTripped);
    }

    [Fact]
    public void Compare_WithOrderedIds_FollowsUnderlyingOrder()
    {
        Snowflake smaller = Snowflake.NewId(1, 1, SnowflakeType.System);
        Snowflake bigger = Snowflake.NewId(2, 1, SnowflakeType.System);

        Assert.True(smaller < bigger);
        Assert.True(smaller <= bigger);
        Assert.True(bigger > smaller);
        Assert.True(bigger >= smaller);
        Assert.Equal(-1, Math.Sign(Snowflake.Compare(smaller, bigger)));
        Assert.Equal(1, Math.Sign(Snowflake.Compare(bigger, smaller)));
        Assert.Equal(0, Snowflake.Compare(smaller, smaller));
    }

    [Fact]
    public void Equals_WithOtherISnowflakeImplementation_ReturnsFalse()
    {
        Snowflake id = Snowflake.NewId(123, 4, SnowflakeType.System);
        ISnowflake other = new StubSnowflake();

        Assert.False(id.Equals(other));
    }

    [Fact]
    public void NewId_WhenSequenceCapacityExceeded_ProducesUniqueIds()
    {
        int count = 16384 + 5;
        HashSet<ulong> ids = new(count);

        for (int i = 0; i < count; i++)
        {
            Snowflake id = Snowflake.NewId(SnowflakeType.System);
            Assert.True(ids.Add(id.ToUInt64()), $"Duplicate ID generated at index {i}: {id}");
        }
    }

    [Fact]
    public void NewId_WhenClockMovesBackward_KeepsLogicalTimestamp()
    {
        (FieldInfo lastTimestampField, FieldInfo sequenceField) = GetSnowflakeGeneratorFields();
        uint oldLastTimestamp = (uint)lastTimestampField.GetValue(null)!;
        int oldSequence = (int)sequenceField.GetValue(null)!;
        uint futureTimestamp = Clock.UnixSecondsNowUInt32() + 60;

        try
        {
            lastTimestampField.SetValue(null, futureTimestamp);
            sequenceField.SetValue(null, 0);

            Snowflake id = Snowflake.NewId(SnowflakeType.System, machineId: 1);

            Assert.Equal(futureTimestamp, id.Value);
            Assert.Equal(1, id.Sequence);
        }
        finally
        {
            lastTimestampField.SetValue(null, oldLastTimestamp);
            sequenceField.SetValue(null, oldSequence);
        }
    }

    [Fact]
    public void NewId_WhenSequenceCapacityIsExceeded_AdvancesLogicalTimestamp()
    {
        (FieldInfo lastTimestampField, FieldInfo sequenceField) = GetSnowflakeGeneratorFields();
        uint oldLastTimestamp = (uint)lastTimestampField.GetValue(null)!;
        int oldSequence = (int)sequenceField.GetValue(null)!;
        uint timestamp = Clock.UnixSecondsNowUInt32();

        try
        {
            lastTimestampField.SetValue(null, timestamp);
            sequenceField.SetValue(null, 0x3FFF);

            Snowflake id = Snowflake.NewId(SnowflakeType.System, machineId: 1);

            Assert.Equal(timestamp + 1, id.Value);
            Assert.Equal(0, id.Sequence);
        }
        finally
        {
            lastTimestampField.SetValue(null, oldLastTimestamp);
            sequenceField.SetValue(null, oldSequence);
        }
    }

    private static (FieldInfo LastTimestamp, FieldInfo Sequence) GetSnowflakeGeneratorFields()
    {
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Static;
        return (
            typeof(Snowflake).GetField("s_lastTimestamp", flags)!,
            typeof(Snowflake).GetField("s_sequence", flags)!
        );
    }

    private sealed class StubSnowflake : ISnowflake
    {
        public bool IsEmpty => false;
        public SnowflakeType Type => SnowflakeType.System;
        public uint Value => 1;
        public ushort MachineId => 1;
        public ulong ToUInt64() => 0UL;
        public byte[] ToByteArray() => new byte[8];
        public bool TryWriteBytes(Span<byte> destination) => false;
        public bool TryWriteBytes(Span<byte> destination, out int bytesWritten)
        {
            bytesWritten = 0;
            return false;
        }
    }
}
