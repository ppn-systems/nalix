// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using Nalix.Abstractions.Networking.Packets;

using Nalix.Abstractions.Networking.Protocols;
using Nalix.Abstractions.Primitives;
using Nalix.Codec.DataFrames;
using Nalix.Codec.ProtocolFrames;
using Nalix.Framework.Injection;
using Nalix.Framework.Memory.Objects;

namespace Nalix.Codec.Tests.DataFrames;

/// <summary>
/// Verifies packet registry round-trips and lookup behavior using the public registry pipeline.
/// </summary>
public sealed class PacketRegistryTests : IDisposable
{
    public PacketRegistryTests()
    {
        _ = InstanceManager.Instance.GetOrCreateInstance<ObjectPoolManager>();


        if (!PacketRegistry.IsBuilt)
        {
            PacketRegistry.Build();
        }
    }

    public void Dispose()
    {
    }

    [Fact]
    public void Deserialize_WhenControlWasSerialized_PreservesPublicState()
    {
        Control original = new();
        original.Initialize(
            type: ControlType.PING,
            sequenceId: 42,
            reasonCode: ProtocolReason.NONE);
        PacketHeader h = original.Header;
        h.OpCode = 0x0001;
        original.Header = h;

        byte[] bytes = original.Serialize();
        IPacket packet = PacketRegistry.Deserialize(bytes);

        Assert.NotNull(packet);

        Control result = Assert.IsType<Control>(packet);
        Assert.Equal(original.Header.OpCode, result.Header.OpCode);

        Assert.Equal(original.Header.SequenceId, result.Header.SequenceId);
        Assert.Equal(original.Type, result.Type);
        Assert.Equal(original.Reason, result.Reason);
        Assert.Equal(original.Header.Flags, result.Header.Flags);
        Assert.Equal(original.Header.Priority, result.Header.Priority);
    }



    [Fact]
    public void Deserialize_WhenControlWasResetAndReinitialized_RoundTripsThroughRegistry()
    {
        Control packet = new();
        packet.Initialize(ControlType.PONG, sequenceId: 99);
        packet.ResetForPool();

        packet.Initialize(ControlType.PING, sequenceId: 7);
        byte[] bytes = packet.Serialize();

        IPacket result = PacketRegistry.Deserialize(bytes);

        Control control = Assert.IsType<Control>(result);
        Assert.Equal(Control.StaticOpCode, control.Header.OpCode);
        Assert.Equal(7u, control.Header.SequenceId);
        Assert.Equal(ControlType.PING, control.Type);
    }


    [Fact]
    public void StaticOpCode_WhenPacketsAreInitialized_MatchesHeaderAndSerializedBytes()
    {
        Control control = new();
        control.Initialize(ControlType.PING, 0, PacketFlags.SYSTEM, ProtocolReason.NONE);
        Directive directive = new();
        directive.Initialize(ControlType.NOTICE, ProtocolReason.NONE, ProtocolAdvice.NONE, 0, PacketFlags.NONE, ControlFlags.NONE, 0, 0, 0);

        ushort regControl = Control.StaticOpCode;
        ushort regDirective = Directive.StaticOpCode;

        Assert.Equal(regControl, control.Header.OpCode);
        Assert.Equal(regDirective, directive.Header.OpCode);

        byte[] bytes = control.Serialize();
        ushort opCodeInBytes = System.Buffers.Binary.BinaryPrimitives
                                           .ReadUInt16LittleEndian(bytes.AsSpan((int)PacketHeaderOffset.OpCode));
        Assert.Equal(regControl, opCodeInBytes);
    }


    [Fact]
    public void Deserialize_WhenDirectiveWasSerialized_PreservesAllFields()
    {
        Directive original = new();
        original.Initialize(
            type: ControlType.NOTICE,
            reason: ProtocolReason.NONE,
            action: ProtocolAdvice.RETRY,
            sequenceId: 123,
            flags: PacketFlags.SYSTEM,
            controlFlags: ControlFlags.NONE,
            arg0: 0xDEAD,
            arg1: 0xBEEF,
            arg2: 0xFF);

        byte[] bytes = original.Serialize();
        IPacket packet = PacketRegistry.Deserialize(bytes);

        Directive result = Assert.IsType<Directive>(packet);

        Assert.Equal(original.Header.OpCode, result.Header.OpCode);

        Assert.Equal(original.Header.SequenceId, result.Header.SequenceId);
        Assert.Equal(original.Type, result.Type);
        Assert.Equal(original.Reason, result.Reason);
        Assert.Equal(original.Action, result.Action);
        Assert.Equal(original.Control, result.Control);
        Assert.Equal(original.Arg0, result.Arg0);
        Assert.Equal(original.Arg1, result.Arg1);
        Assert.Equal(original.Arg2, result.Arg2);
        Assert.Equal(original.Header.Priority, result.Header.Priority);
        Assert.Equal(original.Header.Flags, result.Header.Flags);
    }

