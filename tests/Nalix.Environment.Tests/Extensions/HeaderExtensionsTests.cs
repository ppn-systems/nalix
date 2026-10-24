// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Buffers.Binary;
using Nalix.Abstractions;
using Nalix.Abstractions.Networking.Packets;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.Extensions;
using Nalix.Environment.Extensions;
using Nalix.Framework.Extensions;

namespace Nalix.Environment.Tests.Extensions;

public sealed class HeaderExtensionsTests
{
    [Fact]
    public void AsHeaderRef_WhenBufferContainsValues_ReadsExpectedFields()
    {
        byte[] buffer = new byte[(int)PacketHeaderOffset.Region];
        const ushort opCode = 0x7788;
        const ushort sequence = 0x1234;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan((int)PacketHeaderOffset.OpCode), opCode);
        buffer[(int)PacketHeaderOffset.Flags] = (byte)PacketFlags.ENCRYPTED;
        buffer[(int)PacketHeaderOffset.Priority] = (byte)PacketPriority.HIGH;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan((int)PacketHeaderOffset.SequenceId), sequence);

        PacketHeader header = buffer.AsSpan().AsHeaderRef();
        Assert.Equal(opCode, header.OpCode);
        Assert.Equal(PacketFlags.ENCRYPTED, header.Flags);
        Assert.Equal(PacketPriority.HIGH, header.Priority);
        Assert.Equal(sequence, header.SequenceId);
    }

    [Fact]
    public void AsHeaderRef_WhenAssignedThroughRef_WritesAllFields()
    {
        byte[] buffer = new byte[(int)PacketHeaderOffset.Region];
        PacketHeader header = new()
        {
            OpCode = 0x7788,
            Flags = PacketFlags.NONE,
            Priority = PacketPriority.HIGH,
            SequenceId = 0x1234
        };

        buffer.AsSpan().AsHeaderRef() = header;

        PacketHeader read = buffer.AsSpan().AsHeaderRef();
        Assert.Equal(header.OpCode, read.OpCode);
        Assert.Equal(header.Flags, read.Flags);
        Assert.Equal(header.Priority, read.Priority);
        Assert.Equal(header.SequenceId, read.SequenceId);
    }

    [Fact]
    public void AsHeaderRef_WhenBufferTooSmall_ThrowsArgumentException()
    {
        byte[] small = new byte[2];

        Assert.Throws<ArgumentException>(() => small.AsSpan().AsHeaderRef());
        Assert.Throws<ArgumentException>(() => { small.AsSpan().AsHeaderRef() = default; });
    }
}
