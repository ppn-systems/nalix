// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System;
using Nalix.Abstractions.Exceptions;
using Nalix.Codec.Extensions;
using Nalix.Environment.Memory;
using Nalix.Framework.Extensions;
using Nalix.Framework.Memory.Buffers;
using Xunit;

namespace Nalix.Framework.Tests.Extensions;

public sealed class DataReaderWriterExtensionsTests
{
    [Fact]
    public void WriteAndRead_WhenValuesRoundTripped_PreservesWrittenValues()
    {
        DataWriter writer = new(128);

        writer.Write((sbyte)-5);
        writer.Write((byte)250);
        writer.Write((short)-1234);
        writer.Write((ushort)43210);
        writer.Write(123456789u);
        writer.Write(-1234567);
        writer.Write(-1234567890123456789L);
        writer.Write(12345678901234567890UL);
        writer.Write(true);
        writer.Write('N');
        writer.Write(1.25f);
        writer.Write(2.5d);
        writer.WriteEnum(ByteEnum.C);
        writer.WriteEnum(UShortEnum.B);
        writer.WriteEnum(UIntEnum.C);
        writer.WriteEnum(IntEnum.B);
        writer.Write([10, 11]);
        writer.Write((ReadOnlySpan<byte>)[12, 13, 14]);
        writer.WriteUnmanaged(new Pair { A = 77, B = 99 });

        byte[] payload = writer.ToArray();
        writer.Dispose();

        DataReader reader = new(payload);

        Assert.Equal((sbyte)-5, reader.ReadSByte());
        Assert.Equal((byte)250, reader.ReadByte());
        Assert.Equal((short)-1234, reader.ReadInt16());
        Assert.Equal((ushort)43210, reader.ReadUInt16());
        Assert.Equal(123456789u, reader.ReadUInt32());
        Assert.Equal(-1234567, reader.ReadInt32());
        Assert.Equal(-1234567890123456789L, reader.ReadInt64());
        Assert.Equal(12345678901234567890UL, reader.ReadUInt64());
        Assert.True(reader.ReadBoolean());
        Assert.Equal('N', reader.ReadChar());
        Assert.Equal(1.25f, reader.ReadSingle(), 3);
        Assert.Equal(2.5d, reader.ReadDouble(), 5);
        Assert.Equal(ByteEnum.C, reader.ReadEnumByte<ByteEnum>());
        Assert.Equal(UShortEnum.B, reader.ReadEnumUInt16<UShortEnum>());
        Assert.Equal(UIntEnum.C, reader.ReadEnumUInt32<UIntEnum>());
        Assert.Equal((int)IntEnum.B, reader.ReadInt32());
        Assert.Equal([10, 11], reader.ReadBytes(2));
        Assert.Equal([12, 13, 14], reader.ReadBytes(3));

        Pair pair = reader.ReadUnmanaged<Pair>();
        Assert.Equal(77, pair.A);
        Assert.Equal(99, pair.B);
        Assert.Equal(0, reader.Remaining());
    }

    [Fact]
    public void WriteEnum_WhenUnderlyingTypeIsUnsupported_ThrowsNotSupportedException()
    {
        DataWriter writer = new(16);
        NotSupportedException? exception = null;
        try
        {
            writer.WriteEnum(LongEnum.A);
        }
        catch (NotSupportedException ex)
        {
            exception = ex;
        }

        Assert.NotNull(exception);
        Assert.Contains("not supported", exception.Message, StringComparison.OrdinalIgnoreCase);
        writer.Dispose();
    }

    [Fact]
    public void ReadBytes_WhenCountIsZero_ReturnsEmptyWithoutAdvancing()
    {
        DataReader reader = new([1, 2, 3]);

        byte[] data = reader.ReadBytes(0);

        Assert.Empty(data);
        Assert.Equal(3, reader.Remaining());
    }

    [Fact]
    public void ReadBytes_WhenCountIsNegative_MarksReaderFailed()
    {
        DataReader reader = new([1, 2, 3]);

        _ = reader.ReadBytes(-1);

        Assert.True(reader.IsFailed);
        Assert.Equal(3, reader.Remaining());
    }

    [Fact]
    public void ReadRemainingBytes_WhenSomeBytesWereRead_ReturnsAllUnreadData()
    {
        DataReader reader = new([1, 2, 3, 4]);
        _ = reader.ReadByte();

        byte[] remaining = reader.ReadRemainingBytes();

        Assert.Equal([2, 3, 4], remaining);
        Assert.Equal(0, reader.Remaining());
    }

    [Fact]
    public void ReadUInt64_WhenNotEnoughData_MarksReaderFailed()
    {
        DataReader reader = new([1, 2, 3]);

        _ = reader.ReadUInt64();

        Assert.True(reader.IsFailed);
    }

    private enum ByteEnum : byte
    {
        A = 1,
        B = 2,
        C = 3
    }

    private enum UShortEnum : ushort
    {
        A = 1,
        B = 2
    }

    private enum UIntEnum : uint
    {
        A = 1,
        B = 2,
        C = 3
    }

    private enum IntEnum : int
    {
        A = 1,
        B = 2
    }

    private enum LongEnum : long
    {
        A = 1
    }

    private struct Pair
    {
        public int A;
        public short B;
    }
}