    [Fact]
    public void Deserialize_WhenBufferIsThreeBytes_ThrowsArgumentException()
    {
        byte[] tooShort = new byte[3];

        ArgumentException ex = Assert.Throws<ArgumentException>(() => PacketRegistry.Deserialize(tooShort));
        Assert.StartsWith("Raw packet data is too short to contain a valid header", ex.Message);
    }

    public void DeserializeWhenOpCodeIsUnknownThrowsInvalidOperationException()
    {
        byte[] buf = new byte[PacketConstants.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0xFFFF);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PacketRegistry.Deserialize(buf));
        Assert.StartsWith("Cannot deserialize packet: OpCode", ex.Message);
    }

    [Fact]
    public void Deserialize_WhenBufferIsEmpty_ThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => PacketRegistry.Deserialize([]));
        Assert.StartsWith("Raw packet data is too short to contain a valid header", ex.Message);
    }

    [Fact]
    public void Deserialize_WhenBytesComeFromInitializedControl_ReturnsEqualControl()
    {
        Control original = new();
        original.Initialize(ControlType.PING, 7, PacketFlags.SYSTEM, ProtocolReason.NONE);

        byte[] bytes = original.Serialize();
        _ = new Control();

        IPacket packet = PacketRegistry.Deserialize(bytes);
        Control result = Assert.IsType<Control>(packet);

        Assert.Equal(original.Header.OpCode, result.Header.OpCode);

        Assert.Equal(original.Header.SequenceId, result.Header.SequenceId);
        Assert.Equal(original.Type, result.Type);
    }

    [Fact]
    public void TryDeserialize_WhenOpCodeIsUnknown_ReturnsFalse()
    {
        byte[] buf = new byte[PacketConstants.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0xFFFF);
        _ = new
        Control();
        bool ok = PacketRegistry.TryDeserialize(buf, out IPacket? _);

        Assert.False(ok);
    }

    [Fact]
    public void HeaderOpCode_WhenControlAndDirectiveAreInitialized_AreDistinct()
    {
        Control c = new();
        c.Initialize(ControlType.PING, 0, PacketFlags.SYSTEM, ProtocolReason.NONE);
        Directive d = new();
        d.Initialize(ControlType.NOTICE, ProtocolReason.NONE, ProtocolAdvice.NONE, 0, PacketFlags.NONE, ControlFlags.NONE, 0, 0, 0);

        ushort controlMagic = c.Header.OpCode;
        ushort directiveMagic = d.Header.OpCode;

        Assert.NotEqual(controlMagic, directiveMagic);
    }

    [Fact]
    public void StaticOpCode_WhenControlAndDirectiveAreCompared_AreDistinct()
    {
        ushort a = Control.StaticOpCode;
        ushort c = Directive.StaticOpCode;

        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Deserialize_WhenRegisteredPacketBytesAreProvided_ReturnsExpectedPacket()
    {
        Control packet = new();
        packet.Initialize(ControlType.PONG, 88, PacketFlags.NONE, ProtocolReason.NONE);
        byte[] bytes = packet.Serialize();

        IPacket deserialized = PacketRegistry.Deserialize(bytes);

        Control control = Assert.IsType<Control>(deserialized);
        Assert.Equal(packet.Type, control.Type);
        Assert.Equal(packet.Header.SequenceId, control.Header.SequenceId);
        Assert.True(PacketRegistry.DeserializerCount >= 1);
        Assert.True(PacketRegistry.IsRegistered<Control>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(PacketConstants.HeaderSize - 5)]
    public void Deserialize_WhenBufferIsShorterThanHeader_ThrowsArgumentException(int bufferLength)
    {
        byte[] raw = new byte[bufferLength];
        if (bufferLength >= PacketConstants.HeaderSize)
        {
            BitConverter.GetBytes(0xDEADBEEFu).CopyTo(raw, 0);
        }

        ArgumentException ex = Assert.Throws<ArgumentException>(() => PacketRegistry.Deserialize(raw));
        Assert.StartsWith("Raw packet data is too short to contain a valid header", ex.Message);
    }

    [Theory]
    [InlineData(PacketConstants.HeaderSize)]
    [InlineData(PacketConstants.HeaderSize + 10)]
    public void Deserialize_WhenOpCodeIsUnknown_ThrowsInvalidOperationException(int bufferLength)
    {
        byte[] raw = new byte[bufferLength];
        BitConverter.GetBytes(0xDEADBEEFu).CopyTo(raw, 0);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => PacketRegistry.Deserialize(raw));
        Assert.StartsWith("Cannot deserialize packet: OpCode 48879 is not registered", ex.Message);
    }
}
