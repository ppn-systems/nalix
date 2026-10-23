using Nalix.Abstractions.Networking.Packets;
// Copyright (c) 2025-2026 PPN Corporation. All rights reserved.
// Licensed under the Apache License, Version 2.0.

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
    public void ControlSerializeThenDeserializePreservesPublicState()
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
    public void ControlAfterResetForPoolCanBeReinitializedAndRoundTripped()
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
    public void ComputedOpCodeMatchesSerializedHeader()
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
    public void DirectiveSerializeThenDeserializePreservesAllFields()
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
    public void DirectiveAfterResetForPoolClearsAllPayloadFields()
    {
        Directive directive = new();
        directive.Initialize(
            type: ControlType.NOTICE,
            reason: ProtocolReason.INTERNAL_ERROR,
            action: ProtocolAdvice.RETRY,
            sequenceId: 456,
            flags: PacketFlags.COMPRESSED,
            controlFlags: ControlFlags.IS_TRANSIENT,
            arg0: 1000,
            arg1: 2000,
            arg2: 3);

        directive.ResetForPool();

        Assert.Equal(Directive.StaticOpCode, directive.Header.OpCode);
        Assert.Equal(0u, directive.Header.SequenceId);
        Assert.Equal(PacketFlags.SYSTEM, directive.Header.Flags);
        Assert.Equal(PacketPriority.HIGH, directive.Header.Priority);
        Assert.Equal(ControlType.NONE, directive.Type);
        Assert.Equal(ProtocolReason.NONE, directive.Reason);
        Assert.Equal(ProtocolAdvice.NONE, directive.Action);
        Assert.Equal(ControlFlags.NONE, directive.Control);
        Assert.Equal(0u, directive.Arg0);
        Assert.Equal(0u, directive.Arg1);
        Assert.Equal((ushort)0, directive.Arg2);
    }

    [Fact]
    public void DeserializeWhenBufferIsTooShortThrowsArgumentException()
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
    public void DeserializeWhenBufferIsEmptyThrowsArgumentException()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() => PacketRegistry.Deserialize([]));
        Assert.StartsWith("Raw packet data is too short to contain a valid header", ex.Message);
    }

    [Fact]
    public void DeserializeIntoExistingReferenceReturnsResolvedPacket()
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
    public void TryDeserializeIntoExistingReferenceReturnsFalseForUnknownOpCode()
    {
        byte[] buf = new byte[PacketConstants.HeaderSize];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(4), 0xFFFF);
        _ = new
        Control();
        bool ok = PacketRegistry.TryDeserialize(buf, out IPacket? _);

        Assert.False(ok);
    }

    [Fact]
    public void AllRegisteredPacketsHaveUniqueOpCodes()
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
    public void DifferentPacketTypesProduceDifferentOpCodes()
    {
        ushort a = Control.StaticOpCode;
        ushort c = Directive.StaticOpCode;

        Assert.NotEqual(a, c);
    }













}



